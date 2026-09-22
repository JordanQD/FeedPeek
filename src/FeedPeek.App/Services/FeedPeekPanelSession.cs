using FeedPeek.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.Rss;
using FeedPeek.Helpers;
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

public sealed class FeedItemSavedChangedEventArgs(FeedItemRow item, bool isSaved) : EventArgs
{
    public FeedItemRow Item { get; } = item;

    public bool IsSaved { get; } = isSaved;
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
    internal const string RssFormatOption = "rss.format";
    private const string PluginRoomIdsOption = "roomIds";
    private const string PluginRoomUrlTemplateOption = "roomUrlTemplate";
    private const string DefaultCredentialProfileId = "default";
    private const string RssIconOption = "rss.icon";
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(15);
    internal const int PanelPageSize = 30;

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
    private readonly HashSet<string> _registeredPluginProviders = new(StringComparer.Ordinal);
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

    /// <summary>Raised on the UI thread when an item's "watch later" state changes.</summary>
    public event EventHandler<FeedItemSavedChangedEventArgs>? ItemSavedChanged;

    /// <summary>
    /// Raised on the UI thread just before a background open hands a link to the shell, so the
    /// host can keep the panel open while the shell target takes focus.
    /// </summary>
    public event EventHandler? BackgroundOpenRequested;

    public IReadOnlyList<CreatorDescriptor> FollowingCreators { get; private set; } = [];

    /// <summary>
    /// Optional source filter for list queries. Null or empty means all sources.
    /// Format: <c>providerId|sourceId</c>. Refresh still covers every enabled subscription.
    /// </summary>
    public string? SourceFilter { get; set; }

    private (string ProviderId, string SourceId)? ParsedSourceFilter
    {
        get
        {
            var raw = SourceFilter;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var separator = raw.IndexOf('|');
            if (separator <= 0 || separator >= raw.Length - 1)
            {
                return null;
            }

            return (raw[..separator], raw[(separator + 1)..]);
        }
    }

    private bool MatchesSourceFilter(FeedSubscription subscription)
    {
        var filter = ParsedSourceFilter;
        return filter is null
            || (string.Equals(subscription.ProviderId, filter.Value.ProviderId, StringComparison.Ordinal)
                && string.Equals(subscription.SourceId, filter.Value.SourceId, StringComparison.Ordinal));
    }

