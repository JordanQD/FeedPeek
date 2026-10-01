using FeedPeek.Core.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FeedPeek.Core.Persistence;

public sealed partial class SqliteFeedStore
{
    public async Task<IReadOnlyList<SourceNotificationRule>> GetSourceNotificationRulesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT provider_id, source_id, update_feed_ids_json, live_feed_ids_json,
                   update_notifications_enabled, live_notifications_enabled, is_automatic
            FROM source_notification_rules ORDER BY rowid;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<SourceNotificationRule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new(
                reader.GetString(reader.GetOrdinal("provider_id")),
                reader.GetString(reader.GetOrdinal("source_id")),
                JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("update_feed_ids_json")), JsonOptions) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("live_feed_ids_json")), JsonOptions) ?? [],
                reader.GetInt64(reader.GetOrdinal("update_notifications_enabled")) != 0,
                reader.GetInt64(reader.GetOrdinal("live_notifications_enabled")) != 0,
                reader.GetInt64(reader.GetOrdinal("is_automatic")) != 0));
        }
        return rules;
    }

    /// <summary>Only inserts missing automatic rules. Existing switches, order and suppressions are untouched.</summary>
    public async Task<int> SynchronizeSourceNotificationRulesAsync(
        IReadOnlyList<SourceNotificationRule> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var inserted = 0;
        foreach (var rule in candidates.Where(rule => rule.IsAutomatic))
        {
            ValidateNotificationRule(rule);
            // INSERT ... SELECT also tolerates a source deleted after the candidate snapshot was taken.
            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO source_notification_rules (
                    provider_id, source_id, update_feed_ids_json, live_feed_ids_json,
                    update_notifications_enabled, live_notifications_enabled, is_automatic)
                SELECT @provider_id, @source_id, @updates, @live, @updates_enabled, @live_enabled, @automatic
                WHERE EXISTS (SELECT 1 FROM sources WHERE provider_id = @provider_id AND source_id = @source_id)
                  AND NOT EXISTS (SELECT 1 FROM source_notification_suppressions
                                  WHERE provider_id = @provider_id AND source_id = @source_id)
                  AND NOT EXISTS (SELECT 1 FROM creator_notification_policies
                                  WHERE provider_id = @provider_id AND source_id = @source_id)
                ON CONFLICT(provider_id, source_id) DO NOTHING;
                """);
            AddNotificationRuleParameters(command, rule);
            inserted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    /// <summary>Explicit user save/restore; atomically removes the automatic-recreation suppression.</summary>
    public async Task SaveSourceNotificationRuleAsync(
        SourceNotificationRule rule,
        CancellationToken cancellationToken = default)
    {
        ValidateNotificationRule(rule);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction,
            "DELETE FROM source_notification_suppressions WHERE provider_id = @provider_id AND source_id = @source_id;",
            cancellationToken, ("@provider_id", rule.ProviderId), ("@source_id", rule.SourceId)).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO source_notification_rules (
                provider_id, source_id, update_feed_ids_json, live_feed_ids_json,
                update_notifications_enabled, live_notifications_enabled, is_automatic)
            VALUES (@provider_id, @source_id, @updates, @live, @updates_enabled, @live_enabled, @automatic)
            ON CONFLICT(provider_id, source_id) DO UPDATE SET
                update_feed_ids_json = excluded.update_feed_ids_json,
                live_feed_ids_json = excluded.live_feed_ids_json,
                update_notifications_enabled = excluded.update_notifications_enabled,
                live_notifications_enabled = excluded.live_notifications_enabled,
                is_automatic = excluded.is_automatic;
            """);
        AddNotificationRuleParameters(command, rule);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSourceNotificationRuleAsync(
        SourceNotificationKey key,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(key.ProviderId, nameof(key.ProviderId));
        ValidateIdentifier(key.SourceId, nameof(key.SourceId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO source_notification_suppressions (provider_id, source_id)
            SELECT @provider_id, @source_id
            WHERE EXISTS (SELECT 1 FROM sources WHERE provider_id = @provider_id AND source_id = @source_id)
            ON CONFLICT(provider_id, source_id) DO NOTHING;
            DELETE FROM source_notification_rules WHERE provider_id = @provider_id AND source_id = @source_id;
            """, cancellationToken, ("@provider_id", key.ProviderId), ("@source_id", key.SourceId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The host supplies only loaded compatibility runtimes; ordinary plugin declarations are untouched.</summary>
    public async Task RepairLiveFeedNotificationCapabilitiesAsync(
        IReadOnlyList<string> compatibilityProviderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compatibilityProviderIds);
        if (compatibilityProviderIds.Count == 0)
            return;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        foreach (var providerId in compatibilityProviderIds.Distinct(StringComparer.Ordinal))
        {
            ValidateIdentifier(providerId, nameof(providerId));
            await ExecuteAsync(connection, transaction, """
                UPDATE source_feeds SET supports_notifications = 1
                WHERE provider_id = @provider_id AND kind = 'live' AND supports_notifications = 0;
                """, cancellationToken, ("@provider_id", providerId)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateNotificationRule(SourceNotificationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ValidateIdentifier(rule.ProviderId, nameof(rule.ProviderId));
        ValidateIdentifier(rule.SourceId, nameof(rule.SourceId));
        ArgumentNullException.ThrowIfNull(rule.UpdateFeedIds);
        ArgumentNullException.ThrowIfNull(rule.LiveFeedIds);
        foreach (var feedId in rule.UpdateFeedIds.Concat(rule.LiveFeedIds))
            ValidateIdentifier(feedId, nameof(feedId));
    }

    private static void AddNotificationRuleParameters(SqliteCommand command, SourceNotificationRule rule)
    {
        AddParameter(command, "@provider_id", rule.ProviderId);
        AddParameter(command, "@source_id", rule.SourceId);
        AddParameter(command, "@updates", Serialize(rule.UpdateFeedIds));
        AddParameter(command, "@live", Serialize(rule.LiveFeedIds));
        AddParameter(command, "@updates_enabled", ToSqliteBoolean(rule.UpdateNotificationsEnabled));
        AddParameter(command, "@live_enabled", ToSqliteBoolean(rule.LiveNotificationsEnabled));
        AddParameter(command, "@automatic", ToSqliteBoolean(rule.IsAutomatic));
    }

    private const string NotificationSchemaV3Sql = """
        CREATE TABLE IF NOT EXISTS source_notification_rules (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            update_feed_ids_json TEXT NOT NULL,
            live_feed_ids_json TEXT NOT NULL,
            update_notifications_enabled INTEGER NOT NULL CHECK (update_notifications_enabled IN (0, 1)),
            live_notifications_enabled INTEGER NOT NULL CHECK (live_notifications_enabled IN (0, 1)),
            is_automatic INTEGER NOT NULL CHECK (is_automatic IN (0, 1)),
            PRIMARY KEY (provider_id, source_id),
            FOREIGN KEY (provider_id, source_id) REFERENCES sources (provider_id, source_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS source_notification_suppressions (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            PRIMARY KEY (provider_id, source_id),
            FOREIGN KEY (provider_id, source_id) REFERENCES sources (provider_id, source_id) ON DELETE CASCADE
        );
        """;
}
