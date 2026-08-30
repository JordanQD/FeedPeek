using FeedPeek.Core.Feeds;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginHost;
using FeedPeek.PluginRuntime.Discovery;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Services;

internal sealed record PluginLoadResult(bool IsAvailable, string? ErrorMessage);

internal sealed record PluginLoginDescriptor(
    string PluginId,
    string CredentialProfileId,
    string DisplayName,
    PluginAuthManifest Auth);

internal sealed class FeedPeekPluginRuntime : IDisposable, IAsyncDisposable
{
    private readonly PluginInvocationRegistry _invocations = new();
    private readonly CookieStore _legacyCookieStore;
    private readonly PluginCredentialProfileStore _credentials = new();
    private readonly PluginHttpBridge _httpBridge;
    private readonly PluginRuntimeCatalog _catalog;
    private readonly PluginAccountService _account;
    private readonly PluginAuthService _auth;
    private readonly PluginItemActionService _itemActions;
    private PluginPackage? _sitePackage;
    private bool _loaded;
    private bool _disposed;

    public FeedPeekPluginRuntime(CookieStore cookieStore)
    {
        _legacyCookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _httpBridge = new PluginHttpBridge(_credentials);
        _catalog = new PluginRuntimeCatalog(new PluginHostServices(
            _httpBridge,
            _credentials,
            NullPluginLogSink.Instance,
            _invocations));
        var client = new PluginClient(new PluginRuntimeCatalogInvoker(_catalog));
        FeedProvider = new PluginFeedProvider(
            BuiltInVideoSitePlugin.PluginId,
            client);
        _account = new PluginAccountService(BuiltInVideoSitePlugin.PluginId, client);
        _auth = new PluginAuthService(BuiltInVideoSitePlugin.PluginId, client);
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
            _sitePackage = sitePlugin.Package;
            await MigrateLegacyCredentialAsync(cancellationToken).ConfigureAwait(false);
            return new PluginLoadResult(true, null);
        }

