namespace FeedPeek.Core.Models;

/// <summary>
/// The portable, user-owned portion of the FeedPeek configuration database.
/// Credentials, items, read/saved state, and polling state are deliberately not
/// part of this model.
/// </summary>
public sealed record FeedConfigurationSnapshot(
    IReadOnlyList<SourceDefinition> Sources,
    IReadOnlyList<FeedSubscription> Subscriptions,
    IReadOnlyList<SourceNotificationRule> SourceNotifications,
    IReadOnlyList<CreatorNotificationPolicy> CreatorPolicies,
    IReadOnlyList<SourceNotificationKey> NotificationSuppressions);
