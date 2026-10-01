using FeedPeek.Core.Models;

namespace FeedPeek.Core.Feeds;

/// <summary>Foreground and notification sessions serialize a feed's request, state and snapshot commit.</summary>
internal static class FeedPollCoordinator
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Provider, string Source, string Feed), Entry> Entries = [];

    public static async Task<IDisposable> EnterAsync(FeedSubscription subscription, CancellationToken token)
    {
        var key = (subscription.ProviderId, subscription.SourceId, subscription.FeedId);
        Entry entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries[key] = entry = new();
            entry.Users++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(token);
            return new Lease(key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    private static void ReleaseReference((string Provider, string Source, string Feed) key, Entry entry)
    {
        lock (Gate)
            if (--entry.Users == 0)
            {
                Entries.Remove(key);
                entry.Semaphore.Dispose();
            }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Lease((string Provider, string Source, string Feed) key, Entry entry) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            entry.Semaphore.Release();
            ReleaseReference(key, entry);
        }
    }
}
