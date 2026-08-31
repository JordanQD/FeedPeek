namespace FeedPeek.PluginContracts.Manifest;

public static class PluginManifestRules
{
    public const string FileName = "plugin.json";

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
