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
    public const string SubscriptionId = "feedpeek.video-site:account-default:updates";
    public const string LegacyCredentialProfileId = "legacy-biliradar-cookie";
    public const string SourceIconOption = "source.icon";

    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(15);

    public static SourceDefinition CreateSource()
        => new(
            PluginId,
            SourceId,
            "哔哩哔哩",
            LegacyCredentialProfileId,
            [new FeedDefinition(
                PluginFeedIds.Updates,
                "关注更新",
                "video",
                SupportsPaging: true,
                SupportsNotifications: true)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SourceIconOption] = "https://www.bilibili.com/favicon.ico",
            });

    public static FeedSubscription CreateSubscription(bool isEnabled)
        => new(
            SubscriptionId,
            PluginId,
            SourceId,
            PluginFeedIds.Updates,
            "哔哩哔哩关注更新",
            null,
            LegacyCredentialProfileId,
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
