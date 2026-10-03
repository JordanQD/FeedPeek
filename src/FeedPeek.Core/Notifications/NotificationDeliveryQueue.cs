namespace FeedPeek.Core.Notifications;

/// <summary>Retains planned events on temporary disk until submitted, while an original watch generation remains active.</summary>
public sealed class NotificationDeliveryQueue<TKey, TItem>(Func<TItem, TKey> getKey) : IDisposable where TKey : notnull
{
    public const int DeliveryBatchSize = 256;
    private const int ScanLimit = 4096;
    private readonly NotificationDiskMap<TKey, Pending> _pending = new();
    private long _nextSequence;
    private long _cycleEndSequence;

    public int Count => _pending.Count;

    /// <summary>Synchronous scope for enqueuing many events; do not hold it across an await.</summary>
    public IDisposable BeginBatch() => _pending.BeginBatch();

    public void Enqueue(TItem item, IEnumerable<NotificationWatch> watches)
    {
        var eligible = watches.ToArray();
        if (eligible.Length == 0) return;
        var key = getKey(item);
        if (!_pending.TryGetValue(key, out var previous))
            _pending.TryAdd(key, new(item, eligible));
        else
        {
            // Merge independently planned watches, replacing obsolete generations
            // for the same watch identity rather than accumulating old generations.
            var merged = previous.Watches.Where(watch => !eligible.Any(next => next.Key == watch.Key))
                .Concat(eligible).Distinct().ToArray();
            _pending.Set(key, new(item, merged));
        }
    }

    public IReadOnlyList<TItem> GetPending(IReadOnlyList<NotificationWatch> currentWatches,
        Func<TItem, bool>? canObserve = null)
    {
        var active = currentWatches.ToDictionary(watch => watch.Key, watch => watch.Generation);
        if (active.Count == 0)
        {
            _pending.Clear();
            _nextSequence = 0;
            _cycleEndSequence = 0;
            return [];
        }
        var items = new List<TItem>();
        if (_nextSequence >= _cycleEndSequence)
        {
            _nextSequence = 0;
            _cycleEndSequence = _pending.LastSequence;
        }
        var toScan = Math.Min(_pending.Count, ScanLimit);
        using var batch = _pending.BeginBatch();
        while (toScan > 0 && items.Count < DeliveryBatchSize)
        {
            // Freeze each scan cycle's end. Continually arriving new events must
            // not keep older temporarily unavailable events behind the cursor forever.
            var entries = _pending.ReadBatch(Math.Min(DeliveryBatchSize, toScan), _nextSequence, _cycleEndSequence);
            if (entries.Count == 0)
            {
                _nextSequence = _cycleEndSequence;
                break;
            }
            foreach (var entry in entries)
            {
                _nextSequence = entry.Sequence;
                toScan--;
                if (!entry.Value.Watches.Any(watch => active.TryGetValue(watch.Key, out var generation)
                    && generation == watch.Generation))
                    _pending.Remove(entry.Key);
                else if (canObserve?.Invoke(entry.Value.Item) != false)
                    items.Add(entry.Value.Item);
                if (items.Count == DeliveryBatchSize) break;
            }
        }
        return items;
    }

    public void Acknowledge(TItem item) => _pending.Remove(getKey(item));

    public void RemoveWhere(Func<TItem, bool> predicate)
    {
        long after = 0;
        using var batch = _pending.BeginBatch();
        while (true)
        {
            var entries = _pending.ReadBatch(afterSequence: after);
            if (entries.Count == 0) break;
            foreach (var entry in entries)
                if (predicate(entry.Value.Item)) _pending.Remove(entry.Key);
            after = entries[^1].Sequence;
        }
    }

    public void Dispose() => _pending.Dispose();

    private sealed record Pending(TItem Item, IReadOnlyList<NotificationWatch> Watches);
}
