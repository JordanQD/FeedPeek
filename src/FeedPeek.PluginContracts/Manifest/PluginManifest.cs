namespace FeedPeek.PluginContracts.Manifest;

public sealed record PluginManifest(
    string PluginId,
    string Version,
    int ApiVersion,
    string DisplayName,
    string Entry,
    IReadOnlyList<string> PreloadScripts,
    IReadOnlyList<string> Capabilities,
    PluginAuthManifest? Auth,
    PluginNetworkManifest Network,
    IReadOnlyList<PluginViewManifest> Views,
    PluginSourceConfigurationManifest? SourceConfiguration = null,
    string? Runtime = null,
    IReadOnlyDictionary<string, string>? RuntimeOptions = null,
    string? Icon = null,
    string? Website = null,
    PluginNotificationManifest? Notifications = null,
    PluginCreatorResolutionManifest? CreatorResolution = null);

/// <summary>Exact website hosts whose author links the plugin can resolve; does not grant HTTP access.</summary>
public sealed record PluginCreatorResolutionManifest(IReadOnlyList<string> InputHosts);

/// <summary>Source notification defaults; does not grant a Feed notification capability.</summary>
public sealed record PluginNotificationManifest(bool AutoAddToCustom = false);

public sealed record PluginAuthManifest(
    bool Required,
    string Kind,
    IReadOnlyList<string> CookieDomains,
    IReadOnlyList<string> AuthSignalCookies,
    IReadOnlyList<string>? ExposedCookieNames,
    PluginLoginFlowManifest? LoginFlow);

public sealed record PluginLoginFlowManifest(
    string LoginUrl,
    IReadOnlyList<string> SuccessUrlPrefixes,
    IReadOnlyList<string> SuccessCookieNames);

public sealed record PluginNetworkManifest(
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<string> AllowRedirectHosts);

public sealed record PluginViewManifest(
    string Id,
    string Kind,
    string Title);

public sealed record PluginSourceConfigurationManifest(
    IReadOnlyList<PluginSourceFieldManifest> Fields,
    IReadOnlyList<string> DefaultFeedIds);

public sealed record PluginSourceFieldManifest(
    string Key,
    string Label,
    string Kind,
    bool Required,
    string? Placeholder,
    string? DefaultValue);
