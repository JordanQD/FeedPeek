using FeedPeek.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginContracts.Dtos;
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

namespace FeedPeek.Services;

public sealed class FeedPeekStatus(string message, InfoBarSeverity severity) : EventArgs
{
    public string Message { get; } = message;

    public InfoBarSeverity Severity { get; } = severity;
}

public sealed record FeedItemLoadResult(
    IReadOnlyList<FeedItemRow> Items,
    bool HasMore);

public sealed record FeedFollowingExtras(
    IReadOnlyList<CreatorDescriptor> FollowingCreators,
    IReadOnlyList<PluginLiveItem> LiveItems);

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
        _providerRegistry = new FeedProviderRegistry([_rssProvider]);
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

    public IReadOnlyList<CreatorDescriptor> FollowingCreators { get; private set; } = [];

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
                foreach (var subscription in subscriptions.Where(item =>
                    item.IsEnabled
                    && IsAvailableProvider(item.ProviderId)
                    && !string.Equals(item.FeedId, PluginFeedIds.Live, StringComparison.Ordinal)))
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

    public async Task<FeedFollowingExtras> GetFollowingExtrasAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new FeedFollowingExtras([], []);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                var updateSubscriptions = subscriptions.Where(item =>
                    IsPluginFeed(item, PluginFeedIds.Updates)).ToArray();
                if (updateSubscriptions.Length == 0)
                {
                    FollowingCreators = [];
                    return;
                }

                var following = new List<CreatorDescriptor>();
                foreach (var subscription in updateSubscriptions)
                {
                    try
                    {
                        following.AddRange(await _pluginRuntime.GetFollowingCreatorsAsync(
                            subscription,
                            operationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        RaiseStatus($"关注列表：{DescribeError(exception)}", InfoBarSeverity.Error);
                    }
                }

                var liveItems = new List<PluginLiveItem>();
                foreach (var liveSubscription in subscriptions.Where(item =>
                    IsPluginFeed(item, PluginFeedIds.Live)))
                {
                    try
                    {
                        liveItems.AddRange(await _pluginRuntime.GetLiveItemsAsync(
                            liveSubscription,
                            operationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        RaiseStatus($"直播列表：{DescribeError(exception)}", InfoBarSeverity.Error);
                    }
                }

                FollowingCreators = following
                    .GroupBy(item => (item.ProviderId, item.SourceId, item.CreatorKey))
                    .Select(group => group.First()).ToArray();
                result = new FeedFollowingExtras(FollowingCreators, liveItems);
            },
            cancellationToken);
        return result;
    }

    private static string DescribeError(Exception exception)
    {
        if (!string.IsNullOrWhiteSpace(exception.Message))
            return exception.Message;
        if (exception is FeedPeek.PluginHost.PluginInvocationException pluginException
            && !string.IsNullOrWhiteSpace(pluginException.Code))
        {
            return pluginException.Code;
        }
        return exception.GetType().Name;
    }

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
                if (_pluginRuntime.IsPluginProvider(item.ProviderId))
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

    public async Task<ActionDescriptor?> GetCreatorRelationActionAsync(
        FeedItemRow item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ActionDescriptor? result = null;
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                if (!_pluginRuntime.IsPluginProvider(item.ProviderId)
                    || string.IsNullOrWhiteSpace(item.Item.CreatorKey))
                {
                    return;
                }

                var subscription = await FindItemSubscriptionAsync(item, operationToken)
                    .ConfigureAwait(false);
                var actions = await _pluginRuntime.GetCreatorActionsAsync(
                    subscription,
                    item.Item,
                    operationToken).ConfigureAwait(false);
                result = actions.FirstOrDefault(action =>
                    action.ActionId is "creator.follow" or "creator.unfollow");
            },
            cancellationToken);
        return result;
    }

    public Task ExecuteCreatorActionAsync(
        FeedItemRow item,
        ActionDescriptor action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(action);
        return RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscription = await FindItemSubscriptionAsync(item, operationToken)
                    .ConfigureAwait(false);
                await _pluginRuntime.ExecuteCreatorActionAsync(
                    subscription,
                    item.Item,
                    action,
                    operationToken).ConfigureAwait(false);
                RaiseStatus(
                    action.ActionId == "creator.follow"
                        ? $"已关注 {item.AuthorName}"
                        : $"已取消关注 {item.AuthorName}",
                    InfoBarSeverity.Success);
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

    internal async Task<IReadOnlyList<PluginManagementEntry>> GetPluginsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PluginManagementEntry> result = [];
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                result = await _pluginRuntime.GetPluginEntriesAsync(operationToken).ConfigureAwait(false);
            },
            cancellationToken);
        return result;
    }

    internal async Task<FeedSourceRow> AddPluginSourceAsync(
        string pluginId,
        string displayName,
        IReadOnlyDictionary<string, string>? options,
        string? credentialProfileId,
        IReadOnlyCollection<string>? selectedFeedIds = null,
        CancellationToken cancellationToken = default)
    {
        FeedSourceRow? result = null;
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var created = await _pluginRuntime.CreateSourceAsync(
                    pluginId,
                    displayName,
                    options,
                    credentialProfileId,
                    selectedFeedIds,
                    operationToken).ConfigureAwait(false);
                foreach (var subscription in created.Subscriptions)
                {
                    await _feedSession.AddSubscriptionAsync(
                        created.Source,
                        subscription,
                        operationToken).ConfigureAwait(false);
                }
                var summary = (await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false)).First(item =>
                        string.Equals(item.Source.ProviderId, created.Source.ProviderId, StringComparison.Ordinal)
                        && string.Equals(item.Source.SourceId, created.Source.SourceId, StringComparison.Ordinal));
                result = new FeedSourceRow(summary);
            },
            cancellationToken);
        return result!;
    }

    internal Task ClearPluginCredentialsAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                await _pluginRuntime.ClearCredentialsAsync(
                    pluginId,
                    credentialProfileId,
                    operationToken).ConfigureAwait(false);
                var sources = await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false);
                foreach (var source in sources.Where(item =>
                    string.Equals(item.Source.ProviderId, pluginId, StringComparison.Ordinal)
                    && string.Equals(item.Source.CredentialProfileId, credentialProfileId, StringComparison.Ordinal)))
                {
                    await _feedSession.DeleteSourceAsync(
                        source.Source.ProviderId,
                        source.Source.SourceId,
                        operationToken).ConfigureAwait(false);
                }
            },
            cancellationToken);

    internal Task DeletePluginSourcesAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var sources = await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false);
                foreach (var source in sources.Where(item =>
                    string.Equals(item.Source.ProviderId, pluginId, StringComparison.Ordinal)))
                {
                    await _feedSession.DeleteSourceAsync(
                        source.Source.ProviderId,
                        source.Source.SourceId,
                        operationToken).ConfigureAwait(false);
                }
            },
            cancellationToken);

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
                if (_pluginRuntime.IsPluginProvider(source.ProviderId))
                {
                    var subscription = (await _feedSession.GetSubscriptionsAsync(operationToken)
                        .ConfigureAwait(false)).FirstOrDefault(item =>
                            string.Equals(item.ProviderId, source.ProviderId, StringComparison.Ordinal)
                            && string.Equals(item.SourceId, source.SourceId, StringComparison.Ordinal));
                    if (subscription is not null)
                        await _pluginRuntime.DeleteSourceAsync(subscription, operationToken).ConfigureAwait(false);
                }
                await _feedSession.DeleteSourceAsync(
                    source.ProviderId,
                    source.SourceId,
                    operationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task UpdateRssEndpointAsync(
        string providerId,
        string sourceId,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);

                var summaries = await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false);
                var summary = summaries.FirstOrDefault(item =>
                        string.Equals(item.Source.ProviderId, providerId, StringComparison.Ordinal)
                        && string.Equals(item.Source.SourceId, sourceId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("来源不存在。");

                var options = new Dictionary<string, string>(StringComparer.Ordinal);
                if (summary.Source.Options is not null)
                {
                    foreach (var pair in summary.Source.Options)
                    {
                        options[pair.Key] = pair.Value;
                    }
                }

                options[RssEndpointOption] = endpoint;
                var updatedSource = summary.Source with { Options = options };

                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                var sourceSubscriptions = subscriptions
                    .Where(item =>
                        string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
                        && string.Equals(item.SourceId, sourceId, StringComparison.Ordinal))
                    .ToList();

                foreach (var subscription in sourceSubscriptions)
                {
                    var updatedSubscription = subscription with { Endpoint = endpoint };
                    await _feedSession.AddSubscriptionAsync(
                        updatedSource,
                        updatedSubscription,
                        operationToken).ConfigureAwait(false);
                }
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
        // Phase 4.3 created this source automatically. It is not a user-created
        // source, and must not spring back to life when the plugin is installed.
        await _feedSession.DeleteSourceAsync(
            LegacyVideoSiteCompatibility.PluginId,
            LegacyVideoSiteCompatibility.SourceId,
            cancellationToken).ConfigureAwait(false);
        await _pluginRuntime.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in _pluginRuntime.FeedProviders)
            _providerRegistry.Register(provider);
        foreach (var subscription in await _feedSession.GetSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (_pluginRuntime.IsPluginProvider(subscription.ProviderId))
                _pluginRuntime.Register(subscription);
        }
        _initialized = true;
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
            var showCreator = _pluginRuntime.IsPluginProvider(item.Item.ProviderId)
                && !string.IsNullOrWhiteSpace(item.Item.CreatorKey);
            return new FeedItemRow(
                item,
                source?.DisplayName,
                GetSourceIconUrl(source),
                preferAuthorAttribution: showCreator);
        }).ToList();
    }

    private async Task<FeedSubscription> FindItemSubscriptionAsync(
        FeedItemRow item,
        CancellationToken cancellationToken)
    {
        var subscriptions = await _feedSession.GetSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return subscriptions.FirstOrDefault(candidate =>
            string.Equals(candidate.SubscriptionId, item.SubscriptionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("找不到当前条目对应的站点订阅。");
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
            new FeedQuery(FeedId: PluginFeedIds.History),
            cancellationToken).ConfigureAwait(false);
        var local = await QueryItemsAsync(
            new FeedQuery(ReadOnly: true),
            cancellationToken).ConfigureAwait(false);
        return MergeLogicalItems(
            remote,
            local);
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

    private bool IsPluginFeed(FeedSubscription subscription, string feedId)
        => subscription.IsEnabled
            && _pluginRuntime.IsPluginProvider(subscription.ProviderId)
            && string.Equals(subscription.FeedId, feedId, StringComparison.Ordinal);

    private bool IsAvailableProvider(string providerId)
        => string.Equals(providerId, RssProviderInfo.ProviderId, StringComparison.Ordinal)
            || _pluginRuntime.IsPluginProvider(providerId);

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

        if ((source.Options.TryGetValue("source.icon", out var iconUrl)
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