    private FeedQuery ApplySourceFilter(FeedQuery query)
    {
        var filter = ParsedSourceFilter;
        return filter is null
            ? query
            : query with
            {
                ProviderId = filter.Value.ProviderId,
                SourceId = filter.Value.SourceId,
            };
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Adds a source from an address the user typed: when the address is a live room link a plugin
    /// recognises, the plugin owns it; anything else is treated as an RSS/Atom feed. Adding by a bare
    /// room number is deliberately not supported yet.
    /// </summary>
    public async Task<FeedSourceRow> AddSourceFromAddressAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var trimmed = address.Trim();
        var room = await ResolvePluginRoomAddressAsync(trimmed, cancellationToken).ConfigureAwait(false);
        if (room is null)
        {
            return await AddRssSubscriptionAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        return await AddPluginSourceAsync(
            room.PluginId,
            room.DisplayName,
            new Dictionary<string, string>(StringComparer.Ordinal) { [PluginRoomIdsOption] = room.RoomId },
            room.CredentialProfileId,
            selectedFeedIds: null,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record PluginRoomAddress(
        string PluginId,
        string DisplayName,
        string RoomId,
        string? CredentialProfileId);

    private async Task<PluginRoomAddress?> ResolvePluginRoomAddressAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (address.All(char.IsDigit))
        {
            throw new InvalidOperationException(
                "暂时只支持粘贴完整的直播间链接，例如 https://live.douyin.com/123456。");
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + address, UriKind.Absolute, out uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var entries = await GetPluginsAsync(cancellationToken).ConfigureAwait(false);
        var matchedDisabledPlugin = false;
        foreach (var entry in entries)
        {
            if (entry.Manifest?.RuntimeOptions is null
                || !entry.Manifest.RuntimeOptions.TryGetValue(PluginRoomUrlTemplateOption, out var template)
                || string.IsNullOrWhiteSpace(template)
                || !TryMatchRoomTemplate(template, uri, out var roomId))
            {
                continue;
            }

            if (!entry.IsEnabled)
            {
                matchedDisabledPlugin = true;
                continue;
            }

            string? credentialProfileId = null;
            if (entry.Manifest.Auth is not null)
            {
                if (await HasCredentialsSafeAsync(entry.PluginId, cancellationToken).ConfigureAwait(false))
                {
                    credentialProfileId = DefaultCredentialProfileId;
                }
                else if (entry.Manifest.Auth.Required)
                {
                    throw new InvalidOperationException("此网站来源需要先登录，请先在插件页面完成登录。");
                }
            }

            var sources = await GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            if (sources.Any(source => IsSameRoom(source, entry.PluginId, roomId)))
            {
                throw new InvalidOperationException("这个直播间已经添加过了。");
            }

            return new PluginRoomAddress(
                entry.PluginId,
                $"{entry.DisplayName} · {roomId}",
                roomId,
                credentialProfileId);
        }

        if (matchedDisabledPlugin)
        {
            throw new InvalidOperationException("对应的网站插件已被停用：请重新安装该插件以启用它。");
        }

        // A bare room link is not an RSS feed; saying so beats letting feed discovery fail with a
        // confusing "response too large" or "no feed found" error.
        if (LooksLikeRoomLink(uri))
        {
            throw new InvalidOperationException(
                "没有已安装的插件支持这个直播间链接。请先在「安装插件」中安装对应网站的插件。");
        }

        return null;
    }

    /// <summary>
    /// Heuristic for "this address is a live room, not a feed": a single path segment that is a number
    /// (for example <c>https://live.douyin.com/945843973757</c> or <c>https://www.douyu.com/74751</c>).
    /// </summary>
    private static bool LooksLikeRoomLink(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 1
            && segments[0].Length >= 4
            && segments[0].All(char.IsDigit);
    }

    private static bool IsSameRoom(FeedSourceRow source, string pluginId, string roomId)
        => string.Equals(source.ProviderId, pluginId, StringComparison.Ordinal)
            && source.Source.Options is not null
            && source.Source.Options.TryGetValue(PluginRoomIdsOption, out var existing)
            && existing
                .Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Contains(roomId, StringComparer.Ordinal);

    /// <summary>Matches a plugin's room URL template (for example <c>https://live.douyin.com/{roomId}</c>).</summary>
    private static bool TryMatchRoomTemplate(string template, Uri address, out string roomId)
    {
        roomId = string.Empty;
        const string marker = "{roomId}";
        var markerIndex = template.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0
            || !Uri.TryCreate(template[..markerIndex], UriKind.Absolute, out var templateBase)
            || !string.Equals(templateBase.Host, address.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = template[..markerIndex];
        var suffix = template[(markerIndex + marker.Length)..];
        var candidate = address.GetLeftPart(UriPartial.Path);
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = candidate[prefix.Length..];
        if (suffix.Length > 0)
        {
            if (!remainder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            remainder = remainder[..^suffix.Length];
        }

        remainder = remainder.Trim('/');
        if (remainder.Length == 0 || !remainder.All(char.IsDigit))
        {
            return false;
        }

        roomId = remainder;
        return true;
    }

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
                    [RssFormatOption] = string.Equals(discovery.Format, "atom", StringComparison.OrdinalIgnoreCase)
                        ? "atom"
                        : "rss",
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
        => GetItemsAsync(
            new FeedQuery(FeedId: PluginFeedIds.Updates, Limit: PanelPageSize, Offset: 0),
            cancellationToken);

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
                // A plugin source may expose only the live view (no updates feed at all), so the
                // live strip is collected even when there is nothing to ask for a following list.
                var updateSubscriptions = subscriptions.Where(item =>
                    MatchesSourceFilter(item) && IsPluginFeed(item, PluginFeedIds.Updates)).ToArray();
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
                    MatchesSourceFilter(item) && IsPluginFeed(item, PluginFeedIds.Live)))
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

    /// <summary>
    /// Creates the default source for plugins whose manifest asks for nothing else. Plugins that declare
    /// required source fields (the live-room plugins pin room ids that way) are skipped because only the
    /// user can supply them. Creation happens at most once per plugin, and never before the credentials a
    /// plugin requires actually exist.
    /// </summary>
    private async Task CreateAutomaticSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await _feedSession.GetSourceSummariesAsync(cancellationToken).ConfigureAwait(false);
        var withSource = sources
            .Select(summary => summary.Source.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var provider in _pluginRuntime.FeedProviders)
        {
            var pluginId = provider.ProviderId;
            if (withSource.Contains(pluginId) || AppSettings.HasAutoCreatedPluginSource(pluginId))
            {
                continue;
            }

            var manifest = _pluginRuntime.GetManifest(pluginId);
            if (manifest?.SourceConfiguration is null
                || manifest.SourceConfiguration.Fields.Any(field => field.Required))
            {
                continue;
            }

            try
            {
                string? credentialProfileId = null;
                if (manifest.Auth is not null)
                {
                    if (await HasCredentialsSafeAsync(pluginId, cancellationToken).ConfigureAwait(false))
                    {
                        credentialProfileId = DefaultCredentialProfileId;
                    }
                    else if (manifest.Auth.Required)
                    {
                        continue;   // wait until the user has signed in
                    }
                }

                var created = await _pluginRuntime.CreateSourceAsync(
                    pluginId,
                    manifest.DisplayName,
                    options: null,
                    credentialProfileId,
                    manifest.SourceConfiguration.DefaultFeedIds,
                    cancellationToken).ConfigureAwait(false);
                foreach (var subscription in created.Subscriptions)
                {
                    await _feedSession.AddSubscriptionAsync(
                        created.Source,
                        subscription,
                        cancellationToken).ConfigureAwait(false);
                }

                AppSettings.MarkAutoCreatedPluginSource(pluginId);
                withSource.Add(pluginId);
            }
            catch (Exception exception)
            {
                RaiseStatus(
                    $"自动添加 {manifest.DisplayName} 来源失败：{DescribeError(exception)}",
                    InfoBarSeverity.Warning);
            }
        }
    }

    /// <summary>
    /// Credential lookup that cannot take the app down: the Windows credential vault reports a missing
    /// entry as an ArgumentException, and any other read failure means the same thing to us - the user
    /// is not signed in.
    /// </summary>
    private async Task<bool> HasCredentialsSafeAsync(string pluginId, CancellationToken cancellationToken)
    {
        try
        {
            return await _pluginRuntime
                .HasCredentialsAsync(pluginId, DefaultCredentialProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
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
        FeedItemLoadResult result = new([], false);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var items = await QueryItemsAsync(
                    new FeedQuery(FeedId: PluginFeedIds.Updates, Limit: PanelPageSize, Offset: 0),
                    operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                var hasMore = items.Count >= PanelPageSize
                    || await HasMoreFeedItemsCoreAsync(subscriptions, PluginFeedIds.Updates, operationToken)
                        .ConfigureAwait(false);
                result = new FeedItemLoadResult(items, hasMore);
            },
            cancellationToken);
        return result;
    }

    public Task<FeedItemLoadResult> LoadMoreFollowingItemsAsync(
        int offset,
        CancellationToken cancellationToken = default)
        => LoadMoreSectionItemsAsync(
            PluginFeedIds.Updates,
            QueryFollowingItemsCoreAsync,
            offset,
            cancellationToken);

    private async Task<IReadOnlyList<FeedItemRow>> QueryFollowingItemsCoreAsync(
        int offset,
        CancellationToken cancellationToken)
        => await QueryItemsAsync(
            new FeedQuery(FeedId: PluginFeedIds.Updates, Limit: PanelPageSize, Offset: offset),
            cancellationToken).ConfigureAwait(false);

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
        => GetSectionPageAsync(
            PluginFeedIds.History,
            QueryHistoryItemsCoreAsync,
            offset: 0,
            cancellationToken);

    public Task<FeedItemLoadResult> LoadMoreHistoryItemsAsync(
        int offset,
        CancellationToken cancellationToken = default)
        => LoadMoreSectionItemsAsync(
            PluginFeedIds.History,
            QueryHistoryItemsCoreAsync,
            offset,
            cancellationToken);

    public Task<FeedItemLoadResult> GetViewLaterPageAsync(
        CancellationToken cancellationToken = default)
        => GetSectionPageAsync(
            PluginFeedIds.ViewLater,
            QueryViewLaterItemsCoreAsync,
            offset: 0,
            cancellationToken);

    public Task<FeedItemLoadResult> LoadMoreViewLaterItemsAsync(
        int offset,
        CancellationToken cancellationToken = default)
        => LoadMoreSectionItemsAsync(
            PluginFeedIds.ViewLater,
            QueryViewLaterItemsCoreAsync,
            offset,
            cancellationToken);

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
                RaiseItemSavedChanged(item, isSaved);

                if (isSaved)
                {
                    RaiseStatus(
                        LocalizationHelper.GetString("AddedToViewLater"),
                        InfoBarSeverity.Success);
                }
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
                var uri = await MarkReadAndResolveUriAsync(item, operationToken).ConfigureAwait(false);

                if (!await Launcher.LaunchUriAsync(uri))
                {
                    RaiseStatus("无法打开该条目的网页地址。", InfoBarSeverity.Warning);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Opens the item's link in the background. The link is handed to the shell before any work
    /// that needs the operation gate: an initial refresh can hold that gate for seconds, and it
    /// must not delay the browser. The host is told first so it can keep the panel open while the
    /// browser takes focus.
    /// </summary>
    public async Task OpenItemInBackgroundAsync(
        FeedItemRow item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (ResolveWebUri(item) is not Uri uri)
        {
            RaiseStatus("该条目没有有效的网页地址。", InfoBarSeverity.Error);
            return;
        }

        // Raised on the caller's thread before the shell launches, so the host suppresses its light
        // dismiss and reclaims the flyout window while the browser activates.
        RaiseBackgroundOpenRequested();

        if (!ShellUrlLauncher.TryOpenInBackground(uri.AbsoluteUri))
        {
            RaiseStatus("无法在后台打开该条目的网页地址。", InfoBarSeverity.Warning);
        }

        try
        {
            await RunExclusiveAsync(
                async operationToken =>
                {
                    await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                    await _feedSession.MarkReadAsync(item.Key, cancellationToken: operationToken)
                        .ConfigureAwait(false);
                    item.SetRead(true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The link is already open; a failed read-state write must not disturb the user.
        }
    }

    private static Uri? ResolveWebUri(FeedItemRow item)
        => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri
                : null;

    private async Task<Uri> MarkReadAndResolveUriAsync(FeedItemRow item, CancellationToken cancellationToken)
    {
        await _feedSession.MarkReadAsync(item.Key, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        item.SetRead(true);

        return ResolveWebUri(item) ?? throw new InvalidOperationException("该条目没有有效的网页地址。");
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

    public async Task UpdateSourceIconAsync(
        string providerId,
        string sourceId,
        string? iconUrl,
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

                if (string.IsNullOrWhiteSpace(iconUrl))
                {
                    options.Remove("source.icon");
                }
                else
                {
                    options["source.icon"] = iconUrl;
                }

                var updatedSource = summary.Source with { Options = options };

                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                var subscription = subscriptions.FirstOrDefault(item =>
                    string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
                    && string.Equals(item.SourceId, sourceId, StringComparison.Ordinal));
                if (subscription is not null)
                {
                    await _feedSession.AddSubscriptionAsync(
                        updatedSource,
                        subscription,
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
        if (!_initialized)
        {
            await _feedSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
            // Phase 4.3 created this source automatically. It is not a user-created
            // source, and must not spring back to life when the plugin is installed.
            await _feedSession.DeleteSourceAsync(
                LegacyVideoSiteCompatibility.PluginId,
                LegacyVideoSiteCompatibility.SourceId,
                cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }

        // Re-synced on every call: LoadAsync is a no-op until the install state changes, and this is what
        // lets a plugin installed from Settings start working without restarting the app.
        await _pluginRuntime.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in _pluginRuntime.FeedProviders)
            _providerRegistry.Register(provider);
        foreach (var stale in _registeredPluginProviders
            .Where(id => !_pluginRuntime.IsPluginProvider(id))
            .ToArray())
        {
            _providerRegistry.Unregister(stale);
            _registeredPluginProviders.Remove(stale);
        }

        foreach (var provider in _pluginRuntime.FeedProviders)
            _registeredPluginProviders.Add(provider.ProviderId);

        // Plugins that need no user input get their default source created here, so installing one is
        // enough for it to show up in the main window.
        await CreateAutomaticSourcesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var subscription in await _feedSession.GetSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (_pluginRuntime.IsPluginProvider(subscription.ProviderId))
                _pluginRuntime.Register(subscription);
        }
    }

    private async Task<IReadOnlyList<FeedItemRow>> QueryItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken)
    {
        var filtered = ApplySourceFilter(query);
        var summaries = await _feedSession.GetSourceSummariesAsync(cancellationToken)
            .ConfigureAwait(false);
        var sources = summaries.ToDictionary(
            item => (item.Source.ProviderId, item.Source.SourceId),
            item => item.Source);
        var storedItems = await _feedSession.GetItemsAsync(filtered, cancellationToken)
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
        Func<int, CancellationToken, Task<IReadOnlyList<FeedItemRow>>> query,
        int offset,
        CancellationToken cancellationToken)
    {
        FeedItemLoadResult result = new([], false);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                var items = await query(offset, operationToken).ConfigureAwait(false);
                var pluginHasMore = await HasMoreFeedItemsCoreAsync(
                    subscriptions,
                    feedId,
                    operationToken).ConfigureAwait(false);
                // An empty page after offset/merge must never keep hasMore — view-later
                // merge can shrink a full raw window to fewer logical rows.
                var hasMore = items.Count > 0
                    && (items.Count >= PanelPageSize || pluginHasMore);
                result = new FeedItemLoadResult(items, hasMore);
            },
            cancellationToken);
        return result;
    }

    private async Task<FeedItemLoadResult> LoadMoreSectionItemsAsync(
        string feedId,
        Func<int, CancellationToken, Task<IReadOnlyList<FeedItemRow>>> query,
        int offset,
        CancellationToken cancellationToken)
    {
        FeedItemLoadResult result = new([], false);
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var subscriptions = await _feedSession.GetSubscriptionsAsync(operationToken)
                    .ConfigureAwait(false);
                foreach (var subscription in subscriptions.Where(item =>
                    MatchesSourceFilter(item) && IsPluginFeed(item, feedId)))
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

                var items = await query(offset, operationToken).ConfigureAwait(false);
                var hasMore = items.Count >= PanelPageSize
                    || await HasMoreFeedItemsCoreAsync(subscriptions, feedId, operationToken)
                        .ConfigureAwait(false);
                result = new FeedItemLoadResult(items, hasMore);
            },
            cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<FeedItemRow>> QueryHistoryItemsCoreAsync(
        int offset,
        CancellationToken cancellationToken)
    {
        // Merge then page so history from plugins and local reads keep one time order.
        // Bound the fetch window to offset+page (not the old 500-row default).
        var window = offset + PanelPageSize;
        var remote = await QueryItemsAsync(
            new FeedQuery(FeedId: PluginFeedIds.History, Limit: window),
            cancellationToken).ConfigureAwait(false);
        var local = await QueryItemsAsync(
            new FeedQuery(ReadOnly: true, Limit: window),
            cancellationToken).ConfigureAwait(false);
        var merged = MergeLogicalItems(remote, local);
        return offset >= merged.Count
            ? []
            : merged.Skip(offset).Take(PanelPageSize).ToList();
    }

    private async Task<IReadOnlyList<FeedItemRow>> QueryViewLaterItemsCoreAsync(
        int offset,
        CancellationToken cancellationToken)
    {
        // Saved rows exist under several feed_ids. SQL OFFSET counts raw rows while the UI
        // list is logical (de-duped) items, so paging by Count stops mid-list. Merge first.
        var limit = Math.Max(500, offset + PanelPageSize * 2);
        var rows = await QueryItemsAsync(
            new FeedQuery(SavedOnly: true, Limit: limit),
            cancellationToken).ConfigureAwait(false);
        var merged = MergeLogicalItems(rows);
        return offset >= merged.Count
            ? []
            : merged.Skip(offset).Take(PanelPageSize).ToList();
    }

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
        foreach (var subscription in subscriptions.Where(item =>
            MatchesSourceFilter(item) && IsPluginFeed(item, feedId)))
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
            // Some failures have an empty Message; the panel would show a blank error toast.
            RaiseStatus(DescribeError(exception), InfoBarSeverity.Error);
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

    private void RaiseItemSavedChanged(FeedItemRow item, bool isSaved)
    {
        var args = new FeedItemSavedChangedEventArgs(item, isSaved);
        if (_dispatcherQueue.HasThreadAccess)
        {
            ItemSavedChanged?.Invoke(this, args);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ItemSavedChanged?.Invoke(this, args));
    }

    private void RaiseBackgroundOpenRequested()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            BackgroundOpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => BackgroundOpenRequested?.Invoke(this, EventArgs.Empty));
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
