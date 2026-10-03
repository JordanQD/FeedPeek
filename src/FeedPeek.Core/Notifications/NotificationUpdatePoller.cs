using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using System.Globalization;
using System.Text.Json;

namespace FeedPeek.Core.Notifications;

public sealed record NotificationCatchUpOptions(int MaxRequests = 8, int MaxItems = 2000,
    TimeSpan? Timeout = null);

public sealed record NotificationCatchUpResult(NotificationFeedKey Feed, string Generation,
    IReadOnlyList<NotificationUpdateObservation> Items, DateTimeOffset ObservedAt,
    bool IsComplete, int PendingGaps, string? ContinuationCursor, string? Error,
    bool CanInitializeBaseline = true);

/// <summary>
/// Process-local notification cursors. A new process deliberately starts a silent baseline.
/// The caller serializes polls/pruning/acknowledgments; feed writes also share the foreground lease.
/// Historical ledgers, unfinished gaps and pending observations use temporary disk storage.
/// </summary>
public sealed class NotificationUpdatePoller : IDisposable
{
    private readonly IFeedStore _store;
    private readonly IFeedProviderResolver _providers;
    private readonly TimeProvider _clock;
    private readonly NotificationCatchUpOptions _options;
    private readonly Dictionary<NotificationFeedKey, Progress> _progress = [];
    private bool _disposed;

    public NotificationUpdatePoller(IFeedStore store, IFeedProviderResolver providers,
        TimeProvider? clock = null, NotificationCatchUpOptions? options = null)
    {
        _store = store;
        _providers = providers;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new();
        if (_options.MaxRequests < 2 || _options.MaxItems < 1
            || (_options.Timeout ?? TimeSpan.FromSeconds(20)) <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public void Prune(IEnumerable<NotificationFeedKey> activeFeeds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var active = activeFeeds.ToHashSet();
        foreach (var key in _progress.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            _progress[key].Dispose();
            _progress.Remove(key);
        }
    }

    // A discarded poll (e.g. settings changed while awaiting the network) must not lose fetched events.
    public void Acknowledge(NotificationCatchUpResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_progress.TryGetValue(result.Feed, out var state) || state.Generation != result.Generation) return;
        using var batch = state.Pending.BeginBatch();
        foreach (var item in result.Items) state.Pending.Remove(item.Item.Key);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _progress.Values) state.Dispose();
        _progress.Clear();
    }

    public async Task<NotificationCatchUpResult> PollAsync(FeedSubscription subscription, string generation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId,
            subscription.FeedId, NotificationKind.Updates);
        using var lease = await FeedPollCoordinator.EnterAsync(subscription, cancellationToken);
        var current = await _store.GetSubscriptionAsync(subscription.SubscriptionId, cancellationToken);
        if (current is null || !current.IsEnabled || current.ProviderId != subscription.ProviderId
            || current.SourceId != subscription.SourceId || current.FeedId != subscription.FeedId
            || current.CredentialProfileId != subscription.CredentialProfileId || current.Endpoint != subscription.Endpoint)
            throw new InvalidOperationException("The notification subscription changed or is unavailable.");
        if (!_progress.TryGetValue(key, out var state) || state.Generation != generation)
        {
            state?.Dispose();
            _progress[key] = state = new(generation);
        }

