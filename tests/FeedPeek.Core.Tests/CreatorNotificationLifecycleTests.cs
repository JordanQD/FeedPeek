using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using FeedPeek.Services;

namespace FeedPeek.Core.Tests;

public sealed class CreatorNotificationLifecycleTests
{
    [Fact]
    public void ResponseFromPreviousAccountCannotBeStampedAsTheNewAccount()
    {
        var source = new SourceNotificationKey(Guid.NewGuid().ToString(), "source");
        var oldGeneration = NotificationStateChanges.MembershipGeneration(source, "default");
        NotificationStateChanges.MarkProvider(source.ProviderId);
        var newGeneration = NotificationStateChanges.MembershipGeneration(source, "default");
        CreatorNotificationStatusCache.Publish(source, newGeneration, [], true);
        CreatorNotificationStatusCache.SetCreatorState(source, "default", "old-up", CreatorFollowState.Followed, oldGeneration);
        Assert.Equal(CreatorFollowState.NotFollowed, CreatorNotificationStatusCache.GetState(source, "old-up", "default"));
        CreatorNotificationStatusCache.Remove(source);
    }

    [Fact]
    public void NewFollowingCreatorSilentlyBaselinesUpdatesWhileExistingCreatorStillNotifies()
    {
        var source = new SourceNotificationKey("updates-provider", "updates-source");
        var feed = new NotificationFeedKey(source.ProviderId, source.SourceId, "updates", NotificationKind.Updates);
        var membership = new CreatorNotificationMembershipTracker();
        var baseline = new NotificationBaselineTracker();
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        void Synchronize(params string[] creators) => baseline.Synchronize(
            CreatorNotificationMembershipTracker.ExpandWatches(feed, [new(feed)],
                membership.Observe(source, "account", new(creators.Select(id => Creator(source, id)).ToArray(), true)))
                .Select(key => new NotificationWatch(key, "account")).ToArray());
        FeedItem Item(string id, string author) => new(source.ProviderId, source.SourceId, feed.FeedId,
            id, id, author, author, null, "https://example.test/" + id, start, null, null, null, null, null);
        Synchronize("existing");
        Assert.Empty(baseline.ObserveUpdates(feed, [], start));
        Synchronize("existing", "new");
        var arrived = start.AddMinutes(1);
        var first = baseline.ObserveUpdates(feed, [new(Item("existing-update", "existing"), arrived),
            new(Item("new-history", "new"), arrived)], arrived);
        Assert.Equal(["existing-update"], first.Select(item => item.ItemId));
        var later = start.AddMinutes(2);
        Assert.Equal(["new-update"], baseline.ObserveUpdates(feed,
            [new(Item("new-history", "new"), arrived), new(Item("new-update", "new"), later)], later)
            .Select(item => item.ItemId));
    }

    [Fact]
    public void NewFollowedCreatorGetsSilentLiveBaselineWithoutResettingExistingCreator()
    {
        var source = new SourceNotificationKey("membership-provider", "membership-source");
        var feed = new NotificationFeedKey(source.ProviderId, source.SourceId, "live", NotificationKind.Live);
        var membership = new CreatorNotificationMembershipTracker();
        var baseline = new NotificationBaselineTracker();
        void Synchronize(params string[] creators)
        {
            var snapshot = membership.Observe(source, "account", new(creators.Select(id => Creator(source, id)).ToArray(), true));
            baseline.Synchronize(CreatorNotificationMembershipTracker.ExpandWatches(feed, [new(feed)], snapshot)
                .Select(key => new NotificationWatch(key, "unchanged")).ToArray());
        }
        Synchronize("existing");
        Assert.Empty(baseline.ObserveLive(feed, []));
        Synchronize("existing", "new");
        Assert.Equal(["existing-room"], baseline.ObserveLive(feed,
            [new("existing-room", "existing"), new("new-room", "new")]));
        Assert.Empty(baseline.ObserveLive(feed, [new("new-room", "new")]));
        Assert.Empty(baseline.ObserveLive(feed, []));
        Assert.Equal(["new-room"], baseline.ObserveLive(feed, [new("new-room", "new")]));
    }

