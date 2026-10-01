namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginCreatorDescriptor(
    string CreatorKey,
    string DisplayName,
    string? ProfileUrl,
    string? AvatarUrl,
    IReadOnlyList<string> SupportedNotificationKinds,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record PluginCreatorListResult(
    IReadOnlyList<PluginCreatorDescriptor> Creators,
    bool IsComplete = false);

public sealed record PluginCreatorResolvePayload(PluginInvocationContext Context, string Input);

public sealed record PluginCreatorResolveResult(PluginCreatorDescriptor Creator, string FollowState);

public sealed record PluginCreatorReference(
    PluginInvocationContext Context,
    string CreatorKey,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record ExecuteCreatorActionPayload(
    PluginInvocationContext Context,
    string CreatorKey,
    string ActionId,
    IReadOnlyDictionary<string, string>? Payload);

// IsComplete is optional on the wire so native plugins written before the
// completeness signal was introduced remain successful by default.
public sealed record PluginLivePage(
    IReadOnlyList<PluginLiveItem> Items,
    bool IsComplete = true);

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
