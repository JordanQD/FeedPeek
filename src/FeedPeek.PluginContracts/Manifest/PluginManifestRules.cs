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
}
