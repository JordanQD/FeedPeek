namespace FeedPeek.Core.Models;

public sealed record FeedPage(
    IReadOnlyList<FeedItem> Items,
    string? NextCursor,
    bool HasMore,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool NotModified = false);
