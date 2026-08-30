namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginInvocationContext(
    string SubscriptionId,
    string ProviderId,
    string SourceId,
    string FeedId,
    string? CredentialProfileId,
    IReadOnlyDictionary<string, string>? Options);

public sealed record FeedPagePayload(
    PluginInvocationContext Context,
    string? Cursor,
    int PageSize);
