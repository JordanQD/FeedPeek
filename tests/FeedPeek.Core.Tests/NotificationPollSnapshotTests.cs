using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class NotificationPollSnapshotTests
{
    [Fact]
    public async Task SharedStore_304UsesForegroundSnapshotAndBaselineOnlyReportsNewItem()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var subscription = CreateSubscription("source-a", "snapshot-subscription");
        await store.SaveSourceAndSubscriptionAsync(CreateSource("source-a"), subscription);

        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        var first = CreateItem(subscription, "first");
        var second = CreateItem(subscription, "second");
        using var providers = new FeedProviderRegistry([
            new SequenceProvider(
                new FeedPage([first], null, false, "v1"),
                new FeedPage([first, second], null, false, "v2"),
                new FeedPage([], null, false, "v2", null, NotModified: true))
        ]);
        var background = new FeedPollingService(store, providers, time);
        var foreground = new FeedPollingService(store, providers, time);

        var baseline = await background.PollAsync(subscription);
        var firstSeenAt = (await store.GetItemsAsync(new FeedQuery(
            ProviderId: subscription.ProviderId,
            SourceId: subscription.SourceId,
            FeedId: subscription.FeedId))).Single().FirstSeenAt;
        Assert.Equal(time.GetUtcNow(), firstSeenAt);

        var feed = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId,
            subscription.FeedId, NotificationKind.Updates);
        var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([new NotificationWatch(new NotificationWatchKey(feed), "test-generation")]);
        Assert.Empty(tracker.ObserveUpdates(feed,
            [new NotificationUpdateObservation(first, firstSeenAt)], time.GetUtcNow()));

        time.Advance(TimeSpan.FromMinutes(1));
        var foregroundPoll = await foreground.PollAsync(subscription);
        var afterForeground = await background.PollAsync(subscription);

        Assert.True(baseline.InitializedBaseline);
        Assert.Empty(baseline.NewItems);
        Assert.False(foregroundPoll.NotModified);
        Assert.True(afterForeground.NotModified);
        Assert.Empty(afterForeground.NewItems);
        Assert.Equal(["first", "second"], afterForeground.NotificationItems.Select(item => item.ItemId));

        var stored = await store.GetItemsAsync(new FeedQuery(
            ProviderId: subscription.ProviderId,
            SourceId: subscription.SourceId,
            FeedId: subscription.FeedId));
        var observations = stored
            .Select(item => new NotificationUpdateObservation(item.Item, item.FirstSeenAt))
            .ToArray();
        var notifications = tracker.ObserveUpdates(feed, observations, time.GetUtcNow());

        Assert.Equal(["second"], notifications.Select(item => item.ItemId));
    }

    [Fact]
    public async Task SameFeedPollsAreSerialized_AndCancelledWaitDoesNotBlockLaterPoll()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var sourceId = "coordinator-" + Guid.NewGuid().ToString("N");
        var subscription = CreateSubscription(sourceId, "coordinator-subscription");
        await store.SaveSourceAndSubscriptionAsync(CreateSource(sourceId), subscription);

        var provider = new BlockingSequenceProvider(
            CreateItem(subscription, "baseline"),
            CreateItem(subscription, "after-wait"));
        using var providers = new FeedProviderRegistry([provider]);
        var firstService = new FeedPollingService(store, providers);
        var secondService = new FeedPollingService(store, providers);

        var firstPoll = firstService.PollAsync(subscription);
        await provider.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var cancelledWait = secondService.PollAsync(subscription, cancellation.Token);
        await Task.Delay(50);
        Assert.False(cancelledWait.IsCompleted);
        Assert.Equal(1, provider.CallCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledWait);
        Assert.Equal(1, provider.CallCount);

        provider.ReleaseFirstCall();
        var first = await firstPoll;
        Assert.True(first.InitializedBaseline);

        var later = await secondService.PollAsync(subscription);

        Assert.False(later.InitializedBaseline);
        Assert.Equal("after-wait", Assert.Single(later.NewItems).ItemId);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public void SnapshotCache_DoesNotCrossSourcesWithSameSubscriptionId_AndRemoveSourceCleansIt()
    {
        var subscriptionA = CreateSubscription("source-a", "same-subscription");
        var subscriptionB = CreateSubscription("source-b", "same-subscription");
        var itemA = CreateItem(subscriptionA, "item-a");

        FeedPollSnapshotCache.Record(subscriptionA, [itemA]);

        Assert.Empty(FeedPollSnapshotCache.Read(subscriptionB));
        Assert.Equal(["item-a"], FeedPollSnapshotCache.Read(subscriptionA).Select(item => item.ItemId));

        FeedPollSnapshotCache.RemoveSource(subscriptionA.ProviderId, subscriptionA.SourceId);

        Assert.Empty(FeedPollSnapshotCache.Read(subscriptionA));
    }

    private static SourceDefinition CreateSource(string sourceId) => new(
        "notification.poll.test",
        sourceId,
        sourceId,
        null,
        [new FeedDefinition("updates", "Updates", "feed", false, true)],
        null);

    private static FeedSubscription CreateSubscription(string sourceId, string subscriptionId) => new(
        subscriptionId,
        "notification.poll.test",
        sourceId,
        "updates",
        sourceId,
        "https://example.test/feed.xml",
        null,
        true,
        TimeSpan.FromMinutes(15),
        null);

    private static FeedItem CreateItem(FeedSubscription subscription, string itemId) => new(
        subscription.ProviderId,
        subscription.SourceId,
        subscription.FeedId,
        itemId,
        itemId,
        null,
        "Author",
        null,
        $"https://example.test/{itemId}",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        null,
        null,
        null,
        null,
        null);

    private sealed class SequenceProvider(params FeedPage[] pages) : IFeedProvider
    {
        private int _index;

        public string ProviderId => "notification.poll.test";

        public Task<FeedPage> GetPageAsync(
            FeedPageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[Math.Min(_index, pages.Length - 1)];
            _index++;
            return Task.FromResult(page);
        }
    }

    private sealed class BlockingSequenceProvider(FeedItem first, FeedItem second) : IFeedProvider
    {
        private readonly TaskCompletionSource<bool> _firstCallEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public string ProviderId => "notification.poll.test";

        public TaskCompletionSource<bool> FirstCallEntered => _firstCallEntered;

        public int CallCount => Volatile.Read(ref _callCount);

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult(true);

        public async Task<FeedPage> GetPageAsync(
            FeedPageRequest request,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _firstCallEntered.TrySetResult(true);
                await _releaseFirstCall.Task.ConfigureAwait(false);
                return new FeedPage([first], null, false, "v1");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new FeedPage([first, second], null, false, "v2");
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
