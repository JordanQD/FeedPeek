using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;

namespace FeedPeek.Core.Tests;

public sealed class NotificationBaselineTrackerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    [Fact]
    public void NewTracker_SilentlyBaselinesExistingUpdatesAndLiveItems()
    {
        var updates = UpdatesFeed();
        var live = LiveFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(updates), Watch(live)]);

        var update = Update("old", "creator", T0);
        var liveItem = new NotificationLiveObservation("room-1", "creator");

        Assert.Empty(tracker.ObserveUpdates(updates, [new(update, T0)], T0));
        Assert.Empty(tracker.ObserveLive(live, [liveItem]));
    }

    [Fact]
    public void Updates_OnlyItemsFirstSeenAfterInitialSuccessfulObservationCanNotify()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);

        var existing = Update("existing", "creator", T0.AddMinutes(-10));
        Assert.Empty(tracker.ObserveUpdates(feed, [new(existing, T0.AddMinutes(-10))], T0));

        var insertedBeforeBaseline = Update("inserted-before-baseline", "creator", T0.AddMinutes(-1));
        Assert.Empty(tracker.ObserveUpdates(
            feed,
            [new(existing, T0.AddMinutes(-10)), new(insertedBeforeBaseline, T0.AddMinutes(-1))],
            T0.AddMinutes(1)));

        var insertedAfterBaseline = Update("inserted-after-baseline", "creator", T0.AddMinutes(2));
        var notifications = tracker.ObserveUpdates(
            feed,
            [
                new(existing, T0.AddMinutes(-10)),
                new(insertedBeforeBaseline, T0.AddMinutes(-1)),
                new(insertedAfterBaseline, T0.AddMinutes(2)),
            ],
            T0.AddMinutes(3));

        Assert.Equal(["inserted-after-baseline"], notifications.Select(item => item.ItemId));
    }

    [Fact]
    public void Updates_SuppressItemsPublishedBeforeBaselineEvenWhenTheyArriveDuringCatchup()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);
        Assert.Empty(tracker.ObserveUpdates(feed, [], T0));

        var historical = Update("historical", "creator", T0.AddMinutes(-1));
        Assert.Empty(tracker.ObserveUpdates(
            feed, [new(historical, T0.AddMinutes(1))], T0.AddMinutes(2)));
    }

    [Fact]
    public void Updates_SuppressUnknownAndFuturePublishedDates()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);
        Assert.Empty(tracker.ObserveUpdates(feed, [], T0));

        var unknown = Update("unix-epoch", "creator", DateTimeOffset.UnixEpoch);
        var defaultDate = Update("default-date", "creator", default);
        var future = Update("future", "creator", T0.AddMinutes(10));
        var notifications = tracker.ObserveUpdates(
            feed,
            [
                new(unknown, T0.AddMinutes(1)),
                new(defaultDate, T0.AddMinutes(1)),
                new(future, T0.AddMinutes(1)),
            ],
            T0.AddMinutes(1));

        Assert.Empty(notifications);
    }

    [Fact]
    public void Updates_RequirePublishedDateAfterBaselineAndAtOrBeforeObservation()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);
        Assert.Empty(tracker.ObserveUpdates(feed, [], T0));

        var sameSecond = Update("same-second", "creator", T0);
        var reliable = Update("reliable", "creator", T0.AddMinutes(1));
        var future = Update("not-yet", "creator", T0.AddMinutes(3));
        var notifications = tracker.ObserveUpdates(
            feed,
            [
                new(sameSecond, T0.AddMinutes(1)),
                new(reliable, T0.AddMinutes(1)),
                new(future, T0.AddMinutes(1)),
            ],
            T0.AddMinutes(1));

        Assert.Equal(["reliable"], notifications.Select(item => item.ItemId));
    }

    [Fact]
    public void Updates_SuppressExplicitlyUnreliablePublishedDates()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);
        Assert.Empty(tracker.ObserveUpdates(feed, [], T0));

        var unreliable = Update("unreliable", "creator", T0.AddMinutes(1)) with
        {
            Metadata = new Dictionary<string, string>
            {
                ["feedpeek.publishedAtReliable"] = "false",
            },
        };
        Assert.Empty(tracker.ObserveUpdates(
            feed, [new(unreliable, T0.AddMinutes(1))], T0.AddMinutes(2)));
    }

    [Fact]
    public void FailedUpdateObservation_DoesNotEstablishNewCreatorBaseline()
    {
        var feed = UpdatesFeed();
        var sourceWatch = Watch(feed, "source-generation");
        var creatorWatch = new NotificationWatch(new NotificationWatchKey(feed, "creator-a"), "creator-generation");
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([sourceWatch]);

        var old = Update("old", "creator-a", T0);
        Assert.Empty(tracker.ObserveUpdates(feed, [new(old, T0)], T0));

        var existing = Update("existing", "creator-a", T0.AddMinutes(1));
        Assert.Equal(["existing"], tracker.ObserveUpdates(
            feed, [new(old, T0), new(existing, T0.AddMinutes(1))], T0.AddMinutes(2))
            .Select(item => item.ItemId));

        tracker.Synchronize([sourceWatch, creatorWatch]);

        // A failed/partial source query must not initialize the newly added creator watch,
        // even if the poller returned a few staged items alongside the error.
        Assert.Empty(tracker.ObserveUpdates(
            feed, [new(old, T0), new(existing, T0.AddMinutes(1))], T0.AddMinutes(3),
            canInitializeBaseline: false));

        // The first successful observation is the creator's silent baseline.
        Assert.Empty(tracker.ObserveUpdates(
            feed, [new(old, T0), new(existing, T0.AddMinutes(1))], T0.AddMinutes(4)));

        var next = Update("next", "creator-a", T0.AddMinutes(5));
        Assert.Equal(["next"], tracker.ObserveUpdates(
            feed,
            [new(old, T0), new(existing, T0.AddMinutes(1)), new(next, T0.AddMinutes(5))],
            T0.AddMinutes(6)).Select(item => item.ItemId));
    }

    [Fact]
    public void InitializedUpdateWatch_StillProcessesPartialObservationWhenBaselineInitializationIsBlocked()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);
        Assert.Empty(tracker.ObserveUpdates(feed, [], T0));

        var update = Update("partial", "creator", T0.AddMinutes(1));
        Assert.Equal(["partial"], tracker.ObserveUpdates(
            feed, [new(update, T0.AddMinutes(1))], T0.AddMinutes(2),
            canInitializeBaseline: false).Select(item => item.ItemId));
    }

    [Fact]
    public void SuccessfulEmptyHead_InitializesNewUpdateWatch()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);

        Assert.Empty(tracker.ObserveUpdates(feed, [], T0, canInitializeBaseline: true));

        var update = Update("after-empty-head", "creator", T0.AddMinutes(1));
        Assert.Equal(["after-empty-head"], tracker.ObserveUpdates(
            feed, [new(update, T0.AddMinutes(1))], T0.AddMinutes(2))
            .Select(item => item.ItemId));
    }

    [Fact]
    public void DiskSeenLedger_DeduplicatesLargeBatchAndCleansReplacedScopes()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed, "generation-1")]);

        var items = Enumerable.Range(0, 2501)
            .Select(index => Update($"item-{index}", "creator", T0))
            .Select(item => new NotificationUpdateObservation(item, T0))
            .ToArray();
        Assert.Empty(tracker.ObserveUpdates(feed, items, T0));
        Assert.Equal(2501, tracker.RecordCount);

        Assert.Empty(tracker.ObserveUpdates(feed, items, T0.AddMinutes(1)));
        Assert.Equal(2501, tracker.RecordCount);

        tracker.Synchronize([Watch(feed, "generation-2")]);
        Assert.Equal(0, tracker.RecordCount);
        Assert.Empty(tracker.ObserveUpdates(feed, [items[0]], T0.AddMinutes(2)));
        Assert.Equal(1, tracker.RecordCount);

        tracker.Synchronize([]);
        Assert.Equal(0, tracker.RecordCount);
    }

    [Fact]
    public void Updates_DeduplicateSourceWideAndCreatorWatches()
    {
        var feed = UpdatesFeed();
        var selection = new NotificationSelection(
            NotificationSelectionMode.Custom,
            UpdatesEnabled: false,
            LiveEnabled: false,
            Sources: [new SourceNotificationRule(
                "provider", "source", ["updates"], [], true, false)],
            Creators: [new CreatorNotificationRule("provider", "source", "creator", true, false)]);
        var watches = NotificationWatchSelector.Select(feed, selection);
        Assert.Equal(2, watches.Count);

        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize(watches.Select(key => new NotificationWatch(key, "generation")).ToArray());
        var oldItem = Update("old", "creator", T0);
        var newItem = Update("new", "creator", T0.AddMinutes(2));
        Assert.Empty(tracker.ObserveUpdates(feed, [new(oldItem, T0)], T0));

        var notifications = tracker.ObserveUpdates(
            feed,
            [new(oldItem, T0), new(newItem, T0.AddMinutes(2))],
            T0.AddMinutes(3));

        Assert.Single(notifications);
        Assert.Equal("new", notifications[0].ItemId);
    }

    [Fact]
    public void Live_DeduplicateSourceWideAndCreatorWatches()
    {
        var feed = LiveFeed();
        var selection = new NotificationSelection(
            NotificationSelectionMode.Custom,
            UpdatesEnabled: false,
            LiveEnabled: false,
            Sources: [new SourceNotificationRule(
                "provider", "source", [], ["live"], false, true)],
            Creators: [new CreatorNotificationRule("provider", "source", "creator", false, true)]);
        var watches = NotificationWatchSelector.Select(feed, selection);
        Assert.Equal(2, watches.Count);

        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize(watches.Select(key => new NotificationWatch(key, "generation")).ToArray());
        Assert.Empty(tracker.ObserveLive(feed, [new("room-1", "creator")]));

        var notifications = tracker.ObserveLive(
            feed,
            [new("room-1", "creator"), new("room-2", "creator")]);

        Assert.Equal(["room-2"], notifications);
    }

    [Fact]
    public void CustomSelection_UsesActualRuleFeedIdsAndIgnoresGlobalTypeSwitches()
    {
        var updates = UpdatesFeed("updates-a");
        var wrongFeed = UpdatesFeed("updates-b");
        var selection = new NotificationSelection(
            NotificationSelectionMode.Custom,
            UpdatesEnabled: false,
            LiveEnabled: false,
            Sources: [new SourceNotificationRule(
                "provider", "source", ["updates-a"], [], true, false)],
            Creators: []);

        Assert.Single(NotificationWatchSelector.Select(updates, selection));
        Assert.Empty(NotificationWatchSelector.Select(wrongFeed, selection));
    }

    [Fact]
    public void AllSourcesSelection_StillHonorsGlobalTypeSwitches()
    {
        var updates = UpdatesFeed();
        var live = LiveFeed();
        var selection = new NotificationSelection(
            NotificationSelectionMode.AllSources,
            UpdatesEnabled: false,
            LiveEnabled: true,
            Sources: [],
            Creators: []);

        Assert.Empty(NotificationWatchSelector.Select(updates, selection));
        Assert.Single(NotificationWatchSelector.Select(live, selection));
    }

    [Fact]
    public void SelectionAndBaseline_IsolateProviderAndSourceIdentity()
    {
        var first = UpdatesFeed("updates", "provider-a", "source-a");
        var second = UpdatesFeed("updates", "provider-b", "source-a");
        var third = UpdatesFeed("updates", "provider-a", "source-b");
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(first), Watch(second), Watch(third)]);

        Assert.Empty(tracker.ObserveUpdates(first, [new(Update("same", "creator", T0), T0)], T0));
        Assert.Empty(tracker.ObserveUpdates(second, [new(Update("same", "creator", T0), T0)], T0));
        Assert.Empty(tracker.ObserveUpdates(third, [new(Update("same", "creator", T0), T0)], T0));

        var firstNew = Update("new-first", "creator", T0.AddMinutes(1)) with { ProviderId = "provider-a", SourceId = "source-a" };
        var secondNew = Update("new-second", "creator", T0.AddMinutes(1)) with { ProviderId = "provider-b", SourceId = "source-a" };
        Assert.Equal(["new-first"], tracker.ObserveUpdates(first, [new(firstNew, T0.AddMinutes(1))], T0.AddMinutes(2)).Select(item => item.ItemId));
        Assert.Equal(["new-second"], tracker.ObserveUpdates(second, [new(secondNew, T0.AddMinutes(1))], T0.AddMinutes(2)).Select(item => item.ItemId));
        Assert.Empty(tracker.ObserveUpdates(third, [new(Update("same", "creator", T0), T0)], T0.AddMinutes(2)));
    }

    [Fact]
    public void FailedObservation_DoesNotEstablishBaselineOrTurnOnlineIntoOffline()
    {
        var pendingFeed = LiveFeed("pending-source");
        var onlineFeed = LiveFeed("online-source");
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(pendingFeed), Watch(onlineFeed)]);

        // A failed query is represented by not calling Observe at all.
        Assert.Empty(tracker.ObserveLive(onlineFeed, [new("room-1", "creator")]));
        Assert.Empty(tracker.ObserveLive(onlineFeed, [new("room-1", "creator")]));

        // The pending source remains uninitialized after the failure and its first successful
        // observation is still a silent baseline.
        Assert.Empty(tracker.ObserveLive(pendingFeed, [new("room-2", "creator")]));
    }

    [Fact]
    public void FailedSource_DoesNotPreventOtherSourceFromNotifying()
    {
        var failed = LiveFeed("failed-source");
        var healthy = LiveFeed("healthy-source");
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(failed), Watch(healthy)]);
        Assert.Empty(tracker.ObserveLive(failed, [new("room-f", "creator")]));
        Assert.Empty(tracker.ObserveLive(healthy, [new("room-h", "creator")]));

        // No Observe call is made for failed-source during this cycle.
        Assert.Equal(["room-h2"], tracker.ObserveLive(healthy, [new("room-h", "creator"), new("room-h2", "creator")]));
    }

    [Fact]
    public void Live_RequiresSuccessfulOfflineObservationBeforeNotifyingAgain()
    {
        var feed = LiveFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed)]);
        Assert.Empty(tracker.ObserveLive(feed, [new("room-1", "creator")]));

        // A failed query is not an empty successful observation, so this is still online.
        Assert.Empty(tracker.ObserveLive(feed, [new("room-1", "creator")]));
        Assert.Empty(tracker.ObserveLive(feed, []));
        Assert.Equal(["room-1"], tracker.ObserveLive(feed, [new("room-1", "creator")]));
        Assert.Empty(tracker.ObserveLive(feed, [new("room-1", "creator")]));
    }

    [Fact]
    public void RemovingAndReaddingWatchOrChangingGeneration_RebuildsItsBaseline()
    {
        var feed = UpdatesFeed();
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([Watch(feed, "generation-1")]);
        var first = Update("first", "creator", T0);
        Assert.Empty(tracker.ObserveUpdates(feed, [new(first, T0)], T0));

        var second = Update("second", "creator", T0.AddMinutes(1));
        Assert.Single(tracker.ObserveUpdates(feed, [new(first, T0), new(second, T0.AddMinutes(1))], T0.AddMinutes(2)));

        tracker.Synchronize([]);
        tracker.Synchronize([Watch(feed, "generation-2")]);
        Assert.Empty(tracker.ObserveUpdates(feed, [new(first, T0), new(second, T0.AddMinutes(1))], T0.AddMinutes(3)));

        var third = Update("third", "creator", T0.AddMinutes(4));
        Assert.Equal(["third"], tracker.ObserveUpdates(
            feed,
            [new(first, T0), new(second, T0.AddMinutes(1)), new(third, T0.AddMinutes(4))],
            T0.AddMinutes(5)).Select(item => item.ItemId));
    }

    [Fact]
    public void AddingCreatorWatch_BaselinesCreatorWithoutResettingExistingSourceWatch()
    {
        var feed = UpdatesFeed();
        var sourceWatch = Watch(feed, "source-generation");
        var creatorWatch = new NotificationWatch(new NotificationWatchKey(feed, "creator-a"), "creator-generation");
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([sourceWatch]);

        var old = Update("old", "creator-a", T0);
        Assert.Empty(tracker.ObserveUpdates(feed, [new(old, T0)], T0));

        tracker.Synchronize([sourceWatch, creatorWatch]);
        var existingCreatorItem = Update("existing-creator", "creator-a", T0.AddMinutes(1));
        var newOtherCreatorItem = Update("new-other", "creator-b", T0.AddMinutes(2));
        var notifications = tracker.ObserveUpdates(
            feed,
            [new(old, T0), new(existingCreatorItem, T0.AddMinutes(1)), new(newOtherCreatorItem, T0.AddMinutes(2))],
            T0.AddMinutes(3));

        Assert.Equal(["existing-creator", "new-other"], notifications.Select(item => item.ItemId));
    }

    [Fact]
    public void AddingCreatorWatch_BaselinesCreatorLiveStateWithoutResettingSourceWatch()
    {
        var feed = LiveFeed();
        var sourceWatch = Watch(feed, "source-generation");
        var creatorWatch = new NotificationWatch(new NotificationWatchKey(feed, "creator-a"), "creator-generation");
        using var tracker = new NotificationBaselineTracker();
        tracker.Synchronize([sourceWatch]);
        Assert.Empty(tracker.ObserveLive(feed, [new("room-a", "creator-a")]));

        tracker.Synchronize([sourceWatch, creatorWatch]);
        Assert.Equal(["room-b"], tracker.ObserveLive(feed, [new("room-a", "creator-a"), new("room-b", "creator-b")]));
        Assert.Equal(["room-c"], tracker.ObserveLive(
            feed,
            [new("room-a", "creator-a"), new("room-b", "creator-b"), new("room-c", "creator-a")]));
    }

    private static NotificationFeedKey UpdatesFeed(
        string feedId = "updates",
        string providerId = "provider",
        string sourceId = "source")
        => new(providerId, sourceId, feedId, NotificationKind.Updates);

    private static NotificationFeedKey LiveFeed(
        string sourceId = "source",
        string providerId = "provider")
        => new(providerId, sourceId, "live", NotificationKind.Live);

    private static NotificationWatch Watch(NotificationFeedKey feed, string generation = "generation")
        => new(new NotificationWatchKey(feed), generation);

    private static FeedItem Update(string itemId, string creatorKey, DateTimeOffset publishedAt)
        => new(
            "provider",
            "source",
            "updates",
            itemId,
            itemId,
            creatorKey,
            creatorKey,
            null,
            $"https://example.test/{itemId}",
            publishedAt,
            null,
            null,
            null,
            null,
            null);
}
