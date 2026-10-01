namespace FeedPeek.PluginContracts.Protocol;

public static class PluginMethods
{
    public const string Handshake = "plugin.handshake";
    public const string GetStatus = "plugin.getStatus";
    public const string AuthBegin = "auth.begin";
    public const string AuthComplete = "auth.complete";
    public const string AuthValidate = "auth.validate";
    public const string AuthClear = "auth.clear";
    public const string SourceCreate = "source.create";
    public const string SourceGet = "source.get";
    public const string SourceListFeeds = "source.listFeeds";
    public const string SourceDelete = "source.delete";
    public const string AccountGetProfile = "account.getProfile";
    public const string CreatorGetFollowing = "creator.getFollowing";
    public const string CreatorResolve = "creator.resolve";
    public const string FeedGetUpdates = "feed.getUpdates";
    public const string FeedGetMoreUpdates = "feed.getMoreUpdates";
    public const string FeedGetLive = "feed.getLive";
    public const string AccountGetHistory = "account.getHistory";
    public const string AccountGetMoreHistory = "account.getMoreHistory";
    public const string CollectionGetViewLater = "collection.getViewLater";
    public const string CollectionGetMoreViewLater = "collection.getMoreViewLater";
    public const string ItemGetActions = "item.getActions";
    public const string ItemExecuteAction = "item.executeAction";
    public const string CreatorGetActions = "creator.getActions";
    public const string CreatorExecuteAction = "creator.executeAction";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Handshake, GetStatus, AuthBegin, AuthComplete, AuthValidate, AuthClear,
        SourceCreate, SourceGet, SourceListFeeds, SourceDelete, AccountGetProfile,
        CreatorGetFollowing, CreatorResolve, FeedGetUpdates, FeedGetMoreUpdates, FeedGetLive,
        AccountGetHistory, AccountGetMoreHistory, CollectionGetViewLater,
        CollectionGetMoreViewLater, ItemGetActions, ItemExecuteAction,
        CreatorGetActions, CreatorExecuteAction,
    };
}
