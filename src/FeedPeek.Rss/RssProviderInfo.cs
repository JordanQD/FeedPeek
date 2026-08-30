namespace FeedPeek.Rss;

public static class RssProviderInfo
{
    public const string ProviderId = "builtin.rss";
    public const string UpdatesFeedId = "updates";
    public const int MaxFeedBytes = 4 * 1024 * 1024;
    public const int MaxHtmlDiscoveryBytes = 1024 * 1024;
    public const int MaxItems = 200;
}
