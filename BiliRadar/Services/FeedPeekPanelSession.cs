using BiliRadar.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
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

public sealed class FeedPeekPanelSession : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private const string RssEndpointOption = "rss.endpoint";
    private const string RssIconOption = "rss.icon";
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly SqliteFeedStore _store;
    private readonly RssFeedProvider _rssProvider;
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
        var databasePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "FeedPeek",
            "feedpeek.db");

        _store = new SqliteFeedStore(databasePath);
        _rssProvider = new RssFeedProvider();
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
                results = await _feedSession.RefreshAllAsync(operationToken).ConfigureAwait(false);
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
        => GetItemsAsync(new FeedQuery(), cancellationToken);

    public Task<IReadOnlyList<FeedItemRow>> GetHistoryItemsAsync(
        CancellationToken cancellationToken = default)
        => GetItemsAsync(new FeedQuery(ReadOnly: true), cancellationToken);

    public Task<IReadOnlyList<FeedItemRow>> GetViewLaterItemsAsync(
        CancellationToken cancellationToken = default)
        => GetItemsAsync(new FeedQuery(SavedOnly: true), cancellationToken);

    private async Task<IReadOnlyList<FeedItemRow>> GetItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FeedItemRow> result = [];
        await RunExclusiveAsync(
            async operationToken =>
            {
                await EnsureInitializedAsync(operationToken).ConfigureAwait(false);
                var summaries = await _feedSession.GetSourceSummariesAsync(operationToken)
                    .ConfigureAwait(false);
                var sources = summaries.ToDictionary(
                    item => item.Source.SourceId,
                    item => item.Source,
                    StringComparer.Ordinal);
                var storedItems = await _feedSession.GetItemsAsync(query, operationToken)
                    .ConfigureAwait(false);
                result = storedItems.Select(item =>
                {
                    sources.TryGetValue(item.Item.SourceId, out var source);
                    return new FeedItemRow(
                        item,
                        source?.DisplayName,
                        GetSourceIconUrl(source));
                }).ToList();
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
                await _feedSession.MarkSavedAsync(item.Key, isSaved, operationToken)
                    .ConfigureAwait(false);
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
        _initialized = true;
    }

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

        if (source.Options.TryGetValue(RssIconOption, out var iconUrl)
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
