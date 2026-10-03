using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class NotificationUpdatePollerTests
{
    [Fact]
    public async Task PollFollowsOpaquePagesUntilThePreviousHeadAndDoesNotRepeatAcknowledgedItems()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh") with
        {
            PublishedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z"),
        };
        var provider = new ScriptProvider(
            _ => Page(old, "seed", hasMore: true),
            _ => Page(fresh, "catch-up", hasMore: true),
            request => request.Cursor == "catch-up"
                ? Page(old, null, hasMore: false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        var baseline = await poller.PollAsync(subscription, "generation-1");
        poller.Acknowledge(baseline);
        var result = await poller.PollAsync(subscription, "generation-1");

        Assert.True(result.IsComplete);
        Assert.Equal(0, result.PendingGaps);
        Assert.Null(result.ContinuationCursor);
        Assert.Equal(["fresh"], result.Items.Select(item => item.Item.ItemId));
        Assert.Equal([null, null, "catch-up"], provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task RequestLimitRetainsOlderGapWhenASecondHeadArrivesAndEachNewItemIsObservedOnce()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var old = CreateItem(subscription, "old", clock.GetUtcNow().AddMinutes(-1));
        var middle = CreateItem(subscription, "middle", clock.GetUtcNow().AddMinutes(1));
        var firstHead = CreateItem(subscription, "head-1", clock.GetUtcNow().AddMinutes(1));
        var secondHead = CreateItem(subscription, "head-2", clock.GetUtcNow().AddMinutes(2));
        var gapTwo = CreateItem(subscription, "gap-2", clock.GetUtcNow().AddMinutes(3));
        var provider = new ScriptProvider(
            _ => Page(old, null, hasMore: false),
            _ => Page(firstHead, "gap-1", hasMore: true),
            request => request.Cursor == "gap-1"
                ? Page(middle, "gap-1b", hasMore: true)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"),
            _ => Page(secondHead, "gap-2", hasMore: true),
            request => request.Cursor == "gap-1b"
                ? Page(old, null, hasMore: false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"),
            _ => new FeedPage([], null, false, NotModified: true),
            request => request.Cursor == "gap-2"
                ? Page([gapTwo, firstHead], null, hasMore: false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers, clock,
            new NotificationCatchUpOptions(MaxRequests: 2));
        var feed = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId,
            subscription.FeedId, NotificationKind.Updates);
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([new NotificationWatch(new NotificationWatchKey(feed), "account-a")]);

        var baseline = await poller.PollAsync(subscription, "account-a");
        Assert.Empty(tracker.ObserveUpdates(feed, baseline.Items, baseline.ObservedAt));
        poller.Acknowledge(baseline);

        clock.Advance(TimeSpan.FromMinutes(1));
        var firstCatchUp = await poller.PollAsync(subscription, "account-a");
        var firstNotifications = tracker.ObserveUpdates(feed, firstCatchUp.Items, firstCatchUp.ObservedAt);
        poller.Acknowledge(firstCatchUp);
        Assert.Equal(["head-1", "middle"], firstNotifications.Select(item => item.ItemId));
        Assert.False(firstCatchUp.IsComplete);
        Assert.Equal("gap-1b", firstCatchUp.ContinuationCursor);
        Assert.Equal(1, firstCatchUp.PendingGaps);

        clock.Advance(TimeSpan.FromMinutes(1));
        var secondCatchUp = await poller.PollAsync(subscription, "account-a");
        var secondNotifications = tracker.ObserveUpdates(feed, secondCatchUp.Items, secondCatchUp.ObservedAt);
        poller.Acknowledge(secondCatchUp);

        Assert.False(secondCatchUp.IsComplete);
        Assert.Equal("gap-2", secondCatchUp.ContinuationCursor);
        Assert.Equal(1, secondCatchUp.PendingGaps);
        Assert.Equal(["head-2"], secondNotifications.Select(item => item.ItemId));

        clock.Advance(TimeSpan.FromMinutes(1));
        var finalCatchUp = await poller.PollAsync(subscription, "account-a");
        var finalNotifications = tracker.ObserveUpdates(feed, finalCatchUp.Items, finalCatchUp.ObservedAt);

        Assert.True(finalCatchUp.IsComplete);
        Assert.Equal(["gap-2"], finalNotifications.Select(item => item.ItemId));
        Assert.Equal(["head-1", "middle", "head-2", "gap-2"],
            firstNotifications.Concat(secondNotifications).Concat(finalNotifications)
                .Select(item => item.ItemId));
        Assert.Equal([null, null, "gap-1", null, "gap-1b", null, "gap-2"],
            provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task FailedContinuationRetainsItsCursorAndRetryDoesNotDuplicateStagedItems()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh") with
        {
            PublishedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z"),
        };
        var provider = new ScriptProvider(
            _ => Page(old, null, hasMore: false),
            _ => Page(fresh, "retry", hasMore: true),
            request => request.Cursor == "retry"
                ? throw new InvalidOperationException("temporary continuation failure")
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"),
            _ => Page(fresh, "retry", hasMore: true),
            request => request.Cursor == "retry"
                ? Page(old, null, hasMore: false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var failed = await poller.PollAsync(subscription, "generation-1");
        poller.Acknowledge(failed);

        Assert.False(failed.IsComplete);
        Assert.Equal("retry", failed.ContinuationCursor);
        Assert.Contains("temporary continuation failure", failed.Error);

        var retried = await poller.PollAsync(subscription, "generation-1");

        Assert.True(retried.IsComplete);
        Assert.Empty(retried.Items);
        Assert.Equal("retry", provider.Requests[^1].Cursor);
    }

    [Fact]
    public async Task NotModifiedHeadStillAllowsExistingCatchUpToContinue()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh") with
        {
            PublishedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z"),
        };
        var provider = new ScriptProvider(
            _ => new FeedPage([old], null, false, "v1"),
            _ => new FeedPage([fresh], "gap", true, "v2"),
            request => request.Cursor == "gap"
                ? throw new InvalidOperationException("temporary continuation failure")
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"),
            _ => new FeedPage([], null, false, "v2", NotModified: true),
            request => request.Cursor == "gap"
                ? Page(old, null, hasMore: false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var failed = await poller.PollAsync(subscription, "generation-1");
        poller.Acknowledge(failed);
        var recovered = await poller.PollAsync(subscription, "generation-1");

        Assert.True(recovered.IsComplete);
        Assert.Null(recovered.Error);
        Assert.Equal([null, null, "gap", null, "gap"], provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task RepeatedCursorRetainsGapInsteadOfDeclaringCompletion()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh");
        var provider = new ScriptProvider(
            _ => Page(old, null, false),
            _ => Page(fresh, "repeat", true),
            request => request.Cursor == "repeat"
                ? Page(fresh, "repeat", true)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var result = await poller.PollAsync(subscription, "generation-1");

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.PendingGaps);
        Assert.Equal("repeat", result.ContinuationCursor);
        Assert.Contains("Repeated notification cursor", result.Error);
    }

    [Fact]
    public async Task TwoEmptyContinuationPagesRetainTheLastKnownCursor()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh");
        var middle = CreateItem(subscription, "middle");
        var provider = new ScriptProvider(
            _ => Page(old, null, false),
            _ => Page(fresh, "empty-1", true),
            request => request.Cursor == "empty-1"
                ? new FeedPage([], "empty-2", true)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"),
            request => request.Cursor == "empty-2"
                ? new FeedPage([], "empty-3", true)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"),
            _ => new FeedPage([], null, false, NotModified: true),
            request => request.Cursor == "empty-3"
                ? new FeedPage([middle, old], null, false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var result = await poller.PollAsync(subscription, "generation-1");

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.PendingGaps);
        Assert.Equal("empty-3", result.ContinuationCursor);
        Assert.Contains("no item progress", result.Error);
        poller.Acknowledge(result);

        var recovered = await poller.PollAsync(subscription, "generation-1");

        Assert.True(recovered.IsComplete);
        Assert.Null(recovered.Error);
        Assert.Equal(["middle"], recovered.Items.Select(item => item.Item.ItemId));
    }

    [Fact]
    public async Task ForegroundPollingCursorIsNotConsumedOrOverwritten()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        await store.SavePollingStateAsync(new FeedPollingState(
            subscription.SubscriptionId, "foreground-etag", null, "foreground-cursor", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        var provider = new ScriptProvider(_ => Page(CreateItem(subscription, "item"), null, false));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        await poller.PollAsync(subscription, "generation-1");

        var state = await store.GetPollingStateAsync(subscription.SubscriptionId);
        Assert.Equal("foreground-cursor", state!.NextCursor);
        Assert.Equal("foreground-etag", state.ETag);
        Assert.Null(provider.Requests[0].Cursor);
        Assert.Null(provider.Requests[0].ETag);
    }

    [Fact]
    public async Task MaxItemsStopsCatchUpWithoutDroppingTheContinuation()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh");
        var provider = new ScriptProvider(
            _ => Page(old, null, false),
            _ => Page(fresh, "max-items-gap", true),
            _ => new FeedPage([], null, false, NotModified: true),
            request => request.Cursor == "max-items-gap"
                ? Page(old, null, false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers,
            options: new NotificationCatchUpOptions(MaxItems: 1));

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var limited = await poller.PollAsync(subscription, "generation-1");

        Assert.False(limited.IsComplete);
        Assert.Equal("max-items-gap", limited.ContinuationCursor);
        Assert.Equal(1, limited.PendingGaps);

        var recovered = await poller.PollAsync(subscription, "generation-1");

        Assert.True(recovered.IsComplete);
        Assert.Null(recovered.Error);
    }

    [Fact]
    public async Task RepeatedHeadDoesNotConsumeMaxItemsBudgetForCatchUp()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh");
        var provider = new ScriptProvider(
            _ => Page(old, null, false),
            _ => Page(fresh, "repeated-head-gap", true),
            _ => Page(fresh, "repeated-head-gap", true),
            request => request.Cursor == "repeated-head-gap"
                ? Page(old, null, false)
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers,
            options: new NotificationCatchUpOptions(MaxItems: 1));

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var limited = await poller.PollAsync(subscription, "generation-1");
        var repeated = await poller.PollAsync(subscription, "generation-1");

        Assert.False(limited.IsComplete);
        Assert.Equal("repeated-head-gap", limited.ContinuationCursor);
        Assert.True(repeated.IsComplete);
        Assert.Null(repeated.Error);
        Assert.Equal([null, null, null, "repeated-head-gap"],
            provider.Requests.Select(request => request.Cursor));
    }

    [Fact]
    public async Task DeadlineDuringContinuationRetainsTheFailedCursor()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh");
        var provider = new ScriptProvider(
            (_, _) => Task.FromResult(Page(old, null, false)),
            (_, _) => Task.FromResult(Page(fresh, "deadline-gap", true)),
            async (request, cancellationToken) =>
            {
                Assert.Equal("deadline-gap", request.Cursor);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Page(old, null, false);
            });
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers,
            options: new NotificationCatchUpOptions(Timeout: TimeSpan.FromMilliseconds(100)));

        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        var result = await poller.PollAsync(subscription, "generation-1");

        Assert.False(result.IsComplete);
        Assert.Equal("deadline-gap", result.ContinuationCursor);
        Assert.Contains("time limit", result.Error);
    }

    [Fact]
    public async Task CancellationDuringContinuationRetainsTheFailedCursor()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var old = CreateItem(subscription, "old");
        var fresh = CreateItem(subscription, "fresh");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptProvider(
            (_, _) => Task.FromResult(Page(old, null, false)),
            (_, _) => Task.FromResult(Page(fresh, "cancel-gap", true)),
            async (request, cancellationToken) =>
            {
                Assert.Equal("cancel-gap", request.Cursor);
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Page(old, null, false);
            },
            (_, _) => Task.FromResult(new FeedPage([], null, false, NotModified: true)),
            async (request, _) => request.Cursor == "cancel-gap"
                ? await Task.FromResult(Page(old, null, false))
                : throw new InvalidOperationException($"Unexpected cursor {request.Cursor}"));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);
        poller.Acknowledge(await poller.PollAsync(subscription, "generation-1"));
        using var cancellation = new CancellationTokenSource();

        var pending = poller.PollAsync(subscription, "generation-1", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        var recovered = await poller.PollAsync(subscription, "generation-1");

        Assert.True(recovered.IsComplete);
        Assert.Null(recovered.Error);
        Assert.Equal("cancel-gap", provider.Requests[^1].Cursor);
    }

    [Fact]
    public async Task FailedInitialHeadDoesNotEstablishABaseline()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var baselineItem = CreateItem(subscription, "baseline");
        var provider = new ScriptProvider(
            _ => throw new InvalidOperationException("initial head failed"),
            _ => Page(baselineItem, null, false));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            poller.PollAsync(subscription, "generation-1"));
        var second = await poller.PollAsync(subscription, "generation-1");

        Assert.True(second.IsComplete);
        Assert.Equal(["baseline"], second.Items.Select(item => item.Item.ItemId));
    }

    [Fact]
    public async Task GenerationChangeStartsASeparateSilentBaseline()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var firstItem = CreateItem(subscription, "first");
        var secondItem = CreateItem(subscription, "second") with
        {
            PublishedAt = DateTimeOffset.Parse("2026-01-01T00:01:00Z"),
        };
        var provider = new ScriptProvider(
            _ => Page(firstItem, null, false),
            _ => Page(secondItem, null, false));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var tracker = new NotificationBaselineTracker();
        var feed = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId,
            subscription.FeedId, NotificationKind.Updates);

        var first = await poller.PollAsync(subscription, "account-a");
        tracker.Synchronize([new NotificationWatch(new NotificationWatchKey(feed), "account-a")]);
        Assert.Empty(tracker.ObserveUpdates(feed, first.Items, clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await poller.PollAsync(subscription, "account-b");
        tracker.Synchronize([new NotificationWatch(new NotificationWatchKey(feed), "account-b")]);

        Assert.Equal(["second"], second.Items.Select(item => item.Item.ItemId));
        Assert.Empty(tracker.ObserveUpdates(feed, second.Items, clock.GetUtcNow()));
    }

    [Fact]
    public async Task CancellationBeforeFirstSuccessLeavesTrackerReadyForNextPoll()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var providerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptProvider(
            async (_, cancellationToken) =>
            {
                providerEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Page(CreateItem(subscription, "never"), null, false);
            },
            (_, _) => Task.FromResult(Page(CreateItem(subscription, "baseline"), null, false)));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);
        using var cancellation = new CancellationTokenSource();

        var pending = poller.PollAsync(subscription, "generation-1", cancellation.Token);
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        var next = await poller.PollAsync(subscription, "generation-1");

        Assert.Equal(["baseline"], next.Items.Select(item => item.Item.ItemId));
        Assert.True(next.IsComplete);
    }

    [Fact]
    public async Task UnacknowledgedItemsRemainPendingForTheNextPoll()
    {
        await using var store = await CreateStoreAsync();
        var subscription = CreateSubscription();
        var item = CreateItem(subscription, "pending");
        var provider = new ScriptProvider(
            _ => Page(item, null, false),
            _ => Page(item, null, false));
        using var providers = new FeedProviderRegistry([provider]);
        using var poller = new NotificationUpdatePoller(store, providers);

        var first = await poller.PollAsync(subscription, "generation-1");
        var second = await poller.PollAsync(subscription, "generation-1");

        Assert.Equal(["pending"], first.Items.Select(observation => observation.Item.ItemId));
        Assert.Equal(["pending"], second.Items.Select(observation => observation.Item.ItemId));
    }

    private static async Task<SqliteFeedStore> CreateStoreAsync()
    {
        var store = new SqliteFeedStore("Data Source=:memory:");
        var subscription = CreateSubscription();
        await store.SaveSourceAndSubscriptionAsync(CreateSource(), subscription);
        return store;
    }

    private static SourceDefinition CreateSource() => new(
        "notification.update.poll.test",
        "source",
        "Notification test source",
        null,
        [new FeedDefinition("updates", "Updates", "feed", true, true)],
        null);

    private static FeedSubscription CreateSubscription() => new(
        "subscription",
        "notification.update.poll.test",
        "source",
        "updates",
        "Notification test source",
        "https://example.test/feed.xml",
        null,
        true,
        TimeSpan.FromMinutes(15),
        null);

    private static FeedItem CreateItem(FeedSubscription subscription, string itemId)
        => CreateItem(subscription, itemId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static FeedItem CreateItem(FeedSubscription subscription, string itemId,
        DateTimeOffset publishedAt) => new(
        subscription.ProviderId,
        subscription.SourceId,
        subscription.FeedId,
        itemId,
        itemId,
        null,
        "Author",
        null,
        $"https://example.test/{itemId}",
        publishedAt,
        null,
        null,
        null,
        null,
        null);

    private static FeedPage Page(FeedItem item, string? nextCursor, bool hasMore)
        => new([item], nextCursor, hasMore);

    private static FeedPage Page(IReadOnlyList<FeedItem> items, string? nextCursor, bool hasMore)
        => new(items, nextCursor, hasMore);

    private sealed class ScriptProvider : IFeedProvider
    {
        private readonly Func<FeedPageRequest, CancellationToken, Task<FeedPage>>[] _scripts;
        private int _index;

        public string ProviderId => "notification.update.poll.test";

        public List<FeedPageRequest> Requests { get; } = [];

        public ScriptProvider(params Func<FeedPageRequest, FeedPage>[] scripts)
        {
            _scripts = scripts.Select(script =>
                (Func<FeedPageRequest, CancellationToken, Task<FeedPage>>)((request, _) =>
                    Task.FromResult(script(request)))).ToArray();
        }

        public ScriptProvider(params Func<FeedPageRequest, CancellationToken, Task<FeedPage>>[] scripts)
        {
            _scripts = scripts;
        }

        public async Task<FeedPage> GetPageAsync(FeedPageRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Interlocked.Increment(ref _index) - 1;
            var script = _scripts[Math.Min(index, _scripts.Length - 1)];
            return await script(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
