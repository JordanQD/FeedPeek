using FeedPeek.Core.Models;

namespace FeedPeek.Core.Feeds;

public sealed record FeedPageRequest(
    FeedSubscription Subscription,
    string? Cursor,
    int PageSize,
    string? ETag = null,
    DateTimeOffset? LastModified = null);
