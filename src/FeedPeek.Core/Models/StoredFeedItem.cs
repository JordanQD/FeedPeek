namespace FeedPeek.Core.Models;

public sealed record StoredFeedItem(
    FeedItem Item,
    string SubscriptionId,
    bool IsRead,
    bool IsSaved,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record FeedSourceSummary(
    SourceDefinition Source,
    int ItemCount,
    int UnreadCount);

public sealed record FeedQuery(
    bool UnreadOnly = false,
    string? SourceId = null,
    int Limit = 500,
    bool ReadOnly = false,
    bool SavedOnly = false,
    string? ProviderId = null,
    string? FeedId = null);