        using var budget = new CancellationTokenSource(_options.Timeout ?? TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var token = linked.Token;
        var requests = 0;
        var count = 0;
        var headSucceeded = false;
        string? error = null;
        try
        {
            var provider = _providers.Resolve(subscription.ProviderId);
            requests++;
            var response = await provider.GetPageAsync(new(current, null, 200,
                state.ETag, state.LastModified), token).ConfigureAwait(false);
            if (response.NotModified)
            {
                if (!state.HasHead) throw new InvalidOperationException("304 without a notification baseline.");
            }
            else
            {
                ValidatePage(response, key);
                count += CountNew(response, state);
                await StageAsync(current, response, state, token).ConfigureAwait(false);
                var headKeys = response.Items.Select(item => item.Key).ToHashSet();
                if (state.HeadKeys.Count > 0 && response.HasMore && !headKeys.Overlaps(state.HeadKeys)
                    && (headKeys.Count > 0 || !state.EmptyHeadGapOpened))
                {
                    state.AddGap(response.NextCursor!, state.HeadKeys, headKeys);
                    if (headKeys.Count == 0) state.EmptyHeadGapOpened = true;
                }
                // Empty mapped heads do not replace the last valid anchor. Without
                // an anchor, wait for a valid head rather than scanning all history.
                if (headKeys.Count > 0)
                {
                    state.HeadKeys = headKeys;
                    state.EmptyHeadGapOpened = false;
                }
                state.HasHead = true;
                state.ETag = response.ETag;
                state.LastModified = response.LastModified;
            }
            headSucceeded = true;

            var deferred = new HashSet<long>();
            while (requests < _options.MaxRequests && count < _options.MaxItems)
            {
                var entry = state.NextGap(deferred);
                if (entry is null) break;
                var gapId = entry.Value.Key;
                var gap = entry.Value.Value;
                if (state.Gaps.Count > 1) state.NextGapSequence = entry.Value.Sequence;
                try
                {
                    token.ThrowIfCancellationRequested();
                    requests++;
                    var page = await provider.GetPageAsync(new(current, gap.Cursor, 200), token).ConfigureAwait(false);
                    ValidatePage(page, key);
                    if (page.NotModified) throw new InvalidOperationException("304 is invalid for a continuation page.");
                    count += CountNew(page, state);
                    await StageAsync(current, page, state, token).ConfigureAwait(false);
                    var keys = page.Items.Select(item => item.Key).ToHashSet();
                    if (keys.Overlaps(gap.Target) || !page.HasMore)
                    {
                        state.Gaps.Remove(gapId);
                        state.Markers.RemoveScope(Scope(gapId));
                        continue;
                    }
                    if (page.NextCursor == gap.Cursor
                        || state.Markers.ContainsKey(new(gapId, "cursor", page.NextCursor!)))
                        throw new InvalidOperationException("Repeated notification cursor; retaining catch-up gap.");

                    var newItems = false;
                    using (state.Markers.BeginBatch())
                    {
                        foreach (var item in keys)
                            newItems |= state.Markers.TryAdd(new(gapId, "item", JsonSerializer.Serialize(item)), true);
                        state.Markers.TryAdd(new(gapId, "cursor", gap.Cursor), true);
                    }
                    var noProgress = newItems ? 0 : gap.NoProgressPages + 1;
                    state.Gaps.Set(gapId, gap with { Cursor = page.NextCursor!, NoProgressPages = noProgress >= 2 ? 0 : noProgress });
                    if (noProgress >= 2)
                    {
                        error = "Notification pagination made no item progress; retaining continuation.";
                        deferred.Add(gapId);
                        state.NextGapSequence = entry.Value.Sequence;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    error = budget.IsCancellationRequested ? "Notification catch-up time limit reached." : exception.Message;
                    deferred.Add(gapId);
                    state.NextGapSequence = entry.Value.Sequence;
                    if (budget.IsCancellationRequested) break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Existing watches may process reliable staged pages, but new watches
            // must await a successful current head before establishing a baseline.
            if (!state.HasHead) throw;
            error = budget.IsCancellationRequested ? "Notification catch-up time limit reached." : exception.Message;
        }
        var pending = state.Pending.ReadBatch(Math.Min(_options.MaxItems, 2000)).Select(entry => entry.Value).ToArray();
        var gapCount = state.Gaps.Count;
        return new(key, generation, pending, _clock.GetUtcNow(),
            gapCount == 0 && error is null && state.Pending.Count <= pending.Length, gapCount,
            state.Gaps.ReadBatch(1).FirstOrDefault().Value?.Cursor, error, headSucceeded);
    }

    private static int CountNew(FeedPage page, Progress state)
        => page.Items.Select(item => item.Key).Distinct().Count(item => !state.Observed.ContainsKey(item));

    private async Task StageAsync(FeedSubscription subscription, FeedPage page, Progress state, CancellationToken token)
    {
        var now = _clock.GetUtcNow();
        await _store.UpsertItemsAsync(subscription.SubscriptionId, page.Items, now, token).ConfigureAwait(false);
        if (page.Items.Count == 0) return;
        foreach (var ids in page.Items.Select(item => item.ItemId).Distinct().Chunk(1000))
        {
            var stored = await _store.GetItemsAsync(new FeedQuery(ProviderId: subscription.ProviderId,
                SourceId: subscription.SourceId, FeedId: subscription.FeedId, Limit: ids.Length,
                ItemIds: ids), token).ConfigureAwait(false);
            // Synchronous transaction scopes must never span an await.
            using var observedBatch = state.Observed.BeginBatch();
            using var pendingBatch = state.Pending.BeginBatch();
            foreach (var item in stored)
                if (!state.Observed.ContainsKey(item.Item.Key) || state.Pending.ContainsKey(item.Item.Key))
                {
                    state.Pending.Set(item.Item.Key, new(item.Item, item.FirstSeenAt));
                    state.Observed.TryAdd(item.Item.Key, true);
                }
        }
    }

    private static string Scope(long gapId) => gapId.ToString(CultureInfo.InvariantCulture);

    private static void ValidatePage(FeedPage page, NotificationFeedKey feed)
    {
        if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor))
            throw new InvalidOperationException("Notification page hasMore without a cursor.");
        if (page.Items.Any(item => item.ProviderId != feed.ProviderId || item.SourceId != feed.SourceId
            || item.FeedId != feed.FeedId || string.IsNullOrWhiteSpace(item.ItemId)))
            throw new InvalidOperationException("Notification page returned an invalid item identity.");
    }

    private sealed class Progress : IDisposable
    {
        public Progress(string generation)
        {
            Generation = generation;
            var allocated = new List<IDisposable>();
            try
            {
                Gaps = new();
                allocated.Add(Gaps);
                Markers = new(marker => Scope(marker.GapId));
                allocated.Add(Markers);
                Pending = new();
                allocated.Add(Pending);
                Observed = new();
                allocated.Add(Observed);
            }
            catch
            {
                foreach (var resource in allocated) resource.Dispose();
                throw;
            }
        }

        public string Generation { get; }
        public bool HasHead { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastModified { get; set; }
        public HashSet<FeedItemKey> HeadKeys { get; set; } = [];
        public bool EmptyHeadGapOpened { get; set; }
        public long NextGapSequence { get; set; }
        private long _nextGapId;
        public NotificationDiskMap<long, Gap> Gaps { get; }
        public NotificationDiskMap<GapMarker, bool> Markers { get; }
        public NotificationDiskMap<FeedItemKey, NotificationUpdateObservation> Pending { get; }
        public NotificationDiskMap<FeedItemKey, bool> Observed { get; }

        public void AddGap(string cursor, HashSet<FeedItemKey> target, HashSet<FeedItemKey> headKeys)
        {
            var id = ++_nextGapId;
            using (Markers.BeginBatch())
                foreach (var item in headKeys)
                    Markers.TryAdd(new(id, "item", JsonSerializer.Serialize(item)), true);
            Gaps.Set(id, new(cursor, target, 0));
        }

        public NotificationDiskEntry<long, Gap>? NextGap(HashSet<long> deferred)
        {
            var total = Gaps.Count;
            var after = NextGapSequence;
            for (var i = 0; i < total && deferred.Count < total; i++)
            {
                var entries = Gaps.ReadBatch(1, after);
                if (entries.Count == 0) entries = Gaps.ReadBatch(1);
                if (entries.Count == 0) return null;
                var entry = entries[0];
                if (!deferred.Contains(entry.Key)) return entry;
                after = entry.Sequence;
            }
            return null;
        }

        public void Dispose()
        {
            Gaps.Dispose();
            Markers.Dispose();
            Pending.Dispose();
            Observed.Dispose();
        }
    }

    private readonly record struct GapMarker(long GapId, string Kind, string Value);
    private sealed record Gap(string Cursor, HashSet<FeedItemKey> Target, int NoProgressPages);
}
