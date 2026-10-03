using FeedPeek.Core.Models;
using FeedPeek.Core.Feeds;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FeedPeek.Core.Persistence;

public sealed partial class SqliteFeedStore
{
    // Keep this dependency-free: Core must not reference the RSS provider project.
    private const string RssProviderId = "builtin.rss";

    /// <summary>Reads one transactionally consistent snapshot of portable configuration.</summary>
    public async Task<FeedConfigurationSnapshot> ExportConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        var sources = new List<SourceDefinition>();
        await using (var command = CreateCommand(connection, transaction, """
            SELECT provider_id, source_id, display_name, credential_profile_id, options_json
            FROM sources
            ORDER BY provider_id, source_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Credential profile identifiers are local handles, never portable credentials.
                sources.Add(new SourceDefinition(
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("display_name")),
                    null,
                    [],
                    DeserializeConfigurationDictionary(GetNullableString(reader, "options_json"))));
            }
        }

        var feedsBySource = sources.ToDictionary(
            source => new SourceNotificationKey(source.ProviderId, source.SourceId),
            _ => new List<FeedDefinition>());
        await using (var command = CreateCommand(connection, transaction, """
            SELECT provider_id, source_id, feed_id, display_name, kind,
                   supports_paging, supports_notifications
            FROM source_feeds
            ORDER BY provider_id, source_id, feed_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = new SourceNotificationKey(
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id")));
                if (feedsBySource.TryGetValue(key, out var feeds))
                {
                    feeds.Add(new FeedDefinition(
                        reader.GetString(reader.GetOrdinal("feed_id")),
                        reader.GetString(reader.GetOrdinal("display_name")),
                        reader.GetString(reader.GetOrdinal("kind")),
                        reader.GetInt64(reader.GetOrdinal("supports_paging")) != 0,
                        reader.GetInt64(reader.GetOrdinal("supports_notifications")) != 0));
                }
            }
        }

        sources = sources
            .Select(source => source with
            {
                Feeds = feedsBySource[new SourceNotificationKey(source.ProviderId, source.SourceId)]
            })
            .ToList();

        var subscriptions = new List<FeedSubscription>();
        await using (var command = CreateCommand(connection, transaction, """
            SELECT subscription_id, provider_id, source_id, feed_id, display_name, endpoint,
                   credential_profile_id, is_enabled, refresh_interval_ms, options_json
            FROM subscriptions
            ORDER BY subscription_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                subscriptions.Add(new FeedSubscription(
                    reader.GetString(reader.GetOrdinal("subscription_id")),
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("feed_id")),
                    reader.GetString(reader.GetOrdinal("display_name")),
                    GetNullableString(reader, "endpoint"),
                    null,
                    reader.GetInt64(reader.GetOrdinal("is_enabled")) != 0,
                    TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("refresh_interval_ms"))),
                    DeserializeConfigurationDictionary(GetNullableString(reader, "options_json"))));
            }
        }

        var sourceNotifications = new List<SourceNotificationRule>();
        await using (var command = CreateCommand(connection, transaction, """
            SELECT provider_id, source_id, update_feed_ids_json, live_feed_ids_json,
                   update_notifications_enabled, live_notifications_enabled, is_automatic
            FROM source_notification_rules
            ORDER BY provider_id, source_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sourceNotifications.Add(new SourceNotificationRule(
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id")),
                    DeserializeRequiredStringList(reader.GetString(reader.GetOrdinal("update_feed_ids_json"))),
                    DeserializeRequiredStringList(reader.GetString(reader.GetOrdinal("live_feed_ids_json"))),
                    reader.GetInt64(reader.GetOrdinal("update_notifications_enabled")) != 0,
                    reader.GetInt64(reader.GetOrdinal("live_notifications_enabled")) != 0,
                    reader.GetInt64(reader.GetOrdinal("is_automatic")) != 0));
            }
        }

        var creatorPolicies = new List<CreatorNotificationPolicy>();
        await using (var command = CreateCommand(connection, transaction, """
            SELECT provider_id, source_id, updates_scope, live_scope,
                   updates_enabled, live_enabled
            FROM creator_notification_policies
            ORDER BY provider_id, source_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                creatorPolicies.Add(new CreatorNotificationPolicy(
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id")),
                    (CreatorNotificationScope)reader.GetInt64(reader.GetOrdinal("updates_scope")),
                    (CreatorNotificationScope)reader.GetInt64(reader.GetOrdinal("live_scope")),
                    reader.GetInt64(reader.GetOrdinal("updates_enabled")) != 0,
                    reader.GetInt64(reader.GetOrdinal("live_enabled")) != 0));
            }
        }

        var suppressions = new List<SourceNotificationKey>();
        await using (var command = CreateCommand(connection, transaction, """
            SELECT provider_id, source_id
            FROM source_notification_suppressions
            ORDER BY provider_id, source_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                suppressions.Add(new SourceNotificationKey(
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id"))));
            }
        }

        var snapshot = new FeedConfigurationSnapshot(
            sources,
            subscriptions,
            sourceNotifications,
            creatorPolicies,
            suppressions);
        ValidateConfiguration(snapshot);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// Validates a complete configuration before any database write. The codec
    /// uses this same check before serializing or importing a snapshot.
    /// </summary>
    public static void ValidateConfiguration(FeedConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Sources);
        ArgumentNullException.ThrowIfNull(snapshot.Subscriptions);
        ArgumentNullException.ThrowIfNull(snapshot.SourceNotifications);
        ArgumentNullException.ThrowIfNull(snapshot.CreatorPolicies);
        ArgumentNullException.ThrowIfNull(snapshot.NotificationSuppressions);

        var sourceKeys = new HashSet<SourceNotificationKey>();
        var feedsBySource = new Dictionary<SourceNotificationKey, HashSet<string>>();
        foreach (var source in snapshot.Sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ValidateIdentifier(source.ProviderId, nameof(source.ProviderId));
            ValidateIdentifier(source.SourceId, nameof(source.SourceId));
            ValidateRequiredText(source.DisplayName, nameof(source.DisplayName));
            ValidateOptionalIdentifier(source.CredentialProfileId, nameof(source.CredentialProfileId));
            ArgumentNullException.ThrowIfNull(source.Feeds);
            if (!sourceKeys.Add(new SourceNotificationKey(source.ProviderId, source.SourceId)))
                throw new ArgumentException("The snapshot contains duplicate source identities.", nameof(snapshot));

            var feedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feed in source.Feeds)
            {
                ArgumentNullException.ThrowIfNull(feed);
                ValidateIdentifier(feed.FeedId, nameof(feed.FeedId));
                ValidateRequiredText(feed.DisplayName, nameof(feed.DisplayName));
                ValidateRequiredText(feed.Kind, nameof(feed.Kind));
                if (!feedIds.Add(feed.FeedId))
                    throw new ArgumentException("The snapshot contains duplicate feed identities.", nameof(snapshot));
            }

            ValidateOptions(source.Options);
            feedsBySource.Add(new SourceNotificationKey(source.ProviderId, source.SourceId), feedIds);
        }

        var subscriptionIds = new HashSet<string>(StringComparer.Ordinal);
        var subscriptionTuples = new HashSet<(SourceNotificationKey Source, string FeedId)>();
        foreach (var subscription in snapshot.Subscriptions)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ValidateIdentifier(subscription.SubscriptionId, nameof(subscription.SubscriptionId));
            ValidateIdentifier(subscription.ProviderId, nameof(subscription.ProviderId));
            ValidateIdentifier(subscription.SourceId, nameof(subscription.SourceId));
            ValidateIdentifier(subscription.FeedId, nameof(subscription.FeedId));
            ValidateRequiredText(subscription.DisplayName, nameof(subscription.DisplayName));
            ValidateOptionalIdentifier(subscription.CredentialProfileId, nameof(subscription.CredentialProfileId));
            if (subscription.RefreshInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(snapshot), "Subscription refresh intervals must be positive.");
            ValidateOptions(subscription.Options);
            if (!subscriptionIds.Add(subscription.SubscriptionId))
                throw new ArgumentException("The snapshot contains duplicate subscription identities.", nameof(snapshot));
            var key = new SourceNotificationKey(subscription.ProviderId, subscription.SourceId);
            if (!feedsBySource.TryGetValue(key, out var feedIds) || !feedIds.Contains(subscription.FeedId))
                throw new ArgumentException("A subscription references a missing source or feed.", nameof(snapshot));
            if (!subscriptionTuples.Add((key, subscription.FeedId)))
                throw new ArgumentException(
                    "The snapshot contains duplicate provider/source/feed subscription identities.", nameof(snapshot));
        }

        var notificationOwners = new Dictionary<SourceNotificationKey, string>();
        var ruleKeys = new HashSet<SourceNotificationKey>();
        foreach (var rule in snapshot.SourceNotifications)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ValidateIdentifier(rule.ProviderId, nameof(rule.ProviderId));
            ValidateIdentifier(rule.SourceId, nameof(rule.SourceId));
            var key = ValidateSourceReference(rule.ProviderId, rule.SourceId, sourceKeys, feedsBySource, nameof(snapshot));
            ArgumentNullException.ThrowIfNull(rule.UpdateFeedIds);
            ArgumentNullException.ThrowIfNull(rule.LiveFeedIds);
            var referencedFeeds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feedId in rule.UpdateFeedIds.Concat(rule.LiveFeedIds))
            {
                ValidateIdentifier(feedId, nameof(feedId));
                if (!feedsBySource[key].Contains(feedId) || !referencedFeeds.Add(feedId))
                    throw new ArgumentException("A source notification rule references an invalid or duplicate feed.", nameof(snapshot));
            }
            if (!ruleKeys.Add(key))
                throw new ArgumentException("The snapshot contains duplicate source notification identities.", nameof(snapshot));
            RegisterNotificationOwner(notificationOwners, key, "source notification rule", nameof(snapshot));
        }

        var policyKeys = new HashSet<SourceNotificationKey>();
        foreach (var policy in snapshot.CreatorPolicies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ValidateIdentifier(policy.ProviderId, nameof(policy.ProviderId));
            ValidateIdentifier(policy.SourceId, nameof(policy.SourceId));
            ValidateSourceReference(policy.ProviderId, policy.SourceId, sourceKeys, feedsBySource, nameof(snapshot));
            ValidateCreatorScope(policy.UpdatesScope, nameof(policy.UpdatesScope));
            ValidateCreatorScope(policy.LiveScope, nameof(policy.LiveScope));
            if (!policyKeys.Add(policy.Key))
                throw new ArgumentException("The snapshot contains duplicate creator policy identities.", nameof(snapshot));
            RegisterNotificationOwner(notificationOwners, policy.Key, "creator notification policy", nameof(snapshot));
        }

        var suppressionKeys = new HashSet<SourceNotificationKey>();
        foreach (var suppression in snapshot.NotificationSuppressions)
        {
            ValidateIdentifier(suppression.ProviderId, nameof(suppression.ProviderId));
            ValidateIdentifier(suppression.SourceId, nameof(suppression.SourceId));
            ValidateSourceReference(suppression.ProviderId, suppression.SourceId, sourceKeys, feedsBySource, nameof(snapshot));
            if (!suppressionKeys.Add(suppression))
                throw new ArgumentException("The snapshot contains duplicate notification suppressions.", nameof(snapshot));
            RegisterNotificationOwner(notificationOwners, suppression, "notification suppression", nameof(snapshot));
        }
    }

    /// <summary>Imports portable configuration by source identity, preserving local data and credentials.</summary>
    public Task ImportConfigurationAsync(
        FeedConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => ImportConfigurationAsync(snapshot, beforeCommit: null, cancellationToken);

    /// <summary>
    /// Imports configuration and invokes <paramref name="beforeCommit"/> after
    /// all SQL statements have succeeded, immediately before the SQL commit.
    /// </summary>
    public async Task ImportConfigurationAsync(
        FeedConfigurationSnapshot snapshot,
        Action? beforeCommit,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(snapshot);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        var existingSources = await ReadSourceCredentialProfilesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var existingSubscriptions = await ReadSubscriptionRowsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        // A subscription id is a database primary key. Never reuse it for another
        // source: the old generic upsert path would otherwise detach old item links.
        foreach (var subscription in snapshot.Subscriptions)
        {
            if (existingSubscriptions.TryGetValue(subscription.SubscriptionId, out var existing)
                && !SubscriptionIdentityMatches(
                    (existing.ProviderId, existing.SourceId, existing.FeedId), subscription))
            {
                throw new InvalidOperationException(
                    $"Subscription '{subscription.SubscriptionId}' already belongs to another source or feed.");
            }
        }

        var existingSubscriptionsByTuple = existingSubscriptions.Values
            .GroupBy(subscription => new SourceFeedKey(
                subscription.ProviderId, subscription.SourceId, subscription.FeedId))
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SubscriptionId, StringComparer.Ordinal).First());
        var importedSubscriptionIdsBySource = snapshot.Sources.ToDictionary(
            source => new SourceNotificationKey(source.ProviderId, source.SourceId),
            _ => new HashSet<string>(StringComparer.Ordinal));
        var importedSubscriptionEnabledById = new Dictionary<string, bool>(StringComparer.Ordinal);

        var now = ToStorageDateTime(DateTimeOffset.UtcNow);
        foreach (var source in snapshot.Sources)
        {
            var sourceKey = new SourceNotificationKey(source.ProviderId, source.SourceId);
            var credentialProfileId = existingSources.TryGetValue(sourceKey, out var localCredential)
                ? localCredential
                : GetNewCredentialProfileId(source.ProviderId);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO sources (
                    provider_id, source_id, display_name, credential_profile_id,
                    options_json, created_at, updated_at)
                VALUES (
                    @provider_id, @source_id, @display_name, @credential_profile_id,
                    @options_json, @created_at, @updated_at)
                ON CONFLICT(provider_id, source_id) DO UPDATE SET
                    display_name = excluded.display_name,
                    options_json = excluded.options_json,
                    updated_at = excluded.updated_at;
                """,
                cancellationToken,
                ("@provider_id", source.ProviderId),
                ("@source_id", source.SourceId),
                ("@display_name", source.DisplayName),
                ("@credential_profile_id", credentialProfileId),
                ("@options_json", Serialize(source.Options)),
                ("@created_at", now),
                ("@updated_at", now)).ConfigureAwait(false);

            foreach (var feed in source.Feeds)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO source_feeds (
                        provider_id, source_id, feed_id, display_name, kind,
                        supports_paging, supports_notifications)
                    VALUES (
                        @provider_id, @source_id, @feed_id, @display_name, @kind,
                        @supports_paging, @supports_notifications)
                    ON CONFLICT(provider_id, source_id, feed_id) DO UPDATE SET
                        display_name = excluded.display_name,
                        kind = excluded.kind,
                        supports_paging = excluded.supports_paging,
                        supports_notifications = excluded.supports_notifications;
                    """,
                    cancellationToken,
                    ("@provider_id", source.ProviderId),
                    ("@source_id", source.SourceId),
                    ("@feed_id", feed.FeedId),
                    ("@display_name", feed.DisplayName),
                    ("@kind", feed.Kind),
                    ("@supports_paging", ToSqliteBoolean(feed.SupportsPaging)),
                    ("@supports_notifications", ToSqliteBoolean(feed.SupportsNotifications))).ConfigureAwait(false);
            }
        }

        foreach (var subscription in snapshot.Subscriptions)
        {
            var sourceKey = new SourceNotificationKey(subscription.ProviderId, subscription.SourceId);
            var tuple = new SourceFeedKey(sourceKey.ProviderId, sourceKey.SourceId, subscription.FeedId);
            var localSubscription = existingSubscriptions.GetValueOrDefault(subscription.SubscriptionId)
                ?? existingSubscriptionsByTuple.GetValueOrDefault(tuple);
            var subscriptionId = localSubscription?.SubscriptionId ?? subscription.SubscriptionId;
            string? credentialProfileId;
            if (localSubscription is not null)
                credentialProfileId = localSubscription.CredentialProfileId;
            else if (existingSources.TryGetValue(sourceKey, out var sourceCredential))
                credentialProfileId = sourceCredential;
            else
                credentialProfileId = GetNewCredentialProfileId(subscription.ProviderId);
            importedSubscriptionIdsBySource[sourceKey].Add(subscriptionId);
            importedSubscriptionEnabledById[subscriptionId] = subscription.IsEnabled;

            await ExecuteAsync(connection, transaction, """
                INSERT INTO subscriptions (
                    subscription_id, provider_id, source_id, feed_id, display_name,
                    endpoint, credential_profile_id, is_enabled, refresh_interval_ms,
                    options_json, created_at, updated_at)
                VALUES (
                    @subscription_id, @provider_id, @source_id, @feed_id, @display_name,
                    @endpoint, @credential_profile_id, @is_enabled, @refresh_interval_ms,
                    @options_json, @created_at, @updated_at)
                ON CONFLICT(subscription_id) DO UPDATE SET
                    display_name = excluded.display_name,
                    endpoint = excluded.endpoint,
                    is_enabled = excluded.is_enabled,
                    refresh_interval_ms = excluded.refresh_interval_ms,
                    options_json = excluded.options_json,
                    updated_at = excluded.updated_at;
                """,
                cancellationToken,
                ("@subscription_id", subscriptionId),
                ("@provider_id", subscription.ProviderId),
                ("@source_id", subscription.SourceId),
                ("@feed_id", subscription.FeedId),
                ("@display_name", subscription.DisplayName),
                ("@endpoint", subscription.Endpoint),
                ("@credential_profile_id", credentialProfileId),
                ("@is_enabled", ToSqliteBoolean(subscription.IsEnabled)),
                ("@refresh_interval_ms", (long)subscription.RefreshInterval.TotalMilliseconds),
                ("@options_json", Serialize(subscription.Options)),
                ("@created_at", now),
                ("@updated_at", now)).ConfigureAwait(false);
        }

        // Keep old rows and their item links for history, but prevent a source
        // snapshot from leaving an extra local feed actively polling.
        foreach (var source in snapshot.Sources)
        {
            var sourceKey = new SourceNotificationKey(source.ProviderId, source.SourceId);
            await ExecuteAsync(connection, transaction, """
                UPDATE subscriptions SET is_enabled = 0, updated_at = @now
                WHERE provider_id = @provider_id AND source_id = @source_id;
                DELETE FROM polling_state
                WHERE subscription_id IN (
                    SELECT subscription_id FROM subscriptions
                    WHERE provider_id = @provider_id AND source_id = @source_id);
                """, cancellationToken,
                ("@now", now), ("@provider_id", sourceKey.ProviderId), ("@source_id", sourceKey.SourceId))
                .ConfigureAwait(false);

            foreach (var subscriptionId in importedSubscriptionIdsBySource[sourceKey])
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE subscriptions SET is_enabled = @is_enabled, updated_at = @now
                    WHERE subscription_id = @subscription_id;
                    """, cancellationToken,
                    ("@is_enabled", ToSqliteBoolean(importedSubscriptionEnabledById[subscriptionId])),
                    ("@now", now), ("@subscription_id", subscriptionId)).ConfigureAwait(false);
            }
        }

        var sourceKeys = snapshot.Sources
            .Select(source => new SourceNotificationKey(source.ProviderId, source.SourceId))
            .ToArray();
        foreach (var key in sourceKeys)
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM source_notification_rules WHERE provider_id = @provider_id AND source_id = @source_id;
                DELETE FROM creator_notification_policies WHERE provider_id = @provider_id AND source_id = @source_id;
                DELETE FROM source_notification_suppressions WHERE provider_id = @provider_id AND source_id = @source_id;
                """, cancellationToken,
                ("@provider_id", key.ProviderId), ("@source_id", key.SourceId)).ConfigureAwait(false);
        }

        foreach (var rule in snapshot.SourceNotifications)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO source_notification_rules (
                    provider_id, source_id, update_feed_ids_json, live_feed_ids_json,
                    update_notifications_enabled, live_notifications_enabled, is_automatic)
                VALUES (@provider_id, @source_id, @updates, @live,
                        @updates_enabled, @live_enabled, @automatic);
                """, cancellationToken,
                ("@provider_id", rule.ProviderId), ("@source_id", rule.SourceId),
                ("@updates", Serialize(rule.UpdateFeedIds)), ("@live", Serialize(rule.LiveFeedIds)),
                ("@updates_enabled", ToSqliteBoolean(rule.UpdateNotificationsEnabled)),
                ("@live_enabled", ToSqliteBoolean(rule.LiveNotificationsEnabled)),
                ("@automatic", ToSqliteBoolean(rule.IsAutomatic))).ConfigureAwait(false);
        }

        foreach (var policy in snapshot.CreatorPolicies)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO creator_notification_policies (
                    provider_id, source_id, updates_scope, live_scope,
                    updates_enabled, live_enabled)
                VALUES (@provider_id, @source_id, @updates_scope, @live_scope,
                        @updates_enabled, @live_enabled);
                """, cancellationToken,
                ("@provider_id", policy.ProviderId), ("@source_id", policy.SourceId),
                ("@updates_scope", (int)policy.UpdatesScope), ("@live_scope", (int)policy.LiveScope),
                ("@updates_enabled", ToSqliteBoolean(policy.UpdatesEnabled)),
                ("@live_enabled", ToSqliteBoolean(policy.LiveEnabled))).ConfigureAwait(false);
        }

        foreach (var suppression in snapshot.NotificationSuppressions)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO source_notification_suppressions (provider_id, source_id)
                VALUES (@provider_id, @source_id);
                """, cancellationToken,
                ("@provider_id", suppression.ProviderId), ("@source_id", suppression.SourceId)).ConfigureAwait(false);
        }

        beforeCommit?.Invoke();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in snapshot.Sources)
            FeedPollSnapshotCache.RemoveSource(source.ProviderId, source.SourceId);
    }

    private static string? GetNewCredentialProfileId(string providerId)
        => string.Equals(providerId, RssProviderId, StringComparison.Ordinal) ? null : "default";

    private static async Task<Dictionary<SourceNotificationKey, string?>> ReadSourceCredentialProfilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SourceNotificationKey, string?>();
        await using var command = CreateCommand(connection, transaction,
            "SELECT provider_id, source_id, credential_profile_id FROM sources;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SourceNotificationKey(reader.GetString(0), reader.GetString(1)),
                GetNullableString(reader, "credential_profile_id"));
        }
        return result;
    }

    private static async Task<Dictionary<string, ExistingSubscription>> ReadSubscriptionRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ExistingSubscription>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, transaction,
            "SELECT subscription_id, provider_id, source_id, feed_id, credential_profile_id FROM subscriptions;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0), new ExistingSubscription(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                GetNullableString(reader, "credential_profile_id")));
        }
        return result;
    }

    private static SourceNotificationKey ValidateSourceReference(
        string providerId,
        string sourceId,
        HashSet<SourceNotificationKey> sourceKeys,
        Dictionary<SourceNotificationKey, HashSet<string>> feedsBySource,
        string parameterName)
    {
        var key = new SourceNotificationKey(providerId, sourceId);
        if (!sourceKeys.Contains(key) || !feedsBySource.ContainsKey(key))
            throw new ArgumentException("A notification entry references a missing source.", parameterName);
        return key;
    }

    private static void RegisterNotificationOwner(
        Dictionary<SourceNotificationKey, string> owners,
        SourceNotificationKey key,
        string owner,
        string parameterName)
    {
        if (owners.TryGetValue(key, out var previousOwner))
            throw new ArgumentException(
                $"A source cannot contain both {previousOwner} and {owner}.", parameterName);
        owners.Add(key, owner);
    }

    private static void ValidateOptions(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null)
            return;
        foreach (var pair in options)
        {
            ValidateIdentifier(pair.Key, nameof(options));
            if (pair.Value is null)
                throw new ArgumentException("Configuration options cannot contain null values.", nameof(options));
        }
    }

    private static void ValidateOptionalIdentifier(string? value, string name)
    {
        if (value is not null)
            ValidateIdentifier(value, name);
    }

    private static void ValidateRequiredText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be empty.", name);
    }

    private static string[] DeserializeRequiredStringList(string json)
    {
        var values = JsonSerializer.Deserialize<string[]>(json, JsonOptions)
            ?? throw new InvalidOperationException("A notification list was null in the database.");
        if (values.Any(value => value is null))
            throw new InvalidOperationException("A notification list contained null.");
        return values;
    }

    private static IReadOnlyDictionary<string, string>? DeserializeConfigurationDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? throw new InvalidOperationException("A configuration options object was null in the database.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("A configuration options object was invalid in the database.", exception);
        }
    }

    private readonly record struct SourceFeedKey(string ProviderId, string SourceId, string FeedId);

    private sealed record ExistingSubscription(
        string SubscriptionId,
        string ProviderId,
        string SourceId,
        string FeedId,
        string? CredentialProfileId);
}
