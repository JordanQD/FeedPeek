namespace FeedPeek.PluginContracts.Protocol;

public static class PluginFeedIds
{
    public const string Updates = "updates";
    public const string Live = "live";
    public const string History = "history";
    public const string ViewLater = "viewLater";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Updates,
        Live,
        History,
        ViewLater,
    };
}
