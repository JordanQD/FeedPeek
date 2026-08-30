using BiliRadar.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliRadar.Services;

internal static class NotificationBaselinePlanner
{
    internal sealed record Plan<T>(
        IReadOnlyList<T> NewItems,
        IReadOnlyList<string> KnownIds,
        bool InitializesBaseline);

    public static Plan<BiliVideoUpdate> ForVideos(
        IReadOnlyList<BiliVideoUpdate> updates,
        IReadOnlyList<string> knownIds,
        bool baselineInitialized,
        int maxNotifications,
        int maxStoredIds)
    {
        var orderedUpdates = updates
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .OrderByDescending(item => item.PublishedAt)
            .ToList();

        if (!baselineInitialized)
        {
            return new Plan<BiliVideoUpdate>(
                [],
                NormalizeIds(orderedUpdates.Select(item => item.Id), maxStoredIds),
                true);
        }

        var knownIdSet = knownIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = orderedUpdates
            .Where(item => !knownIdSet.Contains(item.Id))
            .Take(maxNotifications)
            .ToList();
        var nextKnownIds = NormalizeIds(
            orderedUpdates.Select(item => item.Id).Concat(knownIds),
            maxStoredIds);

        return new Plan<BiliVideoUpdate>(newItems, nextKnownIds, false);
    }

    public static Plan<BiliLiveCreator> ForLiveCreators(
        IReadOnlyList<BiliLiveCreator> liveCreators,
        IReadOnlyList<string> knownRoomIds,
        bool baselineInitialized,
        int maxNotifications,
        int maxStoredIds)
    {
        var currentLiveCreators = liveCreators
            .Where(item => item.RoomId > 0)
            .GroupBy(item => item.RoomId)
            .Select(group => group.First())
            .ToList();
        var currentRoomIds = currentLiveCreators.Select(item => item.RoomId.ToString()).ToList();

        if (!baselineInitialized)
        {
            return new Plan<BiliLiveCreator>(
                [],
                NormalizeIds(currentRoomIds, maxStoredIds),
                true);
        }

        var knownIdSet = knownRoomIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = currentLiveCreators
            .Where(item => !knownIdSet.Contains(item.RoomId.ToString()))
            .Take(maxNotifications)
            .ToList();

        return new Plan<BiliLiveCreator>(
            newItems,
            NormalizeIds(currentRoomIds, maxStoredIds),
            false);
    }

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string> ids, int maxStoredIds)
    {
        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxStoredIds)
            .ToList();
    }
}
