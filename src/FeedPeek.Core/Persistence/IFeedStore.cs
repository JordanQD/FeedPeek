using FeedPeek.Core.Models;

namespace FeedPeek.Core.Persistence;

public sealed record FeedUpsertResult(
    IReadOnlyList<FeedItem> InsertedItems,
    IReadOnlyList<FeedItem> UpdatedItems);

public interface IFeedStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveSourceAndSubscriptionAsync(
        SourceDefinition source,
        FeedSubscription subscription,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedSubscription>> GetSubscriptionsAsync(
        CancellationToken cancellationToken = default);

    Task<FeedSubscription?> GetSubscriptionAsync(string subscriptionId,
        CancellationToken cancellationToken = default);

    Task UpdateSourceConfigurationAsync(SourceDefinition source, IReadOnlyList<FeedSubscription> subscriptions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredFeedItem>> GetItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken = default);

    Task<StoredFeedItem?> GetItemAsync(
        FeedItemKey key,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedSourceSummary>> GetSourceSummariesAsync(
        CancellationToken cancellationToken = default);

    Task<FeedUpsertResult> UpsertItemsAsync(
        string subscriptionId,
        IReadOnlyList<FeedItem> items,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default);

    Task MarkReadAsync(
        FeedItemKey key,
        bool isRead = true,
        CancellationToken cancellationToken = default);

    Task MarkSavedAsync(
        FeedItemKey key,
        bool isSaved = true,
        CancellationToken cancellationToken = default);

    Task SetSavedForFeedAsync(
        string providerId,
        string sourceId,
        string feedId,
        bool isSaved,
        CancellationToken cancellationToken = default);

    Task SetSavedForItemAsync(
        string providerId,
        string sourceId,
        string itemId,
        bool isSaved,
        CancellationToken cancellationToken = default);

    Task DeleteSourceAsync(
        string providerId,
        string sourceId,
        CancellationToken cancellationToken = default);

    Task<FeedPollingState?> GetPollingStateAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task SavePollingStateAsync(
        FeedPollingState state,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SourceNotificationRule>> GetSourceNotificationRulesAsync(
        CancellationToken cancellationToken = default);

    Task<int> SynchronizeSourceNotificationRulesAsync(
        IReadOnlyList<SourceNotificationRule> candidates,
        CancellationToken cancellationToken = default);

    Task SaveSourceNotificationRuleAsync(
        SourceNotificationRule rule,
        CancellationToken cancellationToken = default);

    Task DeleteSourceNotificationRuleAsync(
        SourceNotificationKey key,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CreatorNotificationPolicy>> GetCreatorNotificationPoliciesAsync(
        CancellationToken cancellationToken = default);

    Task SaveCreatorNotificationPolicyAsync(
        CreatorNotificationPolicy policy,
        CancellationToken cancellationToken = default);

    Task EnsureCreatorNotificationPolicyAsync(
        CreatorNotificationPolicy policy,
        CancellationToken cancellationToken = default);

    Task DeleteCreatorNotificationPolicyAsync(
        SourceNotificationKey key,
        CancellationToken cancellationToken = default);

    Task<int> MigrateCreatorNotificationPoliciesAsync(
        IReadOnlyList<CreatorNotificationPolicy> candidates,
        CancellationToken cancellationToken = default);

    Task RepairLiveFeedNotificationCapabilitiesAsync(
        IReadOnlyList<string> compatibilityProviderIds,
        CancellationToken cancellationToken = default);
}
