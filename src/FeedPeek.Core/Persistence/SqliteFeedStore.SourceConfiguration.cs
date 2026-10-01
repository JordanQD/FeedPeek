using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;

namespace FeedPeek.Core.Persistence;

public sealed partial class SqliteFeedStore
{
    public async Task<FeedSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT subscription_id, provider_id, source_id, feed_id, display_name, endpoint,
                   credential_profile_id, is_enabled, refresh_interval_ms, options_json
            FROM subscriptions WHERE subscription_id = @id;
            """);
        AddParameter(command, "@id", subscriptionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSubscription(reader) : null;
    }

    /// <summary>Replace address/metadata atomically, preserving source identity, notification rules and user switches.</summary>
    public async Task UpdateSourceConfigurationAsync(SourceDefinition source, IReadOnlyList<FeedSubscription> subscriptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.Count == 0) throw new ArgumentException("No source subscriptions were supplied.");
        foreach (var subscription in subscriptions) ValidateSourceAndSubscription(source, subscription);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var leases = new List<IDisposable>();
        try
        {
            foreach (var subscription in subscriptions.DistinctBy(item => item.FeedId).OrderBy(item => item.FeedId, StringComparer.Ordinal))
                leases.Add(await FeedPollCoordinator.EnterAsync(subscription, cancellationToken).ConfigureAwait(false));
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var now = ToStorageDateTime(DateTimeOffset.UtcNow);
            foreach (var subscription in subscriptions)
            {
                var identity = await ReadSubscriptionIdentityAsync(connection, transaction, subscription.SubscriptionId, cancellationToken)
                    .ConfigureAwait(false);
                if (identity is null || !SubscriptionIdentityMatches(identity.Value, subscription))
                    throw new InvalidOperationException("The source subscription no longer exists or changed identity.");
                await ExecuteAsync(connection, transaction, """
                    UPDATE subscriptions SET display_name = @name, endpoint = @endpoint,
                        options_json = @options, updated_at = @now WHERE subscription_id = @id;
                    DELETE FROM polling_state WHERE subscription_id = @id;
                    """, cancellationToken, ("@name", subscription.DisplayName), ("@endpoint", subscription.Endpoint),
                    ("@options", Serialize(subscription.Options)), ("@now", now), ("@id", subscription.SubscriptionId))
                    .ConfigureAwait(false);
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE sources SET display_name = @name, options_json = @options, updated_at = @now
                    WHERE provider_id = @provider AND source_id = @source;
                """, cancellationToken, ("@name", source.DisplayName), ("@options", Serialize(source.Options)), ("@now", now),
                ("@provider", source.ProviderId), ("@source", source.SourceId)).ConfigureAwait(false);
            foreach (var feed in source.Feeds)
                await ExecuteAsync(connection, transaction, """
                    UPDATE source_feeds SET display_name = @name, kind = @kind,
                        supports_paging = @paging, supports_notifications = @notifications
                    WHERE provider_id = @provider AND source_id = @source AND feed_id = @feed;
                    """, cancellationToken, ("@name", feed.DisplayName), ("@kind", feed.Kind),
                    ("@paging", ToSqliteBoolean(feed.SupportsPaging)), ("@notifications", ToSqliteBoolean(feed.SupportsNotifications)),
                    ("@provider", source.ProviderId), ("@source", source.SourceId), ("@feed", feed.FeedId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            FeedPollSnapshotCache.RemoveSource(source.ProviderId, source.SourceId);
        }
        finally
        {
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
        }
    }
}
