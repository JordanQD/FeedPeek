namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginAuthBeginPayload(
    PluginInvocationContext Context,
    IReadOnlyDictionary<string, string>? Options);

public sealed record PluginAuthBeginResult(
    string State,
    IReadOnlyDictionary<string, string>? Data);

public sealed record PluginAuthCompletePayload(
    PluginInvocationContext Context,
    IReadOnlyDictionary<string, string>? Evidence);

public sealed record PluginAuthState(
    string State,
    PluginAccountProfile? Profile,
    string? Message);

public sealed record PluginAccountProfile(
    string AccountId,
    string DisplayName,
    string? AvatarUrl,
    string? ProfileUrl,
    IReadOnlyDictionary<string, string>? Metadata);
