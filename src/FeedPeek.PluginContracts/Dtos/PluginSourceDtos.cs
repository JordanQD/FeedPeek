namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginSourceCreatePayload(
    PluginInvocationContext Context,
    string? DisplayName,
    IReadOnlyDictionary<string, string>? Options);

public sealed record PluginSourceDefinition(
    string ProviderId,
    string SourceId,
    string DisplayName,
    string? CredentialProfileId,
    IReadOnlyList<PluginFeedDefinition> Feeds,
    IReadOnlyDictionary<string, string>? Options);

public sealed record PluginFeedDefinition(
    string FeedId,
    string DisplayName,
    string Kind,
    bool SupportsPaging,
    bool SupportsNotifications);

public sealed record PluginSourceListResult(
    IReadOnlyList<PluginSourceDefinition> Sources);
