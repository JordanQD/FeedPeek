using FeedPeek.Core.Models;

namespace FeedPeek.Core.Feeds;

/// <summary>Shares only the latest normal refresh page across foreground/background sessions in this process.</summary>
public static class FeedPollSnapshotCache
{
    private const int Capacity = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Snapshot> Snapshots = new(StringComparer.Ordinal);

    public static void Record(FeedSubscription subscription, IReadOnlyList<FeedItem> items)
    {
        lock (Gate)
        {
            Snapshots[subscription.SubscriptionId] = new(subscription, items.ToArray(), DateTimeOffset.UtcNow);
            if (Snapshots.Count > Capacity)
                Snapshots.Remove(Snapshots.MinBy(pair => pair.Value.RecordedAt).Key);
        }
    }

    public static IReadOnlyList<FeedItem> Read(FeedSubscription subscription)
    {
        lock (Gate)
            return Snapshots.TryGetValue(subscription.SubscriptionId, out var snapshot)
                && snapshot.Subscription.ProviderId == subscription.ProviderId
                && snapshot.Subscription.SourceId == subscription.SourceId
                && snapshot.Subscription.FeedId == subscription.FeedId
                    ? snapshot.Items : [];
    }

    public static void RemoveSource(string providerId, string sourceId)
    {
        lock (Gate)
            foreach (var key in Snapshots.Where(pair => pair.Value.Subscription.ProviderId == providerId
                && pair.Value.Subscription.SourceId == sourceId).Select(pair => pair.Key).ToArray())
                Snapshots.Remove(key);
    }

    private sealed record Snapshot(FeedSubscription Subscription, IReadOnlyList<FeedItem> Items, DateTimeOffset RecordedAt);
}
