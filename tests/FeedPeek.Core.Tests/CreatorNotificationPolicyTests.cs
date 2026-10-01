using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;

namespace FeedPeek.Core.Tests;

public sealed class CreatorNotificationPolicyTests
{
    [Fact]
    public void PolicyScopeChange_PreservesIndependentTypeSwitches()
    {
        var policy = new CreatorNotificationPolicy(
            "provider", "source", CreatorNotificationScope.SelectedCreators,
            CreatorNotificationScope.AllFollowing, UpdatesEnabled: false, LiveEnabled: true);

        var changed = policy.WithScope(CreatorNotificationScope.AllFollowing);

        Assert.Equal(CreatorNotificationScope.AllFollowing, changed.UpdatesScope);
        Assert.Equal(CreatorNotificationScope.AllFollowing, changed.LiveScope);
        Assert.False(changed.UpdatesEnabled);
        Assert.True(changed.LiveEnabled);
        Assert.Equal(policy.Key, changed.Key);
    }

    [Fact]
    public void PolicyDisplayPredicate_HidesWhenAllEffectiveTypesAreAllFollowing()
    {
        var policy = Policy(CreatorNotificationScope.AllFollowing, CreatorNotificationScope.AllFollowing);

        Assert.False(policy.ShouldShowSelectedCreators(
            new SourceNotificationKey("provider", "source"), Capabilities(updates: true, live: true)));
    }

    [Fact]
    public void PolicyDisplayPredicate_ShowsMixedScopeWhenSelectedTypeIsSupported()
    {
        var policy = Policy(CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.AllFollowing);

        Assert.True(policy.ShouldShowSelectedCreators(
            new SourceNotificationKey("provider", "source"), Capabilities(updates: true, live: true)));
    }

    [Fact]
    public void PolicyDisplayPredicate_IgnoresSelectedScopeForUnsupportedType()
    {
        var policy = Policy(CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.AllFollowing);

        Assert.False(policy.ShouldShowSelectedCreators(
            new SourceNotificationKey("provider", "source"), Capabilities(updates: false, live: true)));
    }

    [Fact]
    public void PolicyDisplayPredicate_UsesSavedScopesWhenCapabilitiesAreUnavailable()
    {
        var selected = Policy(CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.AllFollowing);
        var all = Policy(CreatorNotificationScope.AllFollowing, CreatorNotificationScope.AllFollowing);
        var key = new SourceNotificationKey("provider", "source");

        Assert.True(selected.ShouldShowSelectedCreators(key, null));
        Assert.False(all.ShouldShowSelectedCreators(key, new([], [])));
    }

    [Fact]
    public void PolicyDisplayPredicate_DoesNotUseDisabledGatesToChangeSavedScope()
    {
        var policy = Policy(CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.AllFollowing)
            with { UpdatesEnabled = false, LiveEnabled = false };

        Assert.True(policy.ShouldShowSelectedCreators(
            new SourceNotificationKey("provider", "source"), Capabilities(updates: true, live: true)));
    }

    [Fact]
    public void PolicyDisplayPredicate_RejectsDifferentProviderOrSource()
    {
        var policy = Policy(CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.SelectedCreators);

        Assert.False(policy.ShouldShowSelectedCreators(
            new SourceNotificationKey("other-provider", "source"), Capabilities(updates: true, live: true)));
        Assert.False(policy.ShouldShowSelectedCreators(
            new SourceNotificationKey("provider", "other-source"), Capabilities(updates: true, live: true)));
    }

    [Fact]
    public void CustomSelection_PolicyAllFollowingCreatesOneFeedWideWatch()
    {
        var feed = Feed("updates");
        var selection = Selection(
            [new CreatorNotificationPolicy("provider", "source",
                CreatorNotificationScope.AllFollowing, CreatorNotificationScope.SelectedCreators,
                UpdatesEnabled: true, LiveEnabled: false)],
            [new CreatorNotificationRule("provider", "source", "creator-a", true, true)]);

        Assert.Equal([new NotificationWatchKey(feed)], NotificationWatchSelector.Select(feed, selection));
    }

    [Fact]
    public void CustomSelection_SelectedPolicyRequiresPolicyTypeAndCreatorSwitch()
    {
        var updates = Feed("updates");
        var live = Feed("live", NotificationKind.Live);
        var selection = Selection(
            [new CreatorNotificationPolicy("provider", "source",
                CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.SelectedCreators,
                UpdatesEnabled: true, LiveEnabled: false)],
            [
                new CreatorNotificationRule("provider", "source", "enabled", true, true),
                new CreatorNotificationRule("provider", "source", "updates-off", false, true),
                new CreatorNotificationRule("provider", "source", "other-source", true, true) with { SourceId = "other" },
            ]);

        Assert.Equal([new NotificationWatchKey(updates, "enabled")], NotificationWatchSelector.Select(updates, selection));
        Assert.Empty(NotificationWatchSelector.Select(live, selection));
    }

    [Fact]
    public void AllSourcesAndNoneKeepTheirPriorityOverCreatorPolicies()
    {
        var feed = Feed("updates");
        var policy = new CreatorNotificationPolicy("provider", "source",
            CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.SelectedCreators,
            UpdatesEnabled: true, LiveEnabled: true);
        var creator = new CreatorNotificationRule("provider", "source", "creator", true, true);

        Assert.Equal([new NotificationWatchKey(feed)], NotificationWatchSelector.Select(
            feed, new(NotificationSelectionMode.AllSources, true, false, [], [creator], [policy])));
        Assert.Empty(NotificationWatchSelector.Select(
            feed, new(NotificationSelectionMode.None, true, true, [], [creator], [policy])));
    }

