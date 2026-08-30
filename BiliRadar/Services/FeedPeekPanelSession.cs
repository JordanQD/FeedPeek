using BiliRadar.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.Rss;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Launcher = Windows.System.Launcher;

namespace BiliRadar.Services;

public sealed class FeedPeekStatus(string message, InfoBarSeverity severity) : EventArgs
{
    public string Message { get; } = message;

    public InfoBarSeverity Severity { get; } = severity;
}

public sealed record FeedItemLoadResult(
    IReadOnlyList<FeedItemRow> Items,
    bool HasMore);

public sealed class FeedPeekPanelSession : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private const string RssEndpointOption = "rss.endpoint";
    private const string RssIconOption = "rss.icon";
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly SqliteFeedStore _store;
    private readonly CookieStore _cookieStore;
    private readonly RssFeedProvider _rssProvider;
    private readonly FeedPeekPluginRuntime _pluginRuntime;
    private readonly FeedProviderRegistry _providerRegistry;
    private readonly FeedPollingService _pollingService;
    private readonly FeedSession _feedSession;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _initialized;
    private bool _disposed;
    private bool _isBusy;

    public FeedPeekPanelSession()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _cookieStore = new CookieStore();
        var databasePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "FeedPeek",
            "feedpeek.db");

        _store = new SqliteFeedStore(databasePath);
        _rssProvider = new RssFeedProvider();
        _pluginRuntime = new FeedPeekPluginRuntime(_cookieStore);
        _providerRegistry = new FeedProviderRegistry([
            _rssProvider,
            _pluginRuntime.FeedProvider,
        ]);
        _pollingService = new FeedPollingService(_store, _providerRegistry);
        _feedSession = new FeedSession(_store, _pollingService);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            if (_dispatcherQueue.HasThreadAccess)
            {
                OnPropertyChanged();
            }
            else
            {
                _dispatcherQueue.TryEnqueue(() => OnPropertyChanged(nameof(IsBusy)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<FeedPeekStatus>? StatusRaised;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public async Task<FeedSourceRow> AddRssSubscriptionAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        FeedSourceRow? result = null;
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var discovery = await _rssProvider.DiscoverAsync(endpoint, operationToken).ConfigureAwait(false);
                var displayName = string.IsNullOrWhiteSpace(discovery.Title)
                    ? discovery.FeedUri.Host
                    : discovery.Title.Trim();
                var options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RssEndpointOption] = discovery.FeedUri.AbsoluteUri,
                };
                if (!string.IsNullOrWhiteSpace(discovery.IconUrl))
                {
                    options[RssIconOption] = discovery.IconUrl;
                }
                var source = new SourceDefinition(
                    RssProviderInfo.ProviderId,
                    discovery.SourceId,
                    displayName,
                    null,
                    [new FeedDefinition(
                        RssProviderInfo.UpdatesFeedId,
                        displayName,
                        "rss",
                        SupportsPaging: false,
                        SupportsNotifications: true)],
                    options);
                var subscription = new FeedSubscription(
                    BuildSubscriptionId(discovery.SourceId),
                    RssProviderInfo.ProviderId,
                    discovery.SourceId,
                    RssProviderInfo.UpdatesFeedId,
                    displayName,
                    discovery.FeedUri.AbsoluteUri,
                    null,
                    true,
                    DefaultRefreshInterval,
                    options);

                await _feedSession.AddSubscriptionAsync(source, subscription, operationToken)
                    .ConfigureAwait(false);

                // The first successful poll stores the current items but deliberately emits no notifications.
                await _feedSession.RefreshAsync(subscription, operationToken).ConfigureAwait(false);

                var summaries = await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false);
                var summary = summaries.FirstOrDefault(item =>
                    string.Equals(item.Source.ProviderId, source.ProviderId, StringComparison.Ordinal)
                    && string.Equals(item.Source.SourceId, source.SourceId, StringComparison.Ordinal));
                result = new FeedSourceRow(summary ?? new FeedSourceSummary(source, 0, 0));
            },
            cancellationToken);

        return result!;
    }

    public async Task<IReadOnlyList<FeedPollResult>> RefreshAllAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FeedPollResult> results = [];
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var successful = new List<FeedPollResult>();
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                foreach (var subscription in subscriptions.Where(item => item.IsEnabled))
                {
                    operationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var poll = await _feedSession.RefreshAsync(subscription, operationToken)
                            .ConfigureAwait(false);
                        if (IsPluginFeed(subscription, PluginFeedIds.ViewLater))
                        {
                            await SynchronizeViewLaterPageAsync(
                                subscription,
                                poll.FetchedItems,
                                resetMembership: true,
                                operationToken).ConfigureAwait(false);
                        }

                        successful.Add(poll);
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        RaiseStatus(
                            $"{subscription.DisplayName}：{exception.Message}",
                            InfoBarSeverity.Error);
                    }
                }

                results = successful;
            },
            cancellationToken);
        return results;
    }

    public async Task<IReadOnlyList<FeedItemRow>> GetItemsAsync(
        bool unreadOnly,
        string? sourceId,
        CancellationToken cancellationToken = default)
        => await GetItemsAsync(
            new FeedQuery(UnreadOnly: unreadOnly, SourceId: sourceId),
            cancellationToken);

    public Task<IReadOnlyList<FeedItemRow>> GetFollowingItemsAsync(
        CancellationToken cancellationToken = default)
        => GetItemsAsync(new FeedQuery(FeedId: PluginFeedIds.Updates), cancellationToken);

    public async Task<FeedItemLoadResult> GetFollowingPageAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await GetFollowingItemsAsync(cancellationToken);
        var hasMore = await HasMoreFollowingItemsAsync(cancellationToken);
        return new FeedItemLoadResult(items, hasMore);
    }

    public async Task<FeedItemLoadResult> LoadMoreFollowingItemsAsync(
        CancellationToken cancellationToken = default)
    {
        FeedItemLoadResult result = new([], false);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                foreach (var subscription in subscriptions.Where(item =>
                    IsPluginFeed(item, PluginFeedIds.Updates)))
                {
                    if (await _feedSession.HasMoreAsync(subscription, operationToken).ConfigureAwait(false))
                    {
                        await _feedSession.LoadMoreAsync(subscription, operationToken).ConfigureAwait(false);
                    }
                }

                var items = await QueryItemsAsync(
                    new FeedQuery(FeedId: PluginFeedIds.Updates),
                    operationToken).ConfigureAwait(false);
                var hasMore = await HasMoreFeedItemsCoreAsync(
                    subscriptions,
                    PluginFeedIds.Updates,
                    operationToken)
                    .ConfigureAwait(false);
                result = new FeedItemLoadResult(items, hasMore);
            },
            cancellationToken);
        return result;
    }

    public async Task<bool> HasMoreFollowingItemsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = false;
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                result = await HasMoreFeedItemsCoreAsync(
                    subscriptions,
                    PluginFeedIds.Updates,
                    operationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
        return result;
    }

    public Task<FeedItemLoadResult> GetHistoryPageAsync(
        CancellationToken cancellationToken = default)
        => GetSectionPageAsync(PluginFeedIds.History, QueryHistoryItemsCoreAsync, cancellationToken);

    public Task<FeedItemLoadResult> LoadMoreHistoryItemsAsync(
        CancellationToken cancellationToken = default)
        => LoadMoreSectionItemsAsync(PluginFeedIds.History, QueryHistoryItemsCoreAsync, cancellationToken);

    public Task<FeedItemLoadResult> GetViewLaterPageAsync(
        CancellationToken cancellationToken = default)
        => GetSectionPageAsync(PluginFeedIds.ViewLater, QueryViewLaterItemsCoreAsync, cancellationToken);

    public Task<FeedItemLoadResult> LoadMoreViewLaterItemsAsync(
        CancellationToken cancellationToken = default)
        => LoadMoreSectionItemsAsync(PluginFeedIds.ViewLater, QueryViewLaterItemsCoreAsync, cancellationToken);

    private async Task<IReadOnlyList<FeedItemRow>> GetItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FeedItemRow> result = [];
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                result = await QueryItemsAsync(query, operationToken).ConfigureAwait(false);
            },
            cancellationToken);
        return result;
    }

    public Task SetSavedAsync(
        FeedItemRow item,
        bool isSaved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                if (string.Equals(
                    item.ProviderId,
                    BuiltInVideoSitePlugin.PluginId,
                    StringComparison.Ordinal))
                {
                    var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                        .ConfigureAwait(false);
                    var subscription = subscriptions.FirstOrDefault(candidate =>
                        string.Equals(candidate.SubscriptionId, item.SubscriptionId, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("找不到当前条目对应的站点订阅。");
                    await _pluginRuntime.ExecuteViewLaterActionAsync(
                        subscription,
                        item.Item,
                        isSaved,
                        operationToken).ConfigureAwait(false);
                    await _feedSession.SetSavedForItemAsync(
                        item.ProviderId,
                        item.SourceId,
                        item.ItemId,
                        isSaved,
                        operationToken).ConfigureAwait(false);
                }
                else
                {
                    await _feedSession.MarkSavedAsync(item.Key, isSaved, operationToken)
                        .ConfigureAwait(false);
                }

                item.SetSaved(isSaved);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<FeedSourceRow>> GetSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FeedSourceRow> result = [];
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var summaries = await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false);
                result = summaries.Select(item => new FeedSourceRow(item)).ToList();
            },
            cancellationToken);
        return result;
    }

    public Task MarkReadAndLaunchAsync(
        FeedItemRow item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                await _feedSession.MarkReadAsync(item.Key, cancellationToken: operationToken)
                    .ConfigureAwait(false);
                item.SetRead(true);

                if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException("该条目没有有效的网页地址。");
                }

                if (!await Launcher.LaunchUriAsync(uri))
                {
                    RaiseStatus("无法打开该条目的网页地址。", InfoBarSeverity.Warning);
                }
            },
            cancellationToken);
    }

    public Task DeleteSourceAsync(
        FeedSourceRow source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                await _feedSession.DeleteSourceAsync(
                    source.ProviderId,
                    source.SourceId,
                    operationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();

        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _providerRegistry.Dispose();
                await _pluginRuntime.DisposeAsync().ConfigureAwait(false);
                await _store.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _lifetimeCancellation.Dispose();
            _operationGate.Dispose();
        }
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _feedSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var pluginLoad = await _pluginRuntime.LoadAsync(cancellationToken).ConfigureAwait(false);
        var source = BuiltInVideoSitePlugin.CreateSource();
        var siteSubscriptions = BuiltInVideoSitePlugin.CreateSubscriptions(
            pluginLoad.IsAvailable && _cookieStore.HasCookie);
        foreach (var subscription in siteSubscriptions)
        {
            await _feedSession.AddSubscriptionAsync(source, subscription, cancellationToken)
                .ConfigureAwait(false);
            _pluginRuntime.Register(subscription);
        }
        _initialized = true;

        if (!pluginLoad.IsAvailable)
        {
            RaiseStatus(
                $"内置视频站点插件不可用：{pluginLoad.ErrorMessage}",
                InfoBarSeverity.Warning);
        }
    }

    private async Task<IReadOnlyList<FeedItemRow>> QueryItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken)
    {
        var summaries = await _feedSession.GetSourceSummariesAsync(cancellationToken)
            .ConfigureAwait(false);
        var sources = summaries.ToDictionary(
            item => (item.Source.ProviderId, item.Source.SourceId),
            item => item.Source);
        var storedItems = await _feedSession.GetItemsAsync(query, cancellationToken)
            .ConfigureAwait(false);
        return storedItems.Select(item =>
        {
            sources.TryGetValue((item.Item.ProviderId, item.Item.SourceId), out var source);
            return new FeedItemRow(
                item,
                source?.DisplayName,
                GetSourceIconUrl(source));
        }).ToList();
    }

    private async Task<FeedItemLoadResult> GetSectionPageAsync(
        string feedId,
        Func<CancellationToken, Task<IReadOnlyList<FeedItemRow>>> query,
        CancellationToken cancellationToken)
    {
        FeedItemLoadResult result = new([], false);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                result = new FeedItemLoadResult(
                    await query(operationToken).ConfigureAwait(false),
                    await HasMoreFeedItemsCoreAsync(subscriptions, feedId, operationToken)
                        .ConfigureAwait(false));
            },
            cancellationToken);
        return result;
    }

    private async Task<FeedItemLoadResult> LoadMoreSectionItemsAsync(
        string feedId,
        Func<CancellationToken, Task<IReadOnlyList<FeedItemRow>>> query,
        CancellationToken cancellationToken)
    {
        FeedItemLoadResult result = new([], false);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                foreach (var subscription in subscriptions.Where(item => IsPluginFeed(item, feedId)))
                {
                    if (!await _feedSession.HasMoreAsync(subscription, operationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var loaded = await _feedSession.LoadMoreAsync(subscription, operationToken)
                        .ConfigureAwait(false);
                    if (string.Equals(feedId, PluginFeedIds.ViewLater, StringComparison.Ordinal))
                    {
                        await SynchronizeViewLaterPageAsync(
                            subscription,
                            loaded.Items,
                            resetMembership: false,
                            operationToken).ConfigureAwait(false);
                    }
                }

                result = new FeedItemLoadResult(
                    await query(operationToken).ConfigureAwait(false),
                    await HasMoreFeedItemsCoreAsync(subscriptions, feedId, operationToken)
                        .ConfigureAwait(false));
            },
            cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<FeedItemRow>> QueryHistoryItemsCoreAsync(
        CancellationToken cancellationToken)
    {
        var remote = await QueryItemsAsync(
            new FeedQuery(
                ProviderId: BuiltInVideoSitePlugin.PluginId,
                SourceId: BuiltInVideoSitePlugin.SourceId,
                FeedId: PluginFeedIds.History),
            cancellationToken).ConfigureAwait(false);
        var local = await QueryItemsAsync(
            new FeedQuery(ReadOnly: true),
            cancellationToken).ConfigureAwait(false);
        return MergeLogicalItems(
            remote,
            local.Where(item => !string.Equals(
                item.ProviderId,
                BuiltInVideoSitePlugin.PluginId,
                StringComparison.Ordinal)).ToList());
    }

    private async Task<IReadOnlyList<FeedItemRow>> QueryViewLaterItemsCoreAsync(
        CancellationToken cancellationToken)
        => MergeLogicalItems(
            await QueryItemsAsync(new FeedQuery(SavedOnly: true), cancellationToken)
                .ConfigureAwait(false));

    private async Task SynchronizeViewLaterPageAsync(
        FeedSubscription subscription,
        IReadOnlyList<FeedItem> items,
        bool resetMembership,
        CancellationToken cancellationToken)
    {
        if (resetMembership)
        {
            foreach (var feedId in new[]
            {
                PluginFeedIds.Updates,
                PluginFeedIds.History,
                PluginFeedIds.ViewLater,
            })
            {
                await _feedSession.SetSavedForFeedAsync(
                    subscription.ProviderId,
                    subscription.SourceId,
                    feedId,
                    false,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var item in items)
        {
            await _feedSession.SetSavedForItemAsync(
                item.ProviderId,
                item.SourceId,
                item.ItemId,
                true,
                cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<FeedItemRow> MergeLogicalItems(
        params IReadOnlyList<FeedItemRow>[] groups)
        => groups
            .SelectMany(group => group)
            .GroupBy(
                item => (item.ProviderId, item.SourceId, item.ItemId),
                item => item)
            .Select(group => group.First())
            .OrderByDescending(item => item.PublishedAt)
            .ToList();

    private async Task<bool> HasMoreFeedItemsCoreAsync(
        IReadOnlyList<FeedSubscription> subscriptions,
        string feedId,
        CancellationToken cancellationToken)
    {
        foreach (var subscription in subscriptions.Where(item => IsPluginFeed(item, feedId)))
        {
            if (await _feedSession.HasMoreAsync(subscription, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPluginFeed(FeedSubscription subscription, string feedId)
        => subscription.IsEnabled
            && string.Equals(
                subscription.ProviderId,
                BuiltInVideoSitePlugin.PluginId,
                StringComparison.Ordinal)
            && string.Equals(subscription.FeedId, feedId, StringComparison.Ordinal);

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            cancellationToken);
        await _operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        IsBusy = true;
        try
        {
            await operation(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RaiseStatus(exception.Message, InfoBarSeverity.Error);
            throw;
        }
        finally
        {
            IsBusy = false;
            _operationGate.Release();
        }
    }

    private static string BuildSubscriptionId(string sourceId)
        => $"{RssProviderInfo.ProviderId}:{sourceId}:{RssProviderInfo.UpdatesFeedId}";

    private static string? GetSourceIconUrl(SourceDefinition? source)
    {
        if (source?.Options is null)
        {
            return null;
        }

        if ((source.Options.TryGetValue(BuiltInVideoSitePlugin.SourceIconOption, out var iconUrl)
                || source.Options.TryGetValue(RssIconOption, out iconUrl))
            && Uri.TryCreate(iconUrl, UriKind.Absolute, out _))
        {
            return iconUrl;
        }

        if (!source.Options.TryGetValue(RssEndpointOption, out var endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return new UriBuilder(endpointUri.Scheme, endpointUri.Host, endpointUri.IsDefaultPort ? -1 : endpointUri.Port)
        {
            Path = "/favicon.ico",
        }.Uri.AbsoluteUri;
    }

    private void RaiseStatus(string message, InfoBarSeverity severity)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            StatusRaised?.Invoke(this, new FeedPeekStatus(message, severity));
            return;
        }

        _dispatcherQueue.TryEnqueue(
            () => StatusRaised?.Invoke(this, new FeedPeekStatus(message, severity)));
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
