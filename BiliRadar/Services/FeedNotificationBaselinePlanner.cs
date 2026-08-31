using BiliRadar.Models;
using FeedPeek.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliRadar.Services;

internal static class FeedNotificationBaselinePlanner
{
    internal sealed record Plan<T>(
        IReadOnlyList<T> NewItems,
        IReadOnlyList<string> KnownIds,
        bool InitializesBaseline);

    public static Plan<FeedItem> ForFeedItems(
        IReadOnlyList<FeedItem> items,
        IReadOnlyList<string> knownIds,
        bool baselineInitialized,
        int maxNotifications,
        int maxStoredIds)
    {
        var orderedItems = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(GetFeedItemKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.PublishedAt)
            .ToList();
        if (!baselineInitialized)
            return new([], NormalizeIds(orderedItems.Select(GetFeedItemKey), maxStoredIds), true);

        var knownIdSet = knownIds.ToHashSet(StringComparer.Ordinal);
        var newItems = orderedItems
            .Where(item => !knownIdSet.Contains(GetFeedItemKey(item)))
            .Take(maxNotifications)
            .ToList();
        return new(
            newItems,
            NormalizeIds(orderedItems.Select(GetFeedItemKey).Concat(knownIds), maxStoredIds),
            false);
    }

    public static Plan<FeedLiveNotificationItem> ForLiveItems(
        IReadOnlyList<FeedLiveNotificationItem> items,
        IReadOnlyList<string> knownIds,
        bool baselineInitialized,
        int maxNotifications,
        int maxStoredIds)
    {
        var currentItems = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Item.CreatorKey))
            .GroupBy(item => item.StableKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (!baselineInitialized)
            return new([], NormalizeIds(currentItems.Select(item => item.StableKey), maxStoredIds), true);

        var knownIdSet = knownIds.ToHashSet(StringComparer.Ordinal);
        var newItems = currentItems
            .Where(item => !knownIdSet.Contains(item.StableKey))
            .Take(maxNotifications)
            .ToList();
        return new(
            newItems,
            NormalizeIds(currentItems.Select(item => item.StableKey), maxStoredIds),
            false);
    }

    private static string GetFeedItemKey(FeedItem item)
        => $"{item.ProviderId}\n{item.SourceId}\n{item.FeedId}\n{item.ItemId}";

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string> ids, int maxStoredIds)
        => ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(maxStoredIds)
            .ToList();
}