    [Fact]
    public void LegacyMigration_ComputesUpdateAndLiveScopesIndependently()
    {
        var source = new SourceNotificationRule(
            "provider", "source", ["updates"], ["live"],
            UpdateNotificationsEnabled: true, LiveNotificationsEnabled: false);
        var creators = new CreatorNotificationRule[]
        {
            new("provider", "source", "disabled-update", UpdatesEnabled: false, LiveEnabled: true),
            new("provider", "source", "enabled-update", UpdatesEnabled: true, LiveEnabled: false),
        };

        var policy = Assert.Single(CreatorNotificationPolicyMigration.FromLegacy([source], creators));

        Assert.Equal(CreatorNotificationScope.AllFollowing, policy.UpdatesScope);
        Assert.True(policy.UpdatesEnabled);
        Assert.Equal(CreatorNotificationScope.SelectedCreators, policy.LiveScope);
        Assert.True(policy.LiveEnabled);
    }

    [Fact]
    public void LegacyMigration_CreatorOnlyRulesStaySelectedAndFalseSwitchesDoNotOpenTypes()
    {
        var creators = new CreatorNotificationRule[]
        {
            new("provider", "source", "creator-a", UpdatesEnabled: false, LiveEnabled: false),
        };

        var first = CreatorNotificationPolicyMigration.FromLegacy([], creators);
        var second = CreatorNotificationPolicyMigration.FromLegacy([], creators);
        var policy = Assert.Single(first);

        Assert.Equal(first, second);
        Assert.Equal(CreatorNotificationScope.SelectedCreators, policy.UpdatesScope);
        Assert.False(policy.UpdatesEnabled);
        Assert.Equal(CreatorNotificationScope.SelectedCreators, policy.LiveScope);
        Assert.False(policy.LiveEnabled);
    }

    [Fact]
    public void LegacyMigration_ClosedSourceTypesRemainAllFollowingAndClosed()
    {
        var source = new SourceNotificationRule(
            "provider", "source", ["updates"], ["live"],
            UpdateNotificationsEnabled: false, LiveNotificationsEnabled: false);

        var policy = Assert.Single(CreatorNotificationPolicyMigration.FromLegacy([source], []));

        Assert.Equal(CreatorNotificationScope.AllFollowing, policy.UpdatesScope);
        Assert.False(policy.UpdatesEnabled);
        Assert.Equal(CreatorNotificationScope.AllFollowing, policy.LiveScope);
        Assert.False(policy.LiveEnabled);
    }

    [Fact]
    public void LegacyMigration_ClosedSourceTypeWithEnabledCreatorStaysSelectedForThatType()
    {
        var source = new SourceNotificationRule(
            "provider", "source", ["updates"], ["live"],
            UpdateNotificationsEnabled: false, LiveNotificationsEnabled: true);
        var creators = new CreatorNotificationRule[]
        {
            new("provider", "source", "creator", UpdatesEnabled: true, LiveEnabled: false),
        };

        var policy = Assert.Single(CreatorNotificationPolicyMigration.FromLegacy([source], creators));

        Assert.Equal(CreatorNotificationScope.SelectedCreators, policy.UpdatesScope);
        Assert.True(policy.UpdatesEnabled);
        Assert.Equal(CreatorNotificationScope.AllFollowing, policy.LiveScope);
        Assert.True(policy.LiveEnabled);
    }

    [Fact]
    public void LegacyMigration_MixedRulesRemainSeparatedByProviderAndSource()
    {
        var sourceA = new SourceNotificationRule("provider-a", "same-source", ["updates"], [], true, false);
        var sourceB = new SourceNotificationRule("provider-b", "same-source", ["updates"], [], false, false);
        var creators = new CreatorNotificationRule[]
        {
            new("provider-b", "same-source", "creator", true, false),
        };

        var policies = CreatorNotificationPolicyMigration.FromLegacy([sourceA, sourceB], creators)
            .ToDictionary(policy => policy.Key);

        Assert.Equal(CreatorNotificationScope.AllFollowing, policies[new("provider-a", "same-source")].UpdatesScope);
        Assert.Equal(CreatorNotificationScope.SelectedCreators, policies[new("provider-b", "same-source")].UpdatesScope);
        Assert.True(policies[new("provider-b", "same-source")].UpdatesEnabled);
    }

    private static NotificationSelection Selection(
        IReadOnlyList<CreatorNotificationPolicy> policies,
        IReadOnlyList<CreatorNotificationRule> creators)
        => new(NotificationSelectionMode.Custom, false, false, [], creators, policies);

    private static NotificationFeedKey Feed(string feedId, NotificationKind kind = NotificationKind.Updates)
        => new("provider", "source", feedId, kind);

    private static CreatorNotificationPolicy Policy(
        CreatorNotificationScope updates,
        CreatorNotificationScope live)
        => new("provider", "source", updates, live, UpdatesEnabled: true, LiveEnabled: true);

    private static SourceNotificationCapabilities Capabilities(bool updates, bool live)
        => new(updates ? ["updates"] : [], live ? ["live"] : []);
}
