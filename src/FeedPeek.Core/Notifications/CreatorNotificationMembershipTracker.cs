using FeedPeek.Core.Models;

namespace FeedPeek.Core.Notifications;

/// <summary>
/// Keeps the last trusted membership of a provider/source pair while its
/// following query is being refreshed. A failed query never changes membership.
/// </summary>
public sealed class CreatorNotificationMembershipTracker
{
    private readonly Dictionary<SourceNotificationKey, MembershipState> _states = [];

    /// <summary>
    /// Applies one successful following response. Complete responses replace the
    /// membership; incomplete responses only add positively observed creators.
    /// </summary>
    public CreatorNotificationMembershipSnapshot Observe(
        SourceNotificationKey source,
        string generation,
        FollowingCreatorsPage page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Creators);

        if (!_states.TryGetValue(source, out var state)
            || !string.Equals(state.Generation, generation, StringComparison.Ordinal))
        {
            state = new MembershipState(generation);
            _states[source] = state;
        }

        var current = page.IsComplete
            ? new Dictionary<string, CreatorDescriptor>(StringComparer.Ordinal)
            : new Dictionary<string, CreatorDescriptor>(state.Creators, StringComparer.Ordinal);
        foreach (var creator in page.Creators)
        {
            if (creator is null
                || !string.Equals(creator.ProviderId, source.ProviderId, StringComparison.Ordinal)
                || !string.Equals(creator.SourceId, source.SourceId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(creator.CreatorKey))
            {
                continue;
            }

            current[creator.CreatorKey] = creator;
        }

        state.Creators = current;
        state.IsComplete = page.IsComplete;
        return Snapshot(state, canObserve: true);
    }

    /// <summary>
    /// Returns the last membership for a failed query. It is safe to retain the
    /// watches, but callers must skip ObserveUpdates/ObserveLive when CanObserve
    /// is false.
    /// </summary>
    public CreatorNotificationMembershipSnapshot GetSnapshot(
        SourceNotificationKey source,
        string generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        if (!_states.TryGetValue(source, out var state))
            return CreatorNotificationMembershipSnapshot.Empty;
        if (!string.Equals(state.Generation, generation, StringComparison.Ordinal))
        {
            _states.Remove(source);
            return CreatorNotificationMembershipSnapshot.Empty;
        }

        return Snapshot(state, canObserve: false);
    }

    public void Clear(SourceNotificationKey source)
        => _states.Remove(source);

    public void Prune(IEnumerable<SourceNotificationKey> activeSources)
    {
        ArgumentNullException.ThrowIfNull(activeSources);
        var active = activeSources.ToHashSet();
        foreach (var source in _states.Keys.Where(source => !active.Contains(source)).ToArray())
            _states.Remove(source);
    }

    /// <summary>
    /// Expands source-wide and creator-specific watches to currently retained
    /// creators. The feed kind is intersected with the creator's capabilities.
    /// </summary>
    public static IReadOnlyList<NotificationWatchKey> ExpandWatches(
        NotificationFeedKey feed,
        IEnumerable<NotificationWatchKey> sourceAndCreatorWatches,
        CreatorNotificationMembershipSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(sourceAndCreatorWatches);
        ArgumentNullException.ThrowIfNull(snapshot);

        var result = new HashSet<NotificationWatchKey>();
        foreach (var watch in sourceAndCreatorWatches)
        {
            if (watch.Feed != feed)
                continue;

            if (watch.CreatorKey is null)
            {
                foreach (var creator in snapshot.Creators.Values)
                    if (Supports(creator, feed.Kind))
                        result.Add(new NotificationWatchKey(feed, creator.CreatorKey));
                continue;
            }

            if (snapshot.Creators.TryGetValue(watch.CreatorKey, out var selected)
                && Supports(selected, feed.Kind))
            {
                result.Add(new NotificationWatchKey(feed, watch.CreatorKey));
            }
        }

        return result.ToArray();
    }

    private static bool Supports(CreatorDescriptor creator, NotificationKind kind)
        => kind == NotificationKind.Updates
            ? creator.SupportedNotificationKinds.Contains("video", StringComparer.Ordinal)
                || creator.SupportedNotificationKinds.Contains("updates", StringComparer.Ordinal)
            : creator.SupportedNotificationKinds.Contains("live", StringComparer.Ordinal);

    private static CreatorNotificationMembershipSnapshot Snapshot(
        MembershipState state,
        bool canObserve)
        => new(
            new Dictionary<string, CreatorDescriptor>(state.Creators, StringComparer.Ordinal),
            state.IsComplete,
            canObserve);

    private sealed class MembershipState(string generation)
    {
        public string Generation { get; } = generation;
        public Dictionary<string, CreatorDescriptor> Creators { get; set; } = new(StringComparer.Ordinal);
        public bool IsComplete { get; set; }
    }
}

public sealed record CreatorNotificationMembershipSnapshot(
    IReadOnlyDictionary<string, CreatorDescriptor> Creators,
    bool IsComplete,
    bool CanObserve)
{
    public static CreatorNotificationMembershipSnapshot Empty
        => new(new Dictionary<string, CreatorDescriptor>(StringComparer.Ordinal), false, false);
}