    [Fact]
    public void MembershipFailureRetainsOnlineStateAndRecoveryDoesNotReportOpening()
    {
        var source = new SourceNotificationKey("failure-provider", "failure-source");
        var feed = new NotificationFeedKey(source.ProviderId, source.SourceId, "live", NotificationKind.Live);
        var membership = new CreatorNotificationMembershipTracker();
        var baseline = new NotificationBaselineTracker();
        void Synchronize(CreatorNotificationMembershipSnapshot snapshot) => baseline.Synchronize(
            CreatorNotificationMembershipTracker.ExpandWatches(feed, [new(feed)], snapshot)
                .Select(key => new NotificationWatch(key, "account")).ToArray());
        Synchronize(membership.Observe(source, "account", new([Creator(source, "up")], true)));
        Assert.Empty(baseline.ObserveLive(feed, [new("room", "up")]));
        var failed = membership.GetSnapshot(source, "account");
        Synchronize(failed);
        Assert.False(failed.CanObserve); // App skips ObserveLive for this batch.
        Synchronize(membership.Observe(source, "account", new([Creator(source, "up")], true)));
        Assert.Empty(baseline.ObserveLive(feed, [new("room", "up")]));
    }

    [Fact]
    public void AuthorPreferencesOnlyResetEffectiveSelectedCustomWatch()
    {
        var feed = new NotificationFeedKey("generation-provider", Guid.NewGuid().ToString(), "live", NotificationKind.Live);
        var key = new NotificationWatchKey(feed, "up");
        var all = NotificationStateChanges.Generation(key, NotificationSelectionMode.AllSources, "subscription");
        var customAll = NotificationStateChanges.Generation(key, NotificationSelectionMode.Custom, "subscription", false);
        var customSelected = NotificationStateChanges.Generation(key, NotificationSelectionMode.Custom, "subscription", true);
        NotificationStateChanges.MarkScope(feed.ProviderId, feed.SourceId, feed.Kind, "up");
        Assert.Equal(all, NotificationStateChanges.Generation(key, NotificationSelectionMode.AllSources, "subscription"));
        Assert.Equal(customAll, NotificationStateChanges.Generation(key, NotificationSelectionMode.Custom, "subscription", false));
        Assert.NotEqual(customSelected, NotificationStateChanges.Generation(key, NotificationSelectionMode.Custom, "subscription", true));
        NotificationStateChanges.MarkScope(feed.ProviderId, feed.SourceId, feed.Kind);
        Assert.NotEqual(customAll, NotificationStateChanges.Generation(key, NotificationSelectionMode.Custom, "subscription", false));
        Assert.Equal(all, NotificationStateChanges.Generation(key, NotificationSelectionMode.AllSources, "subscription"));
    }

    [Fact]
    public void LoginAndSourceChangeInvalidateRelationshipAndMembershipGeneration()
    {
        var source = new SourceNotificationKey(Guid.NewGuid().ToString(), "source");
        var first = NotificationStateChanges.MembershipGeneration(source, "account-a");
        CreatorNotificationStatusCache.Publish(source, first, [Creator(source, "up")], true);
        Assert.Equal(CreatorFollowState.Followed, CreatorNotificationStatusCache.GetState(source, "up", "account-a"));
        Assert.Equal(CreatorFollowState.Unknown, CreatorNotificationStatusCache.GetState(source, "up", "account-b"));
        NotificationStateChanges.MarkProvider(source.ProviderId); // Re-login may retain the same credential profile ID.
        Assert.NotEqual(first, NotificationStateChanges.MembershipGeneration(source, "account-a"));
        Assert.Equal(CreatorFollowState.Unknown, CreatorNotificationStatusCache.GetState(source, "up", "account-a"));
        var second = NotificationStateChanges.MembershipGeneration(source, "account-a");
        NotificationStateChanges.MarkSource(source.ProviderId, source.SourceId);
        Assert.NotEqual(second, NotificationStateChanges.MembershipGeneration(source, "account-a"));
        CreatorNotificationStatusCache.Remove(source);
    }

    [Fact]
    public void PartialOrFailedRelationResponseDoesNotMarkAbsentAuthorUnfollowed()
    {
        var source = new SourceNotificationKey(Guid.NewGuid().ToString(), "source");
        var generation = NotificationStateChanges.MembershipGeneration(source, "account");
        CreatorNotificationStatusCache.Publish(source, generation, [Creator(source, "up")], true);
        Assert.Equal(CreatorFollowState.NotFollowed, CreatorNotificationStatusCache.GetState(source, "absent", "account"));
        CreatorNotificationStatusCache.Publish(source, generation, [], false);
        Assert.Equal(CreatorFollowState.Unknown, CreatorNotificationStatusCache.GetState(source, "up", "account"));
        Assert.Equal(CreatorFollowState.Unknown, CreatorNotificationStatusCache.GetState(source, "absent", "account"));
        CreatorNotificationStatusCache.Remove(source);
    }

    private static CreatorDescriptor Creator(SourceNotificationKey source, string id)
        => new(source.ProviderId, source.SourceId, id, id, null, null, ["video", "live"]);
}
