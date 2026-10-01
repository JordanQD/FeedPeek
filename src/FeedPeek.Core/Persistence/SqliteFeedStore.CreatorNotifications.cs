using FeedPeek.Core.Models;
using Microsoft.Data.Sqlite;

namespace FeedPeek.Core.Persistence;

public sealed partial class SqliteFeedStore
{
    public async Task<IReadOnlyList<CreatorNotificationPolicy>> GetCreatorNotificationPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT provider_id, source_id, updates_scope, live_scope,
                   updates_enabled, live_enabled
            FROM creator_notification_policies
            ORDER BY rowid;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var policies = new List<CreatorNotificationPolicy>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var providerId = reader.GetString(reader.GetOrdinal("provider_id"));
            var sourceId = reader.GetString(reader.GetOrdinal("source_id"));
            var updatesScope = ReadCreatorScope(reader.GetInt64(reader.GetOrdinal("updates_scope")), "updates_scope");
            var liveScope = ReadCreatorScope(reader.GetInt64(reader.GetOrdinal("live_scope")), "live_scope");
            ValidateIdentifier(providerId, nameof(providerId));
            ValidateIdentifier(sourceId, nameof(sourceId));
            policies.Add(new(
                providerId,
                sourceId,
                updatesScope,
                liveScope,
                reader.GetInt64(reader.GetOrdinal("updates_enabled")) != 0,
                reader.GetInt64(reader.GetOrdinal("live_enabled")) != 0));
        }

        return policies;
    }

    public async Task SaveCreatorNotificationPolicyAsync(
        CreatorNotificationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ValidateCreatorNotificationPolicy(policy);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await ClearCreatorSuppressionAsync(connection, transaction, policy.ProviderId, policy.SourceId, cancellationToken)
            .ConfigureAwait(false);
        await InsertOrUpdateCreatorPolicyAsync(connection, transaction, policy, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureCreatorNotificationPolicyAsync(
        CreatorNotificationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ValidateCreatorNotificationPolicy(policy);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await ClearCreatorSuppressionAsync(connection, transaction, policy.ProviderId, policy.SourceId, cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO creator_notification_policies (
                provider_id, source_id, updates_scope, live_scope,
                updates_enabled, live_enabled)
            SELECT @provider_id, @source_id, @updates_scope, @live_scope,
                   @updates_enabled, @live_enabled
            WHERE EXISTS (
                SELECT 1 FROM sources
                WHERE provider_id = @provider_id AND source_id = @source_id)
            ON CONFLICT(provider_id, source_id) DO NOTHING;
            """);
        AddCreatorPolicyParameters(command, policy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCreatorNotificationPolicyAsync(
        SourceNotificationKey key,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceNotificationKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            DELETE FROM creator_notification_policies
            WHERE provider_id = @provider_id AND source_id = @source_id;
            DELETE FROM source_notification_rules
            WHERE provider_id = @provider_id AND source_id = @source_id;
            INSERT INTO source_notification_suppressions (provider_id, source_id)
            SELECT @provider_id, @source_id
            WHERE EXISTS (
                SELECT 1 FROM sources
                WHERE provider_id = @provider_id AND source_id = @source_id)
            ON CONFLICT(provider_id, source_id) DO NOTHING;
            """,
            cancellationToken,
            ("@provider_id", key.ProviderId),
            ("@source_id", key.SourceId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MigrateCreatorNotificationPoliciesAsync(
        IReadOnlyList<CreatorNotificationPolicy> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var policy in candidates)
            ValidateCreatorNotificationPolicy(policy);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var changed = 0;
        foreach (var policy in candidates
            .GroupBy(item => item.Key)
            .Select(group => group.First()))
        {
            await using (var insert = CreateCommand(connection, transaction, """
                INSERT INTO creator_notification_policies (
                    provider_id, source_id, updates_scope, live_scope,
                    updates_enabled, live_enabled)
                SELECT @provider_id, @source_id, @updates_scope, @live_scope,
                       @updates_enabled, @live_enabled
                WHERE EXISTS (
                    SELECT 1 FROM sources
                    WHERE provider_id = @provider_id AND source_id = @source_id)
                  AND NOT EXISTS (
                    SELECT 1 FROM source_notification_suppressions
                    WHERE provider_id = @provider_id AND source_id = @source_id)
                ON CONFLICT(provider_id, source_id) DO NOTHING;
                """))
            {
                AddCreatorPolicyParameters(insert, policy);
                changed += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // A source rule is retired only after a policy exists. If a suppression
            // blocked insertion, the old rule and the user's choice remain intact.
            await using var deleteRule = CreateCommand(connection, transaction, """
                DELETE FROM source_notification_rules
                WHERE provider_id = @provider_id AND source_id = @source_id
                  AND EXISTS (
                    SELECT 1 FROM creator_notification_policies
                    WHERE provider_id = @provider_id AND source_id = @source_id);
                """);
            deleteRule.Parameters.AddWithValue("@provider_id", policy.ProviderId);
            deleteRule.Parameters.AddWithValue("@source_id", policy.SourceId);
            changed += await deleteRule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static async Task ClearCreatorSuppressionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string providerId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM source_notification_suppressions WHERE provider_id = @provider_id AND source_id = @source_id;",
            cancellationToken,
            ("@provider_id", providerId),
            ("@source_id", sourceId)).ConfigureAwait(false);
    }

    private static async Task InsertOrUpdateCreatorPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CreatorNotificationPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO creator_notification_policies (
                provider_id, source_id, updates_scope, live_scope,
                updates_enabled, live_enabled)
            VALUES (@provider_id, @source_id, @updates_scope, @live_scope,
                    @updates_enabled, @live_enabled)
            ON CONFLICT(provider_id, source_id) DO UPDATE SET
                updates_scope = excluded.updates_scope,
                live_scope = excluded.live_scope,
                updates_enabled = excluded.updates_enabled,
                live_enabled = excluded.live_enabled;
            """);
        AddCreatorPolicyParameters(command, policy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddCreatorPolicyParameters(SqliteCommand command, CreatorNotificationPolicy policy)
    {
        command.Parameters.AddWithValue("@provider_id", policy.ProviderId);
        command.Parameters.AddWithValue("@source_id", policy.SourceId);
        command.Parameters.AddWithValue("@updates_scope", (int)policy.UpdatesScope);
        command.Parameters.AddWithValue("@live_scope", (int)policy.LiveScope);
        command.Parameters.AddWithValue("@updates_enabled", ToSqliteBoolean(policy.UpdatesEnabled));
        command.Parameters.AddWithValue("@live_enabled", ToSqliteBoolean(policy.LiveEnabled));
    }

    private static void ValidateCreatorNotificationPolicy(CreatorNotificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateIdentifier(policy.ProviderId, nameof(policy.ProviderId));
        ValidateIdentifier(policy.SourceId, nameof(policy.SourceId));
        ValidateCreatorScope(policy.UpdatesScope, nameof(policy.UpdatesScope));
        ValidateCreatorScope(policy.LiveScope, nameof(policy.LiveScope));
    }

    private static void ValidateSourceNotificationKey(SourceNotificationKey key)
    {
        ValidateIdentifier(key.ProviderId, nameof(key.ProviderId));
        ValidateIdentifier(key.SourceId, nameof(key.SourceId));
    }

    private static CreatorNotificationScope ReadCreatorScope(long value, string name)
    {
        if (value is < 0 or > 1)
            throw new InvalidOperationException($"Invalid creator notification scope in {name}: {value}.");
        return (CreatorNotificationScope)value;
    }

    private static void ValidateCreatorScope(CreatorNotificationScope value, string name)
    {
        if (value is not (CreatorNotificationScope.SelectedCreators or CreatorNotificationScope.AllFollowing))
            throw new ArgumentOutOfRangeException(name, value, "Unsupported creator notification scope.");
    }

    private const string CreatorNotificationSchemaV4Sql = """
        CREATE TABLE IF NOT EXISTS creator_notification_policies (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            updates_scope INTEGER NOT NULL CHECK (updates_scope IN (0, 1)),
            live_scope INTEGER NOT NULL CHECK (live_scope IN (0, 1)),
            updates_enabled INTEGER NOT NULL CHECK (updates_enabled IN (0, 1)),
            live_enabled INTEGER NOT NULL CHECK (live_enabled IN (0, 1)),
            PRIMARY KEY (provider_id, source_id),
            FOREIGN KEY (provider_id, source_id) REFERENCES sources (provider_id, source_id) ON DELETE CASCADE
        );
        """;
}
