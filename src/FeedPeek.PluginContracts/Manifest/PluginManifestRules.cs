namespace FeedPeek.PluginContracts.Manifest;

public static class PluginManifestRules
{
    public const string FileName = "plugin.json";

    /// <summary>Native FeedPeek plugin contract: <c>globalThis.FeedPeekPlugin.handle(request)</c>.</summary>
    public const string NativeRuntime = "feedpeek-v1";

    /// <summary>LiveParse-compatible plugin contract: <c>globalThis.LiveParsePlugin.*</c> methods.</summary>
    public const string LiveParseRuntime = "liveparse-v1";

    public static IReadOnlySet<string> SupportedRuntimes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NativeRuntime, LiveParseRuntime };

    public static string ResolveRuntime(PluginManifest manifest)
        => string.IsNullOrWhiteSpace(manifest.Runtime) ? NativeRuntime : manifest.Runtime;

    /// <summary>Compatibility rooms use the host policy; native plugins must opt in.</summary>
    public static bool ShouldAutoAddToCustomNotifications(PluginManifest manifest)
        => string.Equals(ResolveRuntime(manifest), LiveParseRuntime, StringComparison.Ordinal)
            || manifest.Notifications?.AutoAddToCustom == true;


    public static IReadOnlySet<string> SupportedCapabilities { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "feed.updates",
            "feed.live",
            "account.auth",
            "account.profile",
            "account.history",
            "collection.viewLater",
            "creator.following",
            "creator.resolve",
            "creator.relation",
            "item.actions",
            "creator.actions",
            "source.configuration",
        };

    public static IReadOnlySet<string> SupportedAuthKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "webview-cookie", "opaque-secret" };

    public static IReadOnlySet<string> SupportedViewKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "feed", "collection", "live" };

    public static IReadOnlySet<string> SupportedSourceFieldKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "text", "url" };

    /// <summary>Image formats accepted for the optional manifest <c>icon</c>.</summary>
    public static IReadOnlySet<string> SupportedIconExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".ico", ".bmp", ".gif", ".webp",
        };

    /// <summary>Largest plugin icon FeedPeek accepts, so a package cannot bloat the manifest load.</summary>
    public const int MaxIconBytes = 512 * 1024;
}
