using FeedPeek.Core.Feeds;
using FeedPeek.PluginHost;
using FeedPeek.PluginRuntime.Discovery;
using FeedPeek.PluginRuntime.Host;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Services;

internal sealed record PluginLoadResult(bool IsAvailable, string? ErrorMessage);

internal sealed class FeedPeekPluginRuntime : IDisposable, IAsyncDisposable
{
    private readonly PluginInvocationRegistry _invocations = new();
    private readonly PluginHttpBridge _httpBridge;
    private readonly PluginRuntimeCatalog _catalog;
    private readonly PluginItemActionService _itemActions;
    private bool _loaded;
    private bool _disposed;

    public FeedPeekPluginRuntime(CookieStore cookieStore)
    {
        _httpBridge = new PluginHttpBridge(new LegacyPluginCookieProvider(cookieStore));
        _catalog = new PluginRuntimeCatalog(new PluginHostServices(
            _httpBridge,
            new LegacyPluginCredentialStore(cookieStore),
            NullPluginLogSink.Instance,
            _invocations));
        var client = new PluginClient(new PluginRuntimeCatalogInvoker(_catalog));
        FeedProvider = new PluginFeedProvider(
            BuiltInVideoSitePlugin.PluginId,
            client);
        _itemActions = new PluginItemActionService(BuiltInVideoSitePlugin.PluginId, client);
    }

    public IFeedProvider FeedProvider { get; }

    public async Task<PluginLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loaded)
        {
            return new PluginLoadResult(
                _catalog.LoadedPluginIds.Contains(BuiltInVideoSitePlugin.PluginId, StringComparer.Ordinal),
                null);
        }

        var pluginsRoot = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var results = await _catalog.LoadAsync(
            pluginsRoot,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _loaded = true;

        var sitePlugin = results.FirstOrDefault(result =>
            string.Equals(
                result.Package?.Manifest.PluginId,
                BuiltInVideoSitePlugin.PluginId,
                StringComparison.Ordinal)
            || string.Equals(
                Path.GetFileName(result.DirectoryPath),
                BuiltInVideoSitePlugin.PluginId,
                StringComparison.Ordinal));
        if (sitePlugin?.IsAvailable == true)
        {
            return new PluginLoadResult(true, null);
        }

        return new PluginLoadResult(
            false,
            sitePlugin?.ErrorMessage ?? "没有找到内置视频站点插件。");
    }

    public void Register(FeedPeek.Core.Models.FeedSubscription subscription)
        => _invocations.Register(BuiltInVideoSitePlugin.CreateInvocationContext(subscription));

    public async Task ExecuteViewLaterActionAsync(
        FeedPeek.Core.Models.FeedSubscription subscription,
        FeedPeek.Core.Models.FeedItem item,
        bool isSaved,
        CancellationToken cancellationToken)
    {
        var actionId = isSaved ? "viewLater.add" : "viewLater.remove";
        var actions = await _itemActions.GetActionsAsync(subscription, item, cancellationToken)
            .ConfigureAwait(false);
        var action = actions.FirstOrDefault(candidate =>
            string.Equals(candidate.ActionId, actionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("站点插件没有为当前条目提供稍后再看操作。");
        await _itemActions.ExecuteAsync(subscription, action, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _catalog.DisposeAsync().ConfigureAwait(false);
        _httpBridge.Dispose();
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
