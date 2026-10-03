using FeedPeek.Core.Notifications;

namespace FeedPeek.Core.Tests;

public sealed class NotificationDiskMapTests
{
    [Fact]
    public void ReadBatchReturnsAllEntriesInSequencePages()
    {
        using var map = new NotificationDiskMap<int, Payload>();
        using (map.BeginBatch())
        {
            for (var index = 0; index < 2000; index++)
                Assert.True(map.TryAdd(index, new($"item-{index}", index)));
        }

        var entries = new List<NotificationDiskEntry<int, Payload>>();
        var afterSequence = 0L;
        while (true)
        {
            var page = map.ReadBatch(256, afterSequence);
            if (page.Count == 0)
                break;
            entries.AddRange(page);
            afterSequence = page[^1].Sequence;
        }

        Assert.Equal(2000, entries.Count);
        Assert.Equal(Enumerable.Range(0, 2000), entries.Select(entry => entry.Key));
        Assert.Equal(2000, map.Count);
        Assert.All(entries, entry => Assert.Equal($"item-{entry.Key}", entry.Value.Text));
    }

    [Fact]
    public void SetRewritesValueWithoutChangingSequence()
    {
        using var map = new NotificationDiskMap<string, Payload>();
        Assert.True(map.TryAdd("key", new("before", 1)));
        var first = Assert.Single(map.ReadBatch());

        map.Set("key", new("after", 2));

        var rewritten = Assert.Single(map.ReadBatch());
        Assert.Equal(first.Sequence, rewritten.Sequence);
        Assert.Equal("key", rewritten.Key);
        Assert.Equal(new Payload("after", 2), rewritten.Value);
        Assert.False(map.TryAdd("key", new("duplicate", 3)));
    }

    [Fact]
    public void BatchCommitsOnDisposeAndTheMapCanBeReused()
    {
        using var map = new NotificationDiskMap<string, Payload>();

        using (map.BeginBatch())
            map.Set("first", new("one", 1));

        using (map.BeginBatch())
        {
            map.Set("second", new("two", 2));
            Assert.True(map.Remove("first"));
        }

        Assert.False(map.ContainsKey("first"));
        Assert.True(map.TryGetValue("second", out var value));
        Assert.Equal(new Payload("two", 2), value);
    }

    [Fact]
    public void JsonRecordsRoundTripAndScopesCanBeRemovedTogether()
    {
        using var map = new NotificationDiskMap<ScopedKey, Payload>(key => key.Scope);
        map.Set(new("same", "one"), new("first", 1));
        map.Set(new("same", "two"), new("second", 2));
        map.Set(new("other", "one"), new("other", 3));

        Assert.True(map.TryGetValue(new("same", "one"), out var value));
        Assert.Equal(new Payload("first", 1), value);

        map.RemoveScope("same");

        Assert.False(map.ContainsKey(new("same", "one")));
        Assert.False(map.ContainsKey(new("same", "two")));
        Assert.True(map.ContainsKey(new("other", "one")));
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void MissingKeysAndRepeatedRemovalsReturnFalse()
    {
        using var map = new NotificationDiskMap<string, Payload>();

        Assert.False(map.ContainsKey("missing"));
        Assert.False(map.TryGetValue("missing", out _));
        Assert.False(map.Remove("missing"));
        map.Set("present", new("value", 1));
        Assert.True(map.Remove("present"));
        Assert.False(map.Remove("present"));
        map.Clear();
        map.Clear();
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void FrozenSequenceBoundaryExcludesLaterArrivalsUntilTheNextScan()
    {
        using var map = new NotificationDiskMap<string, Payload>();
        map.Set("first", new("one", 1));
        var boundary = map.LastSequence;
        map.Set("second", new("two", 2));
        Assert.Equal(["first"], map.ReadBatch(throughSequence: boundary).Select(entry => entry.Key));
        Assert.Equal(["second"], map.ReadBatch(afterSequence: boundary).Select(entry => entry.Key));
        Assert.True(map.LastSequence > boundary);
    }

    [Fact]
    public void DisposeMakesMapUnavailableAndTemporaryDatabaseDoesNotSurvive()
    {
        var map = new NotificationDiskMap<string, Payload>();
        map.Set("key", new("value", 1));
        map.Dispose();
        map.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = map.Count);
        Assert.Throws<ObjectDisposedException>(() => map.Set("key", new("again", 2)));

        using var fresh = new NotificationDiskMap<string, Payload>();
        Assert.Equal(0, fresh.Count);
        Assert.Empty(fresh.ReadBatch());
    }

    private sealed record Payload(string Text, int Number);

    private readonly record struct ScopedKey(string Scope, string Id);
}
