using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
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

namespace FeedPeek.Services;

internal sealed record PluginLoadResult(bool IsAvailable, string? ErrorMessage);

internal sealed record PluginSourceCreationResult(
    SourceDefinition Source,
    IReadOnlyList<FeedSubscription> Subscriptions);

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
    private readonly PluginClient _client;
    private readonly PluginInstallationService _installations = new();
    private readonly Dictionary<string, PluginPackage> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IFeedProvider> _providers = new(StringComparer.Ordinal);
    private PluginPackage? _sitePackage;
    private string? _changeToken;
    private bool _loaded;
    private bool _disposed;

    public FeedPeekPluginRuntime(CookieStore cookieStore)
    {
        _legacyCookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _httpBridge = new PluginHttpBridge(_credentials);
        _catalog = new PluginRuntimeCatalog(new PluginHostServices(
            _httpBridge,
            _credentials,
            TracePluginLogSink.Instance,
            _invocations,
            _credentials));
        _client = new PluginClient(new PluginRuntimeCatalogInvoker(_catalog));
    }

    public IReadOnlyList<IFeedProvider> FeedProviders => _providers.Values.ToArray();

    /// <summary>Provider ids the currently loaded plugin set exposes; used to drop stale registrations.</summary>
    public IReadOnlyList<string> AvailableProviderIds => _providers.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public bool IsPluginProvider(string providerId) => _providers.ContainsKey(providerId);

    public bool SupportsCapability(string providerId, string capability)
        => _packages.TryGetValue(providerId, out var package)
            && package.Manifest.Capabilities.Contains(capability, StringComparer.Ordinal);

    public PluginManifest? GetManifest(string pluginId)
        => _packages.TryGetValue(pluginId, out var package) ? package.Manifest : null;

    public Task<IReadOnlyList<PluginManagementEntry>> GetPluginEntriesAsync(
        CancellationToken cancellationToken = default)
        => _installations.GetEntriesAsync(cancellationToken);

    public Task<PluginManagementEntry> InstallPluginAsync(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
        => _installations.InstallAsync(sourceDirectory, cancellationToken);

    public Task SetPluginEnabledAsync(
        string pluginId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
        => _installations.SetEnabledAsync(pluginId, isEnabled, cancellationToken);

    public Task UninstallPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => _installations.UninstallAsync(pluginId, cancellationToken);

    public async Task<PluginLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Fast path: nothing changed since the last load. The token covers installing, enabling,
        // disabling and uninstalling, so a plugin added from Settings starts working in the sessions
        // that are already alive instead of waiting for an app restart.
        var changeToken = _installations.ComputeChangeToken();
        if (_loaded && string.Equals(changeToken, _changeToken, StringComparison.Ordinal))
        {
            return new PluginLoadResult(true, null);
        }

        var configuration = await _installations.GetLoadConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = await _catalog.LoadAsync(
            configuration.Roots,
            configuration.DisabledPluginIds,
            configuration.ExpectedPackageHashes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _loaded = true;
        _changeToken = changeToken;

        _packages.Clear();
        _providers.Clear();
        foreach (var result in results.Where(result => result.IsAvailable && result.Package is not null))
        {
            var package = result.Package!;
            _packages[package.Manifest.PluginId] = package;
            _providers[package.Manifest.PluginId] = new PluginFeedProvider(
                package.Manifest.PluginId,
                _client);
        }

        var sitePlugin = results.FirstOrDefault(result =>
            string.Equals(
                result.Package?.Manifest.PluginId,
                LegacyVideoSiteCompatibility.PluginId,
                StringComparison.Ordinal)
            || string.Equals(
                Path.GetFileName(result.DirectoryPath),
                LegacyVideoSiteCompatibility.PluginId,
                StringComparison.Ordinal));
        if (sitePlugin?.IsAvailable == true)
        {
            _sitePackage = sitePlugin.Package;
            await MigrateLegacyCredentialAsync(cancellationToken).ConfigureAwait(false);
        }
        return new PluginLoadResult(true, null);
    }

    public void Register(FeedPeek.Core.Models.FeedSubscription subscription)
        => _invocations.Register(new PluginInvocationContext(
            subscription.SubscriptionId,
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            subscription.CredentialProfileId,
            subscription.Options));

    public PluginLoginDescriptor GetLoginDescriptor(
        string pluginId,
        string credentialProfileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_packages.TryGetValue(pluginId, out var package))
            throw new InvalidOperationException("网站插件尚未加载或已停用。");
        var auth = package.Manifest.Auth;
        if (auth is null
            || !string.Equals(auth.Kind, "webview-cookie", StringComparison.Ordinal)
            || auth.LoginFlow is null)
        {
            throw new InvalidOperationException("站点插件没有声明可用的 WebView 登录流程。");
        }

        return new PluginLoginDescriptor(
            package.Manifest.PluginId,
            credentialProfileId,
            package.Manifest.DisplayName,
            auth);
    }

    public async Task<bool> HasCredentialsAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
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
            pluginId,
            credentialProfileId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveWebCookiesAsync(
        PluginLoginDescriptor descriptor,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(cookies);
        if (!_packages.ContainsKey(descriptor.PluginId)
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
        var subscription = CreateManagementSubscription(
            descriptor.PluginId,
            descriptor.CredentialProfileId);
        Register(subscription);
        try
        {
            var auth = new PluginAuthService(descriptor.PluginId, _client);
            var completed = await auth.CompleteAsync(subscription, cancellationToken)
                .ConfigureAwait(false);
            var validated = await auth.ValidateAsync(subscription, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(completed.State, "authenticated", StringComparison.Ordinal)
                || !string.Equals(validated.State, "authenticated", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(validated.Message ?? "插件未能验证登录状态。");
            }

            PluginAccountEvents.RaiseChanged();
            NotificationStateChanges.MarkProvider(descriptor.PluginId);
            AppSettings.NotifyNotificationSettingsChanged();
        }
        catch
        {
            await _credentials.DeleteProfileAsync(
                descriptor.PluginId,
                descriptor.CredentialProfileId,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task BeginAuthenticationAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
    {
        var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!load.IsAvailable)
        {
            throw new InvalidOperationException(load.ErrorMessage);
        }

        var subscription = CreateManagementSubscription(pluginId, credentialProfileId);
        Register(subscription);
        var result = await new PluginAuthService(pluginId, _client)
            .BeginAsync(subscription, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.State, "webviewRequired", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("插件没有启动 WebView 登录流程。");
        }
    }

    public async Task ClearCredentialsAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
    {
        if (await HasCredentialsAsync(pluginId, credentialProfileId, cancellationToken).ConfigureAwait(false))
        {
            var subscription = CreateManagementSubscription(pluginId, credentialProfileId);
            Register(subscription);
            try
            {
                await new PluginAuthService(pluginId, _client)
                    .ClearAsync(subscription, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Local credential deletion must still complete if the remote session is already invalid.
            }
        }

        await _credentials.DeleteProfileAsync(pluginId, credentialProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(pluginId, LegacyVideoSiteCompatibility.PluginId, StringComparison.Ordinal)
            && string.Equals(credentialProfileId, LegacyVideoSiteCompatibility.DefaultCredentialProfileId, StringComparison.Ordinal))
        {
            _legacyCookieStore.Clear();
        }
        NotificationStateChanges.MarkProvider(pluginId);
        AppSettings.NotifyNotificationSettingsChanged();
        PluginAccountEvents.RaiseChanged();
    }

    public async Task<PluginAccountProfile?> GetAccountProfileAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!await HasCredentialsAsync(pluginId, credentialProfileId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var load = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!load.IsAvailable)
        {
            throw new InvalidOperationException(load.ErrorMessage);
        }

        var subscription = CreateManagementSubscription(pluginId, credentialProfileId);
        Register(subscription);
        try
        {
            return await new PluginAccountService(pluginId, _client)
                .GetProfileAsync(subscription, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginInvocationException exception) when (
            string.Equals(exception.Code, "AUTH_EXPIRED", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task ExecuteViewLaterActionAsync(
        FeedSubscription subscription,
        FeedItem item,
        bool isSaved,
        CancellationToken cancellationToken)
    {
        var actionId = isSaved ? "viewLater.add" : "viewLater.remove";
        var itemActions = new PluginItemActionService(subscription.ProviderId, _client);
        var actions = await itemActions.GetActionsAsync(subscription, item, cancellationToken)
            .ConfigureAwait(false);
        var action = actions.FirstOrDefault(candidate =>
            string.Equals(candidate.ActionId, actionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("站点插件没有为当前条目提供稍后再看操作。");
        await itemActions.ExecuteAsync(subscription, action, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CreatorDescriptor>> GetFollowingCreatorsAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => new PluginCreatorService(subscription.ProviderId, _client)
            .GetFollowingAsync(subscription, cancellationToken);

    public Task<FollowingCreatorsPage> GetFollowingCreatorPageAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => new PluginCreatorService(subscription.ProviderId, _client)
            .GetFollowingPageAsync(subscription, cancellationToken);

    public Task<CreatorResolutionResult> ResolveCreatorAsync(
        FeedSubscription subscription, string input,
        CancellationToken cancellationToken = default)
        => new PluginCreatorService(subscription.ProviderId, _client)
            .ResolveAsync(subscription, input, cancellationToken);

    public Task<IReadOnlyList<PluginLiveItem>> GetLiveItemsAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => new PluginCreatorService(subscription.ProviderId, _client)
            .GetLiveAsync(subscription, cancellationToken);

    public Task<PluginLivePage> GetLivePageAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
        => new PluginCreatorService(subscription.ProviderId, _client)
            .GetLivePageAsync(subscription, cancellationToken);

    public Task<ActionDescriptor[]> GetCreatorActionsAsync(
        FeedSubscription subscription,
        FeedItem item,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.CreatorKey))
            return Task.FromResult(Array.Empty<ActionDescriptor>());
        return new PluginCreatorService(subscription.ProviderId, _client).GetActionsAsync(
            subscription,
            item.CreatorKey,
            item.Metadata,
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> ExecuteCreatorActionAsync(
        FeedSubscription subscription,
        FeedItem item,
        ActionDescriptor action,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.CreatorKey))
            throw new InvalidOperationException("当前条目没有可操作的作者标识。");
        return new PluginCreatorService(subscription.ProviderId, _client).ExecuteAsync(
            subscription,
            item.CreatorKey,
            action,
            cancellationToken);
    }

    public async Task<PluginSourceCreationResult> CreateSourceAsync(
        string pluginId,
        string displayName,
        IReadOnlyDictionary<string, string>? options,
        string? credentialProfileId,
        IReadOnlyCollection<string>? selectedFeedIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!_packages.TryGetValue(pluginId, out var package))
            throw new InvalidOperationException("网站插件尚未加载或已停用。");
        if (package.Manifest.Auth?.Required == true
            && (string.IsNullOrWhiteSpace(credentialProfileId)
                || !await HasCredentialsAsync(pluginId, credentialProfileId, cancellationToken).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("此网站来源需要先登录。");
        }

        var sourceId = $"source-{Guid.NewGuid():N}";
        var management = CreateManagementSubscription(
            pluginId,
            credentialProfileId,
            sourceId,
            options);
        Register(management);
        var definition = await new PluginSourceService(pluginId, _client)
            .CreateAsync(management, displayName.Trim(), options, cancellationToken)
            .ConfigureAwait(false);
        ValidateSourceDefinition(pluginId, sourceId, credentialProfileId, definition);

        var feeds = definition.Feeds.Select(feed => new FeedDefinition(
            feed.FeedId,
            feed.DisplayName,
            feed.Kind,
            feed.SupportsPaging,
            feed.SupportsNotifications)).ToArray();
        var selected = selectedFeedIds is { Count: > 0 }
            ? selectedFeedIds.ToHashSet(StringComparer.Ordinal)
            : package.Manifest.SourceConfiguration?.DefaultFeedIds?.ToHashSet(StringComparer.Ordinal)
                ?? feeds.Select(feed => feed.FeedId).ToHashSet(StringComparer.Ordinal);
        var source = new SourceDefinition(
            pluginId,
            sourceId,
            definition.DisplayName,
            credentialProfileId,
            feeds,
            definition.Options);
        var subscriptions = feeds.Where(feed => selected.Contains(feed.FeedId))
            .Select(feed => new FeedSubscription(
                $"{pluginId}:{sourceId}:{feed.FeedId}",
                pluginId,
                sourceId,
                feed.FeedId,
                $"{definition.DisplayName} · {feed.DisplayName}",
                Endpoint: null,
                credentialProfileId,
                IsEnabled: true,
                TimeSpan.FromMinutes(15),
                definition.Options))
            .ToArray();
        if (subscriptions.Length == 0)
            throw new InvalidOperationException("至少要选择一个来源视图。");
        foreach (var subscription in subscriptions)
            Register(subscription);
        return new PluginSourceCreationResult(source, subscriptions);
    }

    public async Task<SourceDefinition> ResolveRoomSourceAsync(
        FeedSubscription subscription,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
    {
        var manifest = GetManifest(subscription.ProviderId)
            ?? throw new InvalidOperationException("网站插件尚未加载或已停用。");
        if (PluginManifestRules.ResolveRuntime(manifest) != PluginManifestRules.LiveParseRuntime)
            throw new InvalidOperationException("当前插件不支持修改直播间链接。");
        var probe = subscription with { Options = options };
        Register(probe);
        try
        {
            var definition = await new PluginSourceService(subscription.ProviderId, _client)
                .GetAsync(probe, cancellationToken).ConfigureAwait(false);
            ValidateSourceDefinition(subscription.ProviderId, subscription.SourceId, subscription.CredentialProfileId, definition);
            return new SourceDefinition(definition.ProviderId, definition.SourceId, definition.DisplayName,
                definition.CredentialProfileId, definition.Feeds.Select(feed => new FeedDefinition(
                    feed.FeedId, feed.DisplayName, feed.Kind, feed.SupportsPaging, feed.SupportsNotifications)).ToArray(),
                definition.Options);
        }
        finally { Register(subscription); }
    }

    public async Task DeleteSourceAsync(
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        if (!IsPluginProvider(subscription.ProviderId))
            return;
        Register(subscription);
        await new PluginSourceService(subscription.ProviderId, _client)
            .DeleteAsync(subscription, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateLegacyCredentialAsync(CancellationToken cancellationToken)
    {
        if (await _credentials.ExistsAsync(
            LegacyVideoSiteCompatibility.PluginId,
            LegacyVideoSiteCompatibility.DefaultCredentialProfileId,
            cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var cookie = _legacyCookieStore.GetCookieString();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            await _credentials.ImportCookieHeaderAsync(
                LegacyVideoSiteCompatibility.PluginId,
                LegacyVideoSiteCompatibility.DefaultCredentialProfileId,
                cookie,
                _sitePackage?.Manifest.Auth?.ExposedCookieNames?
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            NotificationStateChanges.MarkProvider(LegacyVideoSiteCompatibility.PluginId);
            AppSettings.NotifyNotificationSettingsChanged();
        }
    }

    private static FeedSubscription CreateManagementSubscription(
        string pluginId,
        string? credentialProfileId,
        string sourceId = "account-default",
        IReadOnlyDictionary<string, string>? options = null)
        => new(
            $"{pluginId}:{sourceId}:management",
            pluginId,
            sourceId,
            "management",
            "插件管理",
            Endpoint: null,
            credentialProfileId,
            IsEnabled: true,
            TimeSpan.FromMinutes(15),
            options);

    private static void ValidateSourceDefinition(
        string pluginId,
        string sourceId,
        string? credentialProfileId,
        PluginSourceDefinition definition)
    {
        if (!string.Equals(definition.ProviderId, pluginId, StringComparison.Ordinal)
            || !string.Equals(definition.SourceId, sourceId, StringComparison.Ordinal)
            || !string.Equals(definition.CredentialProfileId, credentialProfileId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(definition.DisplayName)
            || definition.Feeds.Count == 0
            || definition.Feeds.Any(feed => string.IsNullOrWhiteSpace(feed.FeedId)))
        {
            throw new InvalidOperationException("插件返回了无效的来源定义。");
        }
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
