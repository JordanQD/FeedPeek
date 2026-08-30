namespace FeedPeek.Core.Models;

public sealed record FeedItem(
    string ProviderId,
    string SourceId,
    string FeedId,
    string ItemId,
    string Title,
    string? CreatorKey,
    string AuthorName,
    string? AuthorUrl,
    string Url,
    DateTimeOffset PublishedAt,
    string? Summary,
    string? ImageUrl,
    string? AuthorImageUrl,
    string? DurationText,
    IReadOnlyDictionary<string, string>? Metadata)
{
    public FeedItemKey Key => new(ProviderId, SourceId, FeedId, ItemId);
}

public readonly record struct FeedItemKey(
    string ProviderId,
    string SourceId,
    string FeedId,
    string ItemId);
