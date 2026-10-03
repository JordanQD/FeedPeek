using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class NotificationCatchUpBoundaryTests
{
    [Fact]
    public async Task EmptyHeadsKeepTheLastNonEmptyAnchorAndDoNotAppendGapsForever()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var anchor = CreateItem(subscription, "anchor");
        var provider = new ScriptProvider(
            _ => Page(anchor, null, false),
            _ => new FeedPage([], "empty-1", true),
            request => request.Cursor == "empty-1"
                ? new FeedPage([], "empty-2", true)
                : throw Unexpected(request),
            request => request.Cursor == "empty-2"
                ? new FeedPage([], "empty-3", true)
                : throw Unexpected(request),
            _ => new FeedPage([], "empty-4", true),
            request => request.Cursor == "empty-3"
                ? Page(anchor, null, false)
                : throw Unexpected(request));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var paused = await poller.PollAsync(subscription, "generation-1");
        var recovered = await poller.PollAsync(subscription, "generation-1");

        Assert.False(paused.IsComplete);
        Assert.True(paused.CanInitializeBaseline);
        Assert.Equal(1, paused.PendingGaps);
        Assert.Equal("empty-3", paused.ContinuationCursor);
        Assert.True(recovered.IsComplete);
        Assert.True(recovered.CanInitializeBaseline);
        Assert.Equal(0, recovered.PendingGaps);
        Assert.Equal([null, null, "empty-1", "empty-2", null, "empty-3"],
            provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task InitialEmptyHeadDoesNotCreateAnUnboundedHistoricalGap()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var firstItem = CreateItem(subscription, "first");
        var provider = new ScriptProvider(
            _ => new FeedPage([], "initial-empty", true),
            _ => Page(firstItem, null, false));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        var empty = await poller.PollAsync(subscription, "generation-1");
        var next = await poller.PollAsync(subscription, "generation-1");

        Assert.True(empty.IsComplete);
        Assert.True(empty.CanInitializeBaseline);
        Assert.Equal(0, empty.PendingGaps);
        Assert.Null(empty.ContinuationCursor);
        Assert.Equal(["first"], next.Items.Select(item => item.Item.ItemId));
        Assert.Equal([null, null], provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task FailedGapDoesNotStarveAHealthyGapWithinTheRequestBudget()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var anchor = CreateItem(subscription, "anchor");
        var headOne = CreateItem(subscription, "head-1");
        var middle = CreateItem(subscription, "middle");
        var headTwo = CreateItem(subscription, "head-2");
        var recovered = CreateItem(subscription, "recovered");
        var provider = new ScriptProvider(
            _ => Page(anchor, null, false),
            _ => Page(headOne, "bad-gap", true),
            request => request.Cursor == "bad-gap"
                ? Page(middle, "bad-gap-next", true)
                : throw Unexpected(request),
            request => request.Cursor == "bad-gap-next"
                ? throw new InvalidOperationException("bad gap")
                : throw Unexpected(request),
            _ => Page(headTwo, "good-gap", true),
            request => request.Cursor == "good-gap"
                ? new FeedPage([recovered, headOne], null, false)
                : throw Unexpected(request),
            request => request.Cursor == "bad-gap-next"
                ? throw new InvalidOperationException("bad gap")
                : throw Unexpected(request));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var result = await poller.PollAsync(subscription, "generation-1");

        Assert.False(result.IsComplete);
        Assert.True(result.CanInitializeBaseline);
        Assert.Equal(1, result.PendingGaps);
        Assert.Equal("bad-gap-next", result.ContinuationCursor);
        Assert.Contains(result.Items, item => item.Item.ItemId == "head-2");
        Assert.Contains(result.Items, item => item.Item.ItemId == "recovered");
        Assert.Equal([null, null, "bad-gap", "bad-gap-next", null, "good-gap", "bad-gap-next"],
            provider.Requests.Select(request => request.Cursor));
        Assert.Contains("good-gap", provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task InitialHeadFailureStillThrowsBeforeAnyBaselineExists()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var provider = new ScriptProvider(
            _ => throw new InvalidOperationException("head failed"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            poller.PollAsync(subscription, "generation-1"));
    }

    [Fact]
    public async Task FailedHeadAfterAWorkingHeadReturnsPendingWithoutInitializingANewWatch()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var old = CreateItem(subscription, "old", clock.GetUtcNow().AddMinutes(-1)) with
        {
            CreatorKey = "creator",
        };
        var fresh = CreateItem(subscription, "fresh", clock.GetUtcNow().AddMinutes(1)) with
        {
            CreatorKey = "creator",
        };
        var provider = new ScriptProvider(
            _ => Page(old, null, false),
            _ => throw new InvalidOperationException("head failed after baseline"),
            _ => Page(old, null, false),
            _ => Page(fresh, null, false));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers, clock);
        using var tracker = new NotificationBaselineTracker();
        var feed = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId,
            subscription.FeedId, NotificationKind.Updates);
        tracker.Synchronize([new NotificationWatch(new NotificationWatchKey(feed), "source-generation")]);

        var baseline = await poller.PollAsync(subscription, "generation-1");
        Assert.True(baseline.CanInitializeBaseline);
        Assert.Empty(tracker.ObserveUpdates(feed, baseline.Items, baseline.ObservedAt,
            canInitializeBaseline: true));

        tracker.Synchronize([
            new NotificationWatch(new NotificationWatchKey(feed), "source-generation"),
            new NotificationWatch(new NotificationWatchKey(feed, "creator"), "creator-generation"),
        ]);
        var failed = await poller.PollAsync(subscription, "generation-1");

        Assert.False(failed.CanInitializeBaseline);
        Assert.False(failed.IsComplete);
        Assert.Contains(failed.Items, item => item.Item.ItemId == "old");
        Assert.Empty(tracker.ObserveUpdates(feed, failed.Items, failed.ObservedAt,
            canInitializeBaseline: false));

        var success = await poller.PollAsync(subscription, "generation-1");
        Assert.True(success.CanInitializeBaseline);
        Assert.Empty(tracker.ObserveUpdates(feed, success.Items, success.ObservedAt,
            canInitializeBaseline: true));
        poller.Acknowledge(success);

        clock.Advance(TimeSpan.FromMinutes(1));
        var update = await poller.PollAsync(subscription, "generation-1");
        var notifications = tracker.ObserveUpdates(feed, update.Items, update.ObservedAt,
            canInitializeBaseline: true);

        Assert.Equal(["fresh"], notifications.Select(item => item.ItemId));
    }

    [Fact]
    public async Task SuccessfulHeadWithFailedContinuationCanInitializeBaseline()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var anchor = CreateItem(subscription, "anchor", clock.GetUtcNow().AddMinutes(-1));
        var fresh = CreateItem(subscription, "fresh", clock.GetUtcNow().AddMinutes(1));
        var provider = new ScriptProvider(
            _ => Page(anchor, null, false),
            _ => Page(fresh, "failed-continuation", true),
            request => request.Cursor == "failed-continuation"
                ? throw new InvalidOperationException("continuation failed")
                : throw Unexpected(request));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers, clock);
        using var tracker = new NotificationBaselineTracker();
        var feed = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId,
            subscription.FeedId, NotificationKind.Updates);
        tracker.Synchronize([new NotificationWatch(new NotificationWatchKey(feed), "generation-1")]);

        var baseline = await poller.PollAsync(subscription, "generation-1");
        Assert.Empty(tracker.ObserveUpdates(feed, baseline.Items, baseline.ObservedAt,
            canInitializeBaseline: true));
        poller.Acknowledge(baseline);

        clock.Advance(TimeSpan.FromMinutes(1));
        var partial = await poller.PollAsync(subscription, "generation-1");
        var notifications = tracker.ObserveUpdates(feed, partial.Items, partial.ObservedAt,
            canInitializeBaseline: true);

        Assert.False(partial.IsComplete);
        Assert.True(partial.CanInitializeBaseline);
        Assert.Equal(1, partial.PendingGaps);
        Assert.Equal("failed-continuation", partial.ContinuationCursor);
        Assert.Equal(["fresh"], notifications.Select(item => item.ItemId));
    }

    [Fact]
    public async Task MaxItemsBatchesOneHeadAcross304PollsWithoutReplayingAcknowledgedIds()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var anchor = CreateItem(subscription, "anchor");
        var first = CreateItem(subscription, "first");
        var second = CreateItem(subscription, "second");
        var third = CreateItem(subscription, "third");
        var provider = new ScriptProvider(
            _ => new FeedPage([anchor], null, false, "v1"),
            _ => new FeedPage([first, second, third], null, false, "v2"),
            _ => new FeedPage([], null, false, "v2", NotModified: true),
            _ => new FeedPage([], null, false, "v2", NotModified: true));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers,
            options: new NotificationCatchUpOptions(MaxItems: 2));

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var firstBatch = await poller.PollAsync(subscription, "generation-1");
        poller.Acknowledge(firstBatch);
        var secondBatch = await poller.PollAsync(subscription, "generation-1");
        poller.Acknowledge(secondBatch);
        var empty = await poller.PollAsync(subscription, "generation-1");

        Assert.Equal(["first", "second"], firstBatch.Items.Select(item => item.Item.ItemId));
        Assert.Equal(["third"], secondBatch.Items.Select(item => item.Item.ItemId));
        Assert.Empty(empty.Items);
        Assert.True(firstBatch.CanInitializeBaseline);
        Assert.True(secondBatch.CanInitializeBaseline);
        Assert.Equal([null, null, null, null], provider.Requests.Select(request => request.Cursor));
    }

    private static InvalidOperationException Unexpected(FeedPageRequest request)
        => new($"Unexpected cursor {request.Cursor}");

    private static async Task<SqliteFeedStore> CreateStoreAsync()
    {
        var store = new SqliteFeedStore("Data Source=:memory:");
        await store.SaveSourceAndSubscriptionAsync(CreateSource(), CreateSubscription());
        return store;
    }

    private static SourceDefinition CreateSource() => new(
        "notification.boundary.test",
        "source",
        "Notification boundary source",
        null,
        [new FeedDefinition("updates", "Updates", "feed", true, true)],
        null);

    private static FeedSubscription CreateSubscription() => new(
        "subscription",
        "notification.boundary.test",
        "source",
        "updates",
        "Notification boundary source",
        "https://example.test/feed.xml",
        null,
        true,
        TimeSpan.FromMinutes(15),
        null);

    private static FeedItem CreateItem(FeedSubscription subscription, string itemId,
        DateTimeOffset? publishedAt = null) => new(
        subscription.ProviderId,
        subscription.SourceId,
        subscription.FeedId,
        itemId,
        itemId,
        null,
        "Author",
        null,
        $"https://example.test/{itemId}",
        publishedAt ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        null,
        null,
        null,
        null,
        null);

    private static FeedPage Page(FeedItem item, string? nextCursor, bool hasMore)
        => new([item], nextCursor, hasMore);

    private static FeedPage Page(IReadOnlyList<FeedItem> items, string? nextCursor, bool hasMore)
        => new(items, nextCursor, hasMore);

    private sealed class ScriptProvider(params Func<FeedPageRequest, FeedPage>[] scripts) : IFeedProvider
    {
        private int _index;

        public string ProviderId => "notification.boundary.test";

        public List<FeedPageRequest> Requests { get; } = [];

        public Task<FeedPage> GetPageAsync(FeedPageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var index = Interlocked.Increment(ref _index) - 1;
            if (index >= scripts.Length)
                throw new InvalidOperationException($"Unexpected request {request.Cursor}");
            return Task.FromResult(scripts[index](request));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
