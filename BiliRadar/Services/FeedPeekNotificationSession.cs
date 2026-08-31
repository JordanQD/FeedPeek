using BiliRadar.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace BiliRadar.Services;

internal sealed class FeedPeekNotificationSession : IDisposable, IAsyncDisposable
{
    private readonly SqliteFeedStore _store;
    private readonly RssFeedProvider _rssProvider = new();
    private readonly FeedPeekPluginRuntime _pluginRuntime;
    private readonly FeedProviderRegistry _providerRegistry;
    private readonly FeedPollingService _pollingService;
    private readonly FeedSession _feedSession;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public FeedPeekNotificationSession(CookieStore cookieStore)
    {
        ArgumentNullException.ThrowIfNull(cookieStore);
        var databasePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "FeedPeek",
            "feedpeek.db");
        _store = new SqliteFeedStore(databasePath);
        _pluginRuntime = new FeedPeekPluginRuntime(cookieStore);
        _providerRegistry = new FeedProviderRegistry([_rssProvider]);
        _pollingService = new FeedPollingService(_store, _providerRegistry);
        _feedSession = new FeedSession(_store, _pollingService);
    }

    public Task<IReadOnlyList<FeedItem>> RefreshUpdatesAsync(
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async token =>
            {
                await EnsureInitializedAsync(token).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
                var newItems = new List<FeedItem>();
                foreach (var subscription in subscriptions.Where(item =>
                    item.IsEnabled && string.Equals(item.FeedId, PluginFeedIds.Updates, StringComparison.Ordinal)))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var poll = await _feedSession.RefreshAsync(subscription, token).ConfigureAwait(false);
                        newItems.AddRange(poll.NewItems);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // A failing source must not prevent other sources from producing notifications.
                        NotificationService.TraceNotification(
                            $"Source refresh failed. provider={subscription.ProviderId}, " +
                            $"source={subscription.SourceId}, feed={subscription.FeedId}: " +
                            $"{exception.GetType().Name}: {exception.Message}");
                    }
                }

                return (IReadOnlyList<FeedItem>)newItems
                    .GroupBy(item => item.Key)
                    .Select(group => group.First())
                    .OrderByDescending(item => item.PublishedAt)
                    .ToArray();
            },
            cancellationToken);

    public Task<IReadOnlyList<CreatorDescriptor>> GetFollowingCreatorsAsync(
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async token =>
            {
                await EnsureInitializedAsync(token).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
                var creators = new List<CreatorDescriptor>();
                foreach (var subscription in subscriptions.Where(item =>
                    item.IsEnabled
                    && _pluginRuntime.IsPluginProvider(item.ProviderId)
                    && string.Equals(item.FeedId, PluginFeedIds.Updates, StringComparison.Ordinal)))
                {
                    creators.AddRange(await _pluginRuntime.GetFollowingCreatorsAsync(subscription, token)
                        .ConfigureAwait(false));
                }
                return (IReadOnlyList<CreatorDescriptor>)creators
                    .GroupBy(item => (item.ProviderId, item.SourceId, item.CreatorKey))
                    .Select(group => group.First())
                    .ToArray();
            },
            cancellationToken);

    public Task<IReadOnlyList<FeedLiveNotificationItem>> GetLiveItemsAsync(
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async token =>
            {
                await EnsureInitializedAsync(token).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
                var result = new List<FeedLiveNotificationItem>();
                foreach (var subscription in subscriptions.Where(item =>
                    item.IsEnabled
                    && _pluginRuntime.IsPluginProvider(item.ProviderId)
                    && string.Equals(item.FeedId, PluginFeedIds.Live, StringComparison.Ordinal)))
                {
                    var items = await _pluginRuntime.GetLiveItemsAsync(subscription, token).ConfigureAwait(false);
                    result.AddRange(items.Select(item => new FeedLiveNotificationItem(
                        subscription.ProviderId,
                        subscription.SourceId,
                        item)));
                }
                return (IReadOnlyList<FeedLiveNotificationItem>)result.ToArray();
            },
            cancellationToken);

    public Task AddToViewLaterAsync(
        FeedItemKey key,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async token =>
            {
                await EnsureInitializedAsync(token).ConfigureAwait(false);
                var stored = await _store.GetItemAsync(key, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("找不到通知对应的条目。");
                if (!_pluginRuntime.IsPluginProvider(stored.Item.ProviderId))
                    throw new InvalidOperationException("当前来源不支持稍后再看。");

                var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
                var subscription = subscriptions.FirstOrDefault(item =>
                    string.Equals(item.SubscriptionId, stored.SubscriptionId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("找不到通知对应的来源订阅。");
                await _pluginRuntime.ExecuteViewLaterActionAsync(
                    subscription,
                    stored.Item,
                    isSaved: true,
                    token).ConfigureAwait(false);
                await _feedSession.SetSavedForItemAsync(
                    stored.Item.ProviderId,
                    stored.Item.SourceId,
                    stored.Item.ItemId,
                    isSaved: true,
                    token).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _feedSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var pluginLoad = await _pluginRuntime.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in _pluginRuntime.FeedProviders)
            _providerRegistry.Register(provider);
        var hasCredentials = pluginLoad.IsAvailable
            && await _pluginRuntime.HasCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var source = BuiltInVideoSitePlugin.CreateSource();
        foreach (var subscription in BuiltInVideoSitePlugin.CreateSubscriptions(hasCredentials))
        {
            await _feedSession.AddSubscriptionAsync(source, subscription, cancellationToken)
                .ConfigureAwait(false);
            _pluginRuntime.Register(subscription);
        }
        foreach (var subscription in await _feedSession.GetSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (_pluginRuntime.IsPluginProvider(subscription.ProviderId))
                _pluginRuntime.Register(subscription);
        }
        _initialized = true;
    }

    private async Task<FeedSubscription?> FindSiteSubscriptionAsync(
        string feedId,
        CancellationToken cancellationToken)
    {
        var subscriptions = await _feedSession.GetSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        return subscriptions.FirstOrDefault(item =>
            item.IsEnabled
            && string.Equals(item.ProviderId, BuiltInVideoSitePlugin.PluginId, StringComparison.Ordinal)
            && string.Equals(item.SourceId, BuiltInVideoSitePlugin.SourceId, StringComparison.Ordinal)
            && string.Equals(item.FeedId, feedId, StringComparison.Ordinal));
    }

    private async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
        => RunExclusiveAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _pluginRuntime.DisposeAsync().ConfigureAwait(false);
            _rssProvider.Dispose();
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
