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
    IReadOnlyList<PluginViewManifest> Views);

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
