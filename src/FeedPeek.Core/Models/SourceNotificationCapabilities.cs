namespace FeedPeek.Core.Models;

/// <summary>Available notification kinds, derived from enabled subscriptions and Feed definitions.</summary>
public sealed record SourceNotificationCapabilities(
    IReadOnlyList<string> UpdateFeedIds,
    IReadOnlyList<string> LiveFeedIds)
{
    public bool SupportsUpdates => UpdateFeedIds.Count > 0;
    public bool SupportsLive => LiveFeedIds.Count > 0;
    public bool HasAny => SupportsUpdates || SupportsLive;

    public static SourceNotificationCapabilities Resolve(
        SourceDefinition source,
        IEnumerable<FeedSubscription> subscriptions,
        bool supportsUpdates = true,
        bool supportsLive = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(subscriptions);
        var subscribedFeedIds = subscriptions
            .Where(subscription => subscription.IsEnabled
                && string.Equals(subscription.ProviderId, source.ProviderId, StringComparison.Ordinal)
                && string.Equals(subscription.SourceId, source.SourceId, StringComparison.Ordinal))
            .Select(subscription => subscription.FeedId)
            .ToHashSet(StringComparer.Ordinal);
        var feeds = source.Feeds.Where(feed => feed.SupportsNotifications
            && subscribedFeedIds.Contains(feed.FeedId)).ToArray();

        return new(
            supportsUpdates
                ? feeds.Where(feed => feed.Kind is "feed" or "rss" or "atom")
                    .Select(feed => feed.FeedId).Distinct(StringComparer.Ordinal).ToArray()
                : [],
            supportsLive
                ? feeds.Where(feed => feed.Kind == "live")
                    .Select(feed => feed.FeedId).Distinct(StringComparer.Ordinal).ToArray()
                : []);
    }

    public SourceNotificationRule CreateRule(SourceNotificationKey key, bool isAutomatic = false)
        => new(key.ProviderId, key.SourceId, UpdateFeedIds, LiveFeedIds,
            SupportsUpdates, SupportsLive, isAutomatic);

    /// <summary>Feed IDs describe current capabilities; the user's type switches remain authoritative.</summary>
    public SourceNotificationRule ApplyTo(SourceNotificationRule rule)
        => rule with { UpdateFeedIds = UpdateFeedIds, LiveFeedIds = LiveFeedIds };
}
