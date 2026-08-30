using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Feeds;

public sealed record FeedPollResult(
    FeedSubscription Subscription,
    IReadOnlyList<FeedItem> NewItems,
    bool InitializedBaseline,
    bool NotModified,
    DateTimeOffset CompletedAt);

public sealed class FeedPollingService
{
    private readonly IFeedStore _store;
    private readonly IFeedProviderResolver _providers;
    private readonly TimeProvider _timeProvider;

    public FeedPollingService(
        IFeedStore store,
        IFeedProviderResolver providers,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FeedPollResult> PollAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var state = await _store.GetPollingStateAsync(subscription.SubscriptionId, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        try
        {
            var provider = _providers.Resolve(subscription.ProviderId);
            var page = await provider.GetPageAsync(
                new FeedPageRequest(subscription, null, 200, state?.ETag, state?.LastModified),
                cancellationToken);

            var initializedBaseline = state?.BaselineInitialized != true;
            IReadOnlyList<FeedItem> newItems = [];
            if (!page.NotModified)
            {
                var upsert = await _store.UpsertItemsAsync(
                    subscription.SubscriptionId,
                    page.Items,
                    now,
                    cancellationToken);
                newItems = initializedBaseline ? [] : upsert.InsertedItems;
            }

            await _store.SavePollingStateAsync(
                new FeedPollingState(
                    subscription.SubscriptionId,
                    page.ETag ?? state?.ETag,
                    page.LastModified ?? state?.LastModified,
                    page.NextCursor,
                    true,
                    now,
                    now,
                    0,
                    null),
                cancellationToken);

            return new FeedPollResult(subscription, newItems, initializedBaseline, page.NotModified, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _store.SavePollingStateAsync(
                new FeedPollingState(
                    subscription.SubscriptionId,
                    state?.ETag,
                    state?.LastModified,
                    state?.NextCursor,
                    state?.BaselineInitialized == true,
                    now,
                    state?.LastSuccessAt,
                    (state?.FailureCount ?? 0) + 1,
                    ex.Message),
                cancellationToken);
            throw;
        }
    }
}
