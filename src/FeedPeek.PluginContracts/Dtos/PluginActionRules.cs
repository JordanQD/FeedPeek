namespace FeedPeek.PluginContracts.Dtos;

public static class PluginActionRules
{
    public static IReadOnlySet<string> SupportedDangerLevels { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "none", "warning", "destructive" };

    public static IReadOnlySet<string> SupportedIconSemantics { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "add", "remove", "save", "open", "refresh", "follow", "unfollow", "settings",
        };
}
