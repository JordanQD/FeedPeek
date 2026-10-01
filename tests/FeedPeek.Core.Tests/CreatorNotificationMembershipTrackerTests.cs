using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;

namespace FeedPeek.Core.Tests;

public sealed class CreatorNotificationMembershipTrackerTests
{
    [Fact]
    public void CompleteFollowingReplacesMembershipAndRemovesUnfollowedCreator()
    {
        var source = new SourceNotificationKey("provider", "source");
        var tracker = new CreatorNotificationMembershipTracker();

        tracker.Observe(source, "generation", new([Creator(source, "a"), Creator(source, "b")], true));
        var snapshot = tracker.Observe(source, "generation", new([Creator(source, "b")], true));

        Assert.Equal(["b"], snapshot.Creators.Keys);
        Assert.True(snapshot.IsComplete);
        Assert.True(snapshot.CanObserve);
    }

    [Fact]
    public void IncompleteFollowingOnlyAddsPositiveMembers()
    {
        var source = new SourceNotificationKey("provider", "source");
        var tracker = new CreatorNotificationMembershipTracker();
        tracker.Observe(source, "generation", new([Creator(source, "old", "video")], true));

        var snapshot = tracker.Observe(source, "generation", new([Creator(source, "new", "video")], false));
        var feed = new NotificationFeedKey("provider", "source", "updates", NotificationKind.Updates);

        Assert.Equal(["new", "old"], snapshot.Creators.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.False(snapshot.IsComplete);
        Assert.True(snapshot.CanObserve);
        Assert.Equal(
            new[] { new NotificationWatchKey(feed, "old"), new NotificationWatchKey(feed, "new") }
                .OrderBy(item => item.CreatorKey, StringComparer.Ordinal),
            CreatorNotificationMembershipTracker.ExpandWatches(
                feed, [new NotificationWatchKey(feed)], snapshot)
                .OrderBy(item => item.CreatorKey, StringComparer.Ordinal));
    }

    [Fact]
    public void FailedQueryRetainsSameGenerationMembershipButCannotBeObserved()
    {
        var source = new SourceNotificationKey("provider", "source");
        var tracker = new CreatorNotificationMembershipTracker();
        tracker.Observe(source, "generation", new([Creator(source, "creator", "video")], true));

        var snapshot = tracker.GetSnapshot(source, "generation");
        var feed = new NotificationFeedKey("provider", "source", "updates", NotificationKind.Updates);

        Assert.Equal(["creator"], snapshot.Creators.Keys);
        Assert.True(snapshot.IsComplete);
        Assert.False(snapshot.CanObserve);
        Assert.Equal(
            [new NotificationWatchKey(feed, "creator")],
            CreatorNotificationMembershipTracker.ExpandWatches(
                feed, [new NotificationWatchKey(feed)], snapshot));
    }

    [Fact]
    public void FirstFailedQueryHasNoMembersAndGenerationChangeClearsOldState()
    {
        var source = new SourceNotificationKey("provider", "source");
        var tracker = new CreatorNotificationMembershipTracker();
        Assert.Empty(tracker.GetSnapshot(source, "generation").Creators);

        tracker.Observe(source, "generation", new([Creator(source, "old")], true));
        var changed = tracker.GetSnapshot(source, "new-generation");

        Assert.Empty(changed.Creators);
        Assert.False(changed.CanObserve);
    }

    [Fact]
    public void ObservingWithNewGenerationDoesNotCarryOldMembersIntoPartialResult()
    {
        var source = new SourceNotificationKey("provider", "source");
        var tracker = new CreatorNotificationMembershipTracker();
        tracker.Observe(source, "account-a", new([Creator(source, "old")], true));

        var snapshot = tracker.Observe(source, "account-b", new([Creator(source, "new")], false));

        Assert.Equal(["new"], snapshot.Creators.Keys);
        Assert.False(snapshot.IsComplete);
        Assert.True(snapshot.CanObserve);
    }

    [Fact]
    public void SameCreatorKeyOnDifferentSourcesRemainsIsolated()
    {
        var first = new SourceNotificationKey("provider", "first");
        var second = new SourceNotificationKey("provider", "second");
        var tracker = new CreatorNotificationMembershipTracker();
        tracker.Observe(first, "generation", new([Creator(first, "same")], true));

        Assert.Empty(tracker.GetSnapshot(second, "generation").Creators);
    }

    [Fact]
    public void ExpandWatchesIntersectsSourceMembershipWithCreatorNotificationKinds()
    {
        var source = new SourceNotificationKey("provider", "source");
        var updates = new NotificationFeedKey("provider", "source", "updates", NotificationKind.Updates);
        var live = new NotificationFeedKey("provider", "source", "live", NotificationKind.Live);
        var snapshot = new CreatorNotificationMembershipSnapshot(
            new Dictionary<string, CreatorDescriptor>
            {
                ["video"] = Creator(source, "video", "video"),
                ["updates"] = Creator(source, "updates", "updates"),
                ["live"] = Creator(source, "live", "live"),
                ["none"] = Creator(source, "none"),
            },
            IsComplete: true,
            CanObserve: true);

        var sourceWide = new NotificationWatchKey(updates);
        var selectedUnknown = new NotificationWatchKey(updates, "missing");
        Assert.Equal(
            new[] { new NotificationWatchKey(updates, "video"), new NotificationWatchKey(updates, "updates") }
                .OrderBy(item => item.CreatorKey, StringComparer.Ordinal),
            CreatorNotificationMembershipTracker.ExpandWatches(updates, [sourceWide, selectedUnknown], snapshot)
                .OrderBy(item => item.CreatorKey, StringComparer.Ordinal));
        Assert.Equal(
            new[] { new NotificationWatchKey(live, "live") },
            CreatorNotificationMembershipTracker.ExpandWatches(live, [new NotificationWatchKey(live)], snapshot));
    }

    [Fact]
    public void ClearAndPruneRemoveStaleMemberships()
    {
        var first = new SourceNotificationKey("provider", "first");
        var second = new SourceNotificationKey("provider", "second");
        var tracker = new CreatorNotificationMembershipTracker();
        tracker.Observe(first, "generation", new([Creator(first, "first")], true));
        tracker.Observe(second, "generation", new([Creator(second, "second")], true));

        tracker.Prune([second]);
        Assert.Empty(tracker.GetSnapshot(first, "generation").Creators);
        tracker.Clear(second);
        Assert.Empty(tracker.GetSnapshot(second, "generation").Creators);
    }

    private static CreatorDescriptor Creator(
        SourceNotificationKey source,
        string key,
        params string[] kinds)
        => new(source.ProviderId, source.SourceId, key, key, null, null, kinds);
}
