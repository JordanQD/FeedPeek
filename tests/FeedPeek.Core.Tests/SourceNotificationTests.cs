using FeedPeek.Core.Models;

namespace FeedPeek.Core.Tests;

public sealed class SourceNotificationTests
{
    [Fact]
    public void Capabilities_RequireSubscribedEnabledNotificationFeedsFromTheSameSource()
    {
        var source = Source(
            new("latest-posts", "更新", "feed", false, true),
            new("room-state", "直播", "live", false, true),
            new("history", "历史", "feed", false, false),
            new("saved", "收藏", "collection", false, true),
            new("disabled", "已停用", "feed", false, true),
            new("not-subscribed", "未订阅", "feed", false, true));
        FeedSubscription[] subscriptions = [
            Subscription("latest-posts"), Subscription("room-state"),
            Subscription("history"), Subscription("saved"),
            Subscription("disabled") with { IsEnabled = false },
            Subscription("not-subscribed") with { ProviderId = "another.plugin" },
            Subscription("not-subscribed") with { SourceId = "another-source" },
            Subscription("latest-posts"),
        ];

        var capabilities = SourceNotificationCapabilities.Resolve(source, subscriptions);
        Assert.Equal(["latest-posts"], capabilities.UpdateFeedIds);
        Assert.Equal(["room-state"], capabilities.LiveFeedIds);
        var rule = capabilities.CreateRule(new(source.ProviderId, source.SourceId), isAutomatic: true);
        Assert.Equal(new SourceNotificationKey("demo.plugin", "source"), rule.Key);
        Assert.True(rule.UpdateNotificationsEnabled);
        Assert.True(rule.LiveNotificationsEnabled);
        Assert.True(rule.IsAutomatic);
        Assert.NotEqual(rule.Key, new SourceNotificationKey("another.plugin", "source"));
    }

    [Theory]
    [InlineData("rss")]
    [InlineData("atom")]
    public void RssAndAtom_OnlyExposeUpdates(string kind)
    {
        var capabilities = SourceNotificationCapabilities.Resolve(
            Source(new FeedDefinition("updates", "订阅", kind, false, true)), [Subscription("updates")]);
        Assert.True(capabilities.SupportsUpdates);
        Assert.False(capabilities.SupportsLive);
        var rule = capabilities.CreateRule(new("rss", "source"));
        Assert.True(rule.UpdateNotificationsEnabled);
        Assert.False(rule.LiveNotificationsEnabled);
    }

    [Fact]
    public void LiveRoom_OnlyExposesLiveAndOptInCannotGrantMissingCapabilities()
    {
        var source = Source(new FeedDefinition("room-state", "直播", "live", false, true));
        var subscriptions = new[] { Subscription("room-state") };
        var capabilities = SourceNotificationCapabilities.Resolve(source, subscriptions);
        Assert.False(capabilities.SupportsUpdates);
        Assert.True(capabilities.SupportsLive);
        Assert.False(SourceNotificationCapabilities.Resolve(source, subscriptions, supportsLive: false).HasAny);
        Assert.False(SourceNotificationCapabilities.Resolve(
            Source(new FeedDefinition("room-state", "直播", "live", false, false)), subscriptions).HasAny);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurrentCapabilitiesReplaceStaleFeedIdsWithoutChangingUserSwitches(bool enabled)
    {
        var saved = new SourceNotificationRule("demo.plugin", "source", ["removed-feed"], [],
            UpdateNotificationsEnabled: enabled, LiveNotificationsEnabled: enabled, IsAutomatic: true);
        var capabilities = SourceNotificationCapabilities.Resolve(Source(
            new("latest-posts", "更新", "feed", false, true),
            new("room-state", "修复后的直播", "live", false, true)),
            [Subscription("latest-posts"), Subscription("room-state")]);

        var effective = capabilities.ApplyTo(saved);

        Assert.Equal(["latest-posts"], effective.UpdateFeedIds);
        Assert.Equal(["room-state"], effective.LiveFeedIds);
        Assert.Equal(enabled, effective.UpdateNotificationsEnabled);
        Assert.Equal(enabled, effective.LiveNotificationsEnabled);
        Assert.Equal(saved.Key, effective.Key);
        Assert.Equal(saved.IsAutomatic, effective.IsAutomatic);
        Assert.Equal(["removed-feed"], saved.UpdateFeedIds);
        Assert.Empty(saved.LiveFeedIds);
    }

    private static SourceDefinition Source(params FeedDefinition[] feeds)
        => new("demo.plugin", "source", "Demo", null, feeds, null);

    private static FeedSubscription Subscription(string feedId)
        => new("subscription-" + feedId, "demo.plugin", "source", feedId, "Demo", null, null,
            true, TimeSpan.FromMinutes(1), null);
}
