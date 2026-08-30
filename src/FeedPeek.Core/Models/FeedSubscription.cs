namespace FeedPeek.Core.Models;

public sealed record FeedSubscription(
    string SubscriptionId,
    string ProviderId,
    string SourceId,
    string FeedId,
    string DisplayName,
    string? Endpoint,
    string? CredentialProfileId,
    bool IsEnabled,
    TimeSpan RefreshInterval,
    IReadOnlyDictionary<string, string>? Options);
