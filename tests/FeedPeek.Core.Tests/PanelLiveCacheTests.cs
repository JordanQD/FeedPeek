using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.Services;

namespace FeedPeek.Core.Tests;

public sealed class PanelLiveCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FeedPeek-LiveCache-" + Guid.NewGuid().ToString("N"));
    private readonly FeedSubscription _subscription = new("live-sub", "provider", "source", "live", "Source", "https://example.com/room",
        "default", true, TimeSpan.FromMinutes(15), new Dictionary<string, string> { ["roomIds"] = "1" });
    private static PluginLiveItem Item(string key, string name = "Streamer")
        => new(key, name, "https://example.com/" + key, "Live title", "https://example.com/avatar.png", null, 12,
            DateTimeOffset.Parse("2026-10-03T00:00:00Z"), new Dictionary<string, string> { ["room"] = key });
    private string Context(FeedSubscription? subscription = null, string account = "account-a", string version = "1.0")
        => PanelLiveCache.ContextKey(subscription ?? _subscription, version, account);

    [Fact]
    public async Task SnapshotSurvivesCacheInstanceRecreationAndKeepsEmptySuccess()
    {
        var cache = new PanelLiveCache(_directory);
        await cache.RecordAsync(_subscription, Context(), new([Item("a")], true));
        var restored = await new PanelLiveCache(_directory).ReadAsync(_subscription, Context());
        var item = Assert.Single(restored!);
        Assert.Equal("Streamer", item.DisplayName);
        Assert.Equal("https://example.com/avatar.png", item.AvatarUrl);
        Assert.Equal("a", item.Metadata!["room"]);
        await cache.RecordAsync(_subscription, Context(), new([], true));
        Assert.Empty((await new PanelLiveCache(_directory).ReadAsync(_subscription, Context()))!);
    }

    [Fact]
    public async Task ChangedAccountPluginOrSourceConfigurationCannotReadOldSnapshot()
    {
        var cache = new PanelLiveCache(_directory);
        await cache.RecordAsync(_subscription, Context(), new([Item("a")], true));
        Assert.Null(await cache.ReadAsync(_subscription, Context(account: "account-b")));
        Assert.Null(await cache.ReadAsync(_subscription, Context(version: "2.0")));
        var changed = _subscription with { Endpoint = "https://example.com/new", Options = new Dictionary<string, string> { ["roomIds"] = "2" } };
        Assert.Null(await cache.ReadAsync(changed, Context(changed)));
        Assert.Null(await cache.ReadAsync(_subscription with { IsEnabled = false, ProviderId = "other" }, Context()));
    }

    [Fact]
    public async Task IncompletePageUpdatesObservedRoomsAndRetainsUnobservedRooms()
    {
        var cache = new PanelLiveCache(_directory);
        await cache.RecordAsync(_subscription, Context(), new([Item("a"), Item("b")], true));
        var merged = await cache.RecordAsync(_subscription, Context(), new([Item("a", "Renamed")], false));
        Assert.Equal(new[] { "a", "b" }, merged.Select(item => item.CreatorKey));
        Assert.Equal("Renamed", merged[0].DisplayName);
        Assert.Equal(2, (await new PanelLiveCache(_directory).ReadAsync(_subscription, Context()))!.Count);
        await cache.RecordAsync(_subscription, Context(), new([Item("a")], true));
        Assert.Single((await cache.ReadAsync(_subscription, Context()))!);
    }

    [Fact]
    public async Task FirstIncompleteEmptyResultDoesNotBecomeAnOfflineSnapshot()
    {
        var cache = new PanelLiveCache(_directory);
        Assert.Empty(await cache.RecordAsync(_subscription, Context(), new([], false)));
        Assert.Null(await cache.ReadAsync(_subscription, Context()));
    }

    [Fact]
    public async Task CancelledRefreshDoesNotOverwriteLastSuccessfulSnapshot()
    {
        var cache = new PanelLiveCache(_directory);
        await cache.RecordAsync(_subscription, Context(), new([Item("a")], true));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RecordAsync(_subscription, Context(), new([], true), cancelled.Token));
        Assert.Single((await cache.ReadAsync(_subscription, Context()))!);
    }

    [Fact]
    public async Task CorruptFileFallsBackToFreshSnapshot()
    {
        var cache = new PanelLiveCache(_directory);
        await cache.RecordAsync(_subscription, Context(), new([Item("a")], true));
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        await File.WriteAllTextAsync(path, "{broken");
        Assert.Null(await cache.ReadAsync(_subscription, Context()));
        await cache.RecordAsync(_subscription, Context(), new([Item("b")], true));
        Assert.Equal("b", Assert.Single((await cache.ReadAsync(_subscription, Context()))!).CreatorKey);
    }

    [Fact]
    public async Task SourceRemovalKeepsOtherProviderSnapshots()
    {
        var cache = new PanelLiveCache(_directory);
        var other = _subscription with { ProviderId = "other", SubscriptionId = "other-sub" };
        await cache.RecordAsync(_subscription, Context(), new([Item("a")], true));
        await cache.RecordAsync(other, Context(other), new([Item("b")], true));
        await cache.RemoveSourceAsync(_subscription.ProviderId, _subscription.SourceId);
        Assert.Null(await cache.ReadAsync(_subscription, Context()));
        Assert.Single((await cache.ReadAsync(other, Context(other)))!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
