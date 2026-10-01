using FeedPeek.Core.Models;

namespace FeedPeek.Core.Notifications;

public enum NotificationKind { Updates, Live }
public enum NotificationSelectionMode { None, AllSources, Custom }

public readonly record struct NotificationFeedKey(string ProviderId, string SourceId, string FeedId, NotificationKind Kind);
public readonly record struct NotificationWatchKey(NotificationFeedKey Feed, string? CreatorKey = null);
public sealed record NotificationWatch(NotificationWatchKey Key, string Generation);
public sealed record CreatorNotificationRule(string ProviderId, string SourceId, string CreatorKey, bool UpdatesEnabled, bool LiveEnabled);
public sealed record NotificationSelection(NotificationSelectionMode Mode, bool UpdatesEnabled, bool LiveEnabled,
    IReadOnlyList<SourceNotificationRule> Sources, IReadOnlyList<CreatorNotificationRule> Creators,
    IReadOnlyList<CreatorNotificationPolicy>? CreatorPolicies = null);
public sealed record NotificationUpdateObservation(FeedItem Item, DateTimeOffset FirstSeenAt);
public sealed record NotificationLiveObservation(string StableKey, string CreatorKey);

/// <summary>Pure selection by actual notification-capable feed, independent of plugin feed IDs.</summary>
public static class NotificationWatchSelector
{
    public static IReadOnlyList<NotificationWatchKey> Select(NotificationFeedKey feed, NotificationSelection selection)
    {
        if (selection.Mode == NotificationSelectionMode.None)
            return [];
        if (selection.Mode == NotificationSelectionMode.AllSources)
            return (feed.Kind == NotificationKind.Updates ? selection.UpdatesEnabled : selection.LiveEnabled)
                ? [new(feed)] : [];

        var policy = selection.CreatorPolicies?.FirstOrDefault(item => item.Key ==
            new SourceNotificationKey(feed.ProviderId, feed.SourceId));
        if (policy is not null)
        {
            var isUpdates = feed.Kind == NotificationKind.Updates;
            var enabled = isUpdates ? policy.UpdatesEnabled : policy.LiveEnabled;
            if (!enabled)
                return [];

            var scope = isUpdates ? policy.UpdatesScope : policy.LiveScope;
            if (scope == CreatorNotificationScope.AllFollowing)
                return [new(feed)];

            return selection.Creators
                .Where(rule => rule.ProviderId == feed.ProviderId && rule.SourceId == feed.SourceId
                    && !string.IsNullOrWhiteSpace(rule.CreatorKey)
                    && (isUpdates ? rule.UpdatesEnabled : rule.LiveEnabled))
                .Select(rule => new NotificationWatchKey(feed, rule.CreatorKey))
                .Distinct()
                .ToArray();
        }

        var keys = new List<NotificationWatchKey>();
        if (selection.Sources.Any(rule => rule.ProviderId == feed.ProviderId && rule.SourceId == feed.SourceId
            && (feed.Kind == NotificationKind.Updates
                ? rule.UpdateNotificationsEnabled && rule.UpdateFeedIds.Contains(feed.FeedId, StringComparer.Ordinal)
                : rule.LiveNotificationsEnabled && rule.LiveFeedIds.Contains(feed.FeedId, StringComparer.Ordinal))))
            keys.Add(new(feed));
        keys.AddRange(selection.Creators.Where(rule => rule.ProviderId == feed.ProviderId && rule.SourceId == feed.SourceId
                && !string.IsNullOrWhiteSpace(rule.CreatorKey)
                && (feed.Kind == NotificationKind.Updates ? rule.UpdatesEnabled : rule.LiveEnabled))
            .Select(rule => new NotificationWatchKey(feed, rule.CreatorKey)));
        return keys.Distinct().ToArray();
    }
}

/// <summary>One process only. Failed queries never call Observe, and so cannot establish an offline state.</summary>
public sealed class NotificationBaselineTracker
{
    private readonly Dictionary<NotificationWatchKey, WatchState> _states = [];

    public void Synchronize(IReadOnlyList<NotificationWatch> watches)
    {
        var desired = watches.ToDictionary(watch => watch.Key);
        foreach (var removed in _states.Keys.Where(key => !desired.ContainsKey(key)).ToArray())
            _states.Remove(removed);
        foreach (var watch in watches)
            if (!_states.TryGetValue(watch.Key, out var state) || state.Generation != watch.Generation)
                _states[watch.Key] = new(watch.Generation);
    }

    public IReadOnlyList<FeedItem> ObserveUpdates(NotificationFeedKey feed,
        IReadOnlyList<NotificationUpdateObservation> items, DateTimeOffset observedAt)
    {
        var notifications = new Dictionary<FeedItemKey, FeedItem>();
        foreach (var (key, state) in _states.Where(pair => pair.Key.Feed == feed))
        {
            var current = items.Where(item => item.Item.ProviderId == feed.ProviderId
                && item.Item.SourceId == feed.SourceId && item.Item.FeedId == feed.FeedId
                && (key.CreatorKey is null || item.Item.CreatorKey == key.CreatorKey)).ToArray();
            if (!state.Initialized)
            {
                state.Initialized = true;
                state.BaselineAt = observedAt;
                foreach (var item in current) state.SeenUpdateIds.Add(item.Item.ItemId);
                continue;
            }
            foreach (var item in current)
                if (state.SeenUpdateIds.Add(item.Item.ItemId) && item.FirstSeenAt > state.BaselineAt)
                    notifications.TryAdd(item.Item.Key, item.Item);
        }
        return notifications.Values.OrderBy(item => item.PublishedAt).ToArray();
    }

    public IReadOnlyList<string> ObserveLive(NotificationFeedKey feed, IReadOnlyList<NotificationLiveObservation> items)
    {
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, state) in _states.Where(pair => pair.Key.Feed == feed))
        {
            var current = items.Where(item => key.CreatorKey is null || item.CreatorKey == key.CreatorKey)
                .Select(item => item.StableKey).ToHashSet(StringComparer.Ordinal);
            if (state.Initialized)
                notifications.UnionWith(current.Except(state.OnlineLiveIds));
            state.Initialized = true;
            state.OnlineLiveIds = current;
        }
        return notifications.ToArray();
    }

    private sealed class WatchState(string generation)
    {
        public string Generation { get; } = generation;
        public bool Initialized { get; set; }
        public DateTimeOffset BaselineAt { get; set; }
        public HashSet<string> SeenUpdateIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OnlineLiveIds { get; set; } = new(StringComparer.Ordinal);
    }
}
