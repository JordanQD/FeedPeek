using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Feeds;

public sealed class FeedSession
{
    private readonly IFeedStore _store;
    private readonly FeedPollingService _pollingService;

    public FeedSession(IFeedStore store, FeedPollingService pollingService)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pollingService = pollingService ?? throw new ArgumentNullException(nameof(pollingService));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _store.InitializeAsync(cancellationToken);

    public Task AddSubscriptionAsync(
        SourceDefinition source,
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => _store.SaveSourceAndSubscriptionAsync(source, subscription, cancellationToken);

    public async Task<IReadOnlyList<FeedPollResult>> RefreshAllAsync(
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await _store.GetSubscriptionsAsync(cancellationToken);
        var results = new List<FeedPollResult>();
        foreach (var subscription in subscriptions.Where(item => item.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _pollingService.PollAsync(subscription, cancellationToken));
        }

        return results;
    }

    public Task<FeedPollResult> RefreshAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => _pollingService.PollAsync(subscription, cancellationToken);

    public Task<FeedLoadMoreResult> LoadMoreAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => _pollingService.LoadMoreAsync(subscription, cancellationToken);

    public Task<bool> HasMoreAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => _pollingService.HasMoreAsync(subscription, cancellationToken);

    public Task<IReadOnlyList<FeedSubscription>> GetSubscriptionsAsync(
        CancellationToken cancellationToken = default)
        => _store.GetSubscriptionsAsync(cancellationToken);

    public Task<IReadOnlyList<StoredFeedItem>> GetItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken = default)
        => _store.GetItemsAsync(query, cancellationToken);

    public Task<IReadOnlyList<FeedSourceSummary>> GetSourceSummariesAsync(
        CancellationToken cancellationToken = default)
        => _store.GetSourceSummariesAsync(cancellationToken);

    public Task MarkReadAsync(
        FeedItemKey key,
        bool isRead = true,
        CancellationToken cancellationToken = default)
        => _store.MarkReadAsync(key, isRead, cancellationToken);

    public Task MarkSavedAsync(
        FeedItemKey key,
        bool isSaved = true,
        CancellationToken cancellationToken = default)
        => _store.MarkSavedAsync(key, isSaved, cancellationToken);

    public Task DeleteSourceAsync(
        string providerId,
        string sourceId,
        CancellationToken cancellationToken = default)
        => _store.DeleteSourceAsync(providerId, sourceId, cancellationToken);
}