        return new PluginLoadResult(
            false,
            sitePlugin?.ErrorMessage ?? "没有找到内置视频站点插件。");
    }

    public void Register(FeedPeek.Core.Models.FeedSubscription subscription)
        => _invocations.Register(BuiltInVideoSitePlugin.CreateInvocationContext(subscription));

    public PluginLoginDescriptor GetLoginDescriptor()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var package = _sitePackage
            ?? throw new InvalidOperationException("内置站点插件尚未加载。");
        var auth = package.Manifest.Auth;
        if (auth?.Required != true
            || !string.Equals(auth.Kind, "webview-cookie", StringComparison.Ordinal)
            || auth.LoginFlow is null)
        {
            throw new InvalidOperationException("站点插件没有声明可用的 WebView 登录流程。");
        }

        return new PluginLoginDescriptor(
            package.Manifest.PluginId,
            BuiltInVideoSitePlugin.DefaultCredentialProfileId,
            package.Manifest.DisplayName,
            auth);
    }

    public async Task<bool> HasCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (!_loaded)
        {
            var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!load.IsAvailable)
            {
                return false;
            }
        }

        return await _credentials.ExistsAsync(
            BuiltInVideoSitePlugin.PluginId,
            BuiltInVideoSitePlugin.DefaultCredentialProfileId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveWebCookiesAsync(
        PluginLoginDescriptor descriptor,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(cookies);
        if (!string.Equals(descriptor.PluginId, BuiltInVideoSitePlugin.PluginId, StringComparison.Ordinal)
            || descriptor.Auth.LoginFlow is null
            || descriptor.Auth.LoginFlow.SuccessCookieNames.Any(name => !cookies.ContainsKey(name)))
        {
            throw new InvalidOperationException("网页登录 Cookie 不完整，请重新登录。");
        }

        await _credentials.SaveCookiesAsync(
            descriptor.PluginId,
            descriptor.CredentialProfileId,
            cookies,
            descriptor.Auth.ExposedCookieNames?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
        var cookieHeader = await _credentials.ExportCookieHeaderAsync(
            descriptor.PluginId,
            descriptor.CredentialProfileId,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            _legacyCookieStore.SaveCookieString(cookieHeader);
        }

        var subscription = GetAccountSubscription();
        Register(subscription);
        try
        {
            var completed = await _auth.CompleteAsync(subscription, cancellationToken)
                .ConfigureAwait(false);
            var validated = await _auth.ValidateAsync(subscription, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(completed.State, "authenticated", StringComparison.Ordinal)
                || !string.Equals(validated.State, "authenticated", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(validated.Message ?? "插件未能验证登录状态。");
            }
        }
        catch
        {
            await DeleteCredentialCopiesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task BeginAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!load.IsAvailable)
        {
            throw new InvalidOperationException(load.ErrorMessage);
        }

        var subscription = GetAccountSubscription();
        Register(subscription);
        var result = await _auth.BeginAsync(subscription, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.State, "webviewRequired", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("插件没有启动 WebView 登录流程。");
        }
    }

    public async Task ImportCookieHeaderAsync(
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!load.IsAvailable)
        {
            throw new InvalidOperationException(load.ErrorMessage);
        }
        var descriptor = GetLoginDescriptor();
        await _credentials.ImportCookieHeaderAsync(
            BuiltInVideoSitePlugin.PluginId,
            BuiltInVideoSitePlugin.DefaultCredentialProfileId,
            cookieHeader,
            descriptor.Auth.ExposedCookieNames?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
        _legacyCookieStore.SaveCookieString(cookieHeader);
    }

    public async Task<string?> ExportCookieHeaderAsync(CancellationToken cancellationToken = default)
    {
        await HasCredentialsAsync(cancellationToken).ConfigureAwait(false);
        return await _credentials.ExportCookieHeaderAsync(
            BuiltInVideoSitePlugin.PluginId,
            BuiltInVideoSitePlugin.DefaultCredentialProfileId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (await HasCredentialsAsync(cancellationToken).ConfigureAwait(false))
        {
            var subscription = GetAccountSubscription();
            Register(subscription);
            try
            {
                await _auth.ClearAsync(subscription, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Local credential deletion must still complete if the remote session is already invalid.
            }
        }

        await DeleteCredentialCopiesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PluginAccountProfile?> GetAccountProfileAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await HasCredentialsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!load.IsAvailable)
        {
            throw new InvalidOperationException(load.ErrorMessage);
        }

        var subscription = GetAccountSubscription();
        Register(subscription);
        try
        {
            return await _account.GetProfileAsync(subscription, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginInvocationException exception) when (
            string.Equals(exception.Code, "AUTH_EXPIRED", StringComparison.Ordinal))
        {
            return null;
        }
    }

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

    private async Task MigrateLegacyCredentialAsync(CancellationToken cancellationToken)
    {
        if (await _credentials.ExistsAsync(
            BuiltInVideoSitePlugin.PluginId,
            BuiltInVideoSitePlugin.DefaultCredentialProfileId,
            cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var cookie = _legacyCookieStore.GetCookieString();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            await _credentials.ImportCookieHeaderAsync(
                BuiltInVideoSitePlugin.PluginId,
                BuiltInVideoSitePlugin.DefaultCredentialProfileId,
                cookie,
                _sitePackage?.Manifest.Auth?.ExposedCookieNames?
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static FeedPeek.Core.Models.FeedSubscription GetAccountSubscription()
        => BuiltInVideoSitePlugin.CreateSubscriptions(isEnabled: true)[0];

    private async Task DeleteCredentialCopiesAsync(CancellationToken cancellationToken)
    {
        await _credentials.DeleteProfileAsync(
            BuiltInVideoSitePlugin.PluginId,
            BuiltInVideoSitePlugin.DefaultCredentialProfileId,
            cancellationToken).ConfigureAwait(false);
        using var legacyAuth = new BiliKernelAuthService(_legacyCookieStore);
        await legacyAuth.SignOutAsync(cancellationToken).ConfigureAwait(false);
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
