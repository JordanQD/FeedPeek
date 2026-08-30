namespace FeedPeek.Core.Models;

public sealed record SourceDefinition(
    string ProviderId,
    string SourceId,
    string DisplayName,
    string? CredentialProfileId,
    IReadOnlyList<FeedDefinition> Feeds,
    IReadOnlyDictionary<string, string>? Options);

public sealed record FeedDefinition(
    string FeedId,
    string DisplayName,
    string Kind,
    bool SupportsPaging,
    bool SupportsNotifications);

public sealed record CreatorDescriptor(
    string ProviderId,
    string SourceId,
    string CreatorKey,
    string DisplayName,
    string? ProfileUrl,
    string? AvatarUrl,
    IReadOnlyList<string> SupportedNotificationKinds);
