namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginCreatorDescriptor(
    string CreatorKey,
    string DisplayName,
    string? ProfileUrl,
    string? AvatarUrl,
    IReadOnlyList<string> SupportedNotificationKinds,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record PluginCreatorListResult(
    IReadOnlyList<PluginCreatorDescriptor> Creators);

public sealed record PluginCreatorReference(
    PluginInvocationContext Context,
    string CreatorKey,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record ExecuteCreatorActionPayload(
    PluginInvocationContext Context,
    string CreatorKey,
    string ActionId,
    IReadOnlyDictionary<string, string>? Payload);

public sealed record PluginLivePage(IReadOnlyList<PluginLiveItem> Items);

public sealed record PluginLiveItem(
    string CreatorKey,
    string DisplayName,
    string Url,
    string? Title,
    string? AvatarUrl,
    string? CoverUrl,
    long? ViewerCount,
    DateTimeOffset? StartedAt,
    IReadOnlyDictionary<string, string>? Metadata);
