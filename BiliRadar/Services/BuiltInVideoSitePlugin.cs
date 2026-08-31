using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;
using System;
using System.Collections.Generic;

namespace BiliRadar.Services;

internal static class BuiltInVideoSitePlugin
{
    public const string PluginId = "feedpeek.video-site";
    public const string SourceId = "account-default";
    public const string UpdatesSubscriptionId = "feedpeek.video-site:account-default:updates";
    public const string HistorySubscriptionId = "feedpeek.video-site:account-default:history";
    public const string ViewLaterSubscriptionId = "feedpeek.video-site:account-default:viewLater";
    public const string LiveSubscriptionId = "feedpeek.video-site:account-default:live";
    public const string DefaultCredentialProfileId = "account-default";
    public const string SourceIconOption = "source.icon";

    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(15);

    public static SourceDefinition CreateSource()
        => new(
            PluginId,
            SourceId,
            "哔哩哔哩",
            DefaultCredentialProfileId,
            [
                new FeedDefinition(
                    PluginFeedIds.Updates,
                    "关注更新",
                    "video",
                    SupportsPaging: true,
                    SupportsNotifications: true),
                new FeedDefinition(
                    PluginFeedIds.History,
                    "历史记录",
                    "history",
                    SupportsPaging: true,
                    SupportsNotifications: false),
                new FeedDefinition(
                    PluginFeedIds.ViewLater,
                    "稍后再看",
                    "collection",
                    SupportsPaging: true,
                    SupportsNotifications: false),
                new FeedDefinition(
                    PluginFeedIds.Live,
                    "正在直播",
                    "live",
                    SupportsPaging: false,
                    SupportsNotifications: true),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SourceIconOption] = "https://www.bilibili.com/favicon.ico",
            });

    public static IReadOnlyList<FeedSubscription> CreateSubscriptions(bool isEnabled)
        =>
        [
            CreateSubscription(UpdatesSubscriptionId, PluginFeedIds.Updates, "哔哩哔哩关注更新", isEnabled),
            CreateSubscription(HistorySubscriptionId, PluginFeedIds.History, "哔哩哔哩历史记录", isEnabled),
            CreateSubscription(ViewLaterSubscriptionId, PluginFeedIds.ViewLater, "哔哩哔哩稍后再看", isEnabled),
            CreateSubscription(LiveSubscriptionId, PluginFeedIds.Live, "哔哩哔哩正在直播", isEnabled),
        ];

    public static FeedSubscription CreateLiveSubscription()
        => CreateSubscription(LiveSubscriptionId, PluginFeedIds.Live, "哔哩哔哩正在直播", isEnabled: true);

    private static FeedSubscription CreateSubscription(
        string subscriptionId,
        string feedId,
        string displayName,
        bool isEnabled)
        => new(
            subscriptionId,
            PluginId,
            SourceId,
            feedId,
            displayName,
            null,
            DefaultCredentialProfileId,
            isEnabled,
            DefaultRefreshInterval,
            null);

    public static PluginInvocationContext CreateInvocationContext(FeedSubscription subscription)
        => new(
            subscription.SubscriptionId,
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            subscription.CredentialProfileId,
            subscription.Options);
}
