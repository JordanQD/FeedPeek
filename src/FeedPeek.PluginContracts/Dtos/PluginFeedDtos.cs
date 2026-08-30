namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginFeedPage(
    IReadOnlyList<PluginFeedItem> Items,
    string? NextCursor,
    bool HasMore);

public sealed record PluginFeedItem(
    string ItemId,
    string Title,
    string Url,
    string AuthorName,
    DateTimeOffset PublishedAt,
    string? CreatorKey,
    string? AuthorUrl,
    string? Summary,
    string? ImageUrl,
    string? AuthorImageUrl,
    string? DurationText,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record PluginItemReference(
    PluginInvocationContext Context,
    string ItemId,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record ExecuteActionPayload(
    PluginInvocationContext Context,
    string ActionId,
    IReadOnlyDictionary<string, string>? Payload);
