using FeedPeek.Core.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace FeedPeek.Core.Persistence;

public sealed partial class SqliteFeedStore : IFeedStore
{
    private const int SchemaVersion = 4;
    private const int DefaultItemLimit = 500;
    private const int MaxItemLimit = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly string? _databasePath;
    private readonly SqliteConnection? _memoryKeepAliveConnection;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteFeedStore(string databasePathOrConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        if (LooksLikeConnectionString(databasePathOrConnectionString))
        {
            var builder = new SqliteConnectionStringBuilder(databasePathOrConnectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource)
                || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                || builder.Mode == SqliteOpenMode.Memory)
            {
                var dataSource = string.IsNullOrWhiteSpace(builder.DataSource)
                    || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                    ? $"FeedPeek-{Guid.NewGuid():N}"
                    : builder.DataSource;
                builder.DataSource = dataSource;
                builder.Mode = SqliteOpenMode.Memory;
                builder.Cache = SqliteCacheMode.Shared;
                _connectionString = builder.ToString();
                _memoryKeepAliveConnection = new SqliteConnection(_connectionString);
                _memoryKeepAliveConnection.Open();
                return;
            }

            _connectionString = builder.ToString();
            return;
        }

        _databasePath = Path.GetFullPath(databasePathOrConnectionString);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (_databasePath is not null)
            {
                var directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                null,
                "PRAGMA journal_mode = WAL;",
                cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task SaveSourceAndSubscriptionAsync(
        SourceDefinition source,
        FeedSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(subscription);
        ValidateSourceAndSubscription(source, subscription);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var now = ToStorageDateTime(DateTimeOffset.UtcNow);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        var existingSubscription = await ReadSubscriptionIdentityAsync(
            connection,
            transaction,
            subscription.SubscriptionId,
            cancellationToken).ConfigureAwait(false);
        if (existingSubscription is not null
            && !SubscriptionIdentityMatches(existingSubscription.Value, subscription))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM feed_item_subscriptions WHERE subscription_id = @subscription_id; DELETE FROM polling_state WHERE subscription_id = @subscription_id;",
                cancellationToken,
                ("@subscription_id", subscription.SubscriptionId)).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO sources (
                provider_id, source_id, display_name, credential_profile_id, options_json, created_at, updated_at)
            VALUES (
                @provider_id, @source_id, @display_name, @credential_profile_id, @options_json, @created_at, @updated_at)
            ON CONFLICT (provider_id, source_id) DO UPDATE SET
                display_name = excluded.display_name,
                credential_profile_id = excluded.credential_profile_id,
                options_json = excluded.options_json,
                updated_at = excluded.updated_at;
            """,
            cancellationToken,
            ("@provider_id", source.ProviderId),
            ("@source_id", source.SourceId),
            ("@display_name", source.DisplayName),
            ("@credential_profile_id", source.CredentialProfileId),
            ("@options_json", Serialize(source.Options)),
            ("@created_at", now),
            ("@updated_at", now)).ConfigureAwait(false);

        foreach (var feed in source.Feeds ?? [])
        {
            ArgumentNullException.ThrowIfNull(feed);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO source_feeds (
                    provider_id, source_id, feed_id, display_name, kind, supports_paging, supports_notifications)
                VALUES (
                    @provider_id, @source_id, @feed_id, @display_name, @kind, @supports_paging, @supports_notifications)
                ON CONFLICT (provider_id, source_id, feed_id) DO UPDATE SET
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

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO subscriptions (
                subscription_id, provider_id, source_id, feed_id, display_name, endpoint,
                credential_profile_id, is_enabled, refresh_interval_ms, options_json, created_at, updated_at)
            VALUES (
                @subscription_id, @provider_id, @source_id, @feed_id, @display_name, @endpoint,
                @credential_profile_id, @is_enabled, @refresh_interval_ms, @options_json, @created_at, @updated_at)
            ON CONFLICT (subscription_id) DO UPDATE SET
                provider_id = excluded.provider_id,
                source_id = excluded.source_id,
                feed_id = excluded.feed_id,
                display_name = excluded.display_name,
                endpoint = excluded.endpoint,
                credential_profile_id = excluded.credential_profile_id,
                is_enabled = excluded.is_enabled,
                refresh_interval_ms = excluded.refresh_interval_ms,
                options_json = excluded.options_json,
                updated_at = excluded.updated_at;
            """,
            cancellationToken,
            ("@subscription_id", subscription.SubscriptionId),
            ("@provider_id", subscription.ProviderId),
            ("@source_id", subscription.SourceId),
            ("@feed_id", subscription.FeedId),
            ("@display_name", subscription.DisplayName),
            ("@endpoint", subscription.Endpoint),
            ("@credential_profile_id", subscription.CredentialProfileId),
            ("@is_enabled", ToSqliteBoolean(subscription.IsEnabled)),
            ("@refresh_interval_ms", Math.Max(0, (long)subscription.RefreshInterval.TotalMilliseconds)),
            ("@options_json", Serialize(subscription.Options)),
            ("@created_at", now),
            ("@updated_at", now)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FeedSubscription>> GetSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(
            connection,
            null,
            """
            SELECT subscription_id, provider_id, source_id, feed_id, display_name, endpoint,
                   credential_profile_id, is_enabled, refresh_interval_ms, options_json
            FROM subscriptions
            ORDER BY display_name COLLATE NOCASE, subscription_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var subscriptions = new List<FeedSubscription>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            subscriptions.Add(ReadSubscription(reader));
        }

        return subscriptions;
    }

    public async Task<IReadOnlyList<StoredFeedItem>> GetItemsAsync(
        FeedQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(
            connection,
            null,
            """
            SELECT i.provider_id, i.source_id, i.feed_id, i.item_id, i.title,
                   i.creator_key, i.author_name, i.author_url, i.url, i.published_at,
                   i.summary, i.image_url, i.author_image_url, i.duration_text, i.metadata_json,
                   links.subscription_id, links.first_seen_at, links.last_seen_at,
                   COALESCE(states.is_read, 0) AS is_read,
                   COALESCE(states.is_saved, 0) AS is_saved
            FROM feed_items AS i
            INNER JOIN (
                SELECT provider_id, source_id, feed_id, item_id,
                       MIN(subscription_id) AS subscription_id,
                       MIN(first_seen_at) AS first_seen_at,
                       MAX(last_seen_at) AS last_seen_at
                FROM feed_item_subscriptions
                GROUP BY provider_id, source_id, feed_id, item_id
            ) AS links
                ON links.provider_id = i.provider_id
               AND links.source_id = i.source_id
               AND links.feed_id = i.feed_id
               AND links.item_id = i.item_id
            LEFT JOIN item_states AS states
                ON states.provider_id = i.provider_id
               AND states.source_id = i.source_id
               AND states.feed_id = i.feed_id
               AND states.item_id = i.item_id
            WHERE (@unread_only = 0 OR COALESCE(states.is_read, 0) = 0)
              AND (@read_only = 0 OR COALESCE(states.is_read, 0) = 1)
              AND (@saved_only = 0 OR COALESCE(states.is_saved, 0) = 1)
              AND (@provider_id IS NULL OR i.provider_id = @provider_id)
              AND (@source_id IS NULL OR i.source_id = @source_id)
              AND (@feed_id IS NULL OR i.feed_id = @feed_id)
                AND (@item_ids IS NULL OR i.item_id IN (SELECT value FROM json_each(@item_ids)))
            ORDER BY i.published_at DESC, i.item_id COLLATE NOCASE
            LIMIT @limit OFFSET @offset;
            """);
        AddParameter(command, "@unread_only", ToSqliteBoolean(query.UnreadOnly));
        AddParameter(command, "@read_only", ToSqliteBoolean(query.ReadOnly));
        AddParameter(command, "@saved_only", ToSqliteBoolean(query.SavedOnly));
        AddParameter(command, "@provider_id", query.ProviderId);
        AddParameter(command, "@source_id", query.SourceId);
        AddParameter(command, "@feed_id", query.FeedId);
        AddParameter(command, "@item_ids", query.ItemIds is null ? null : Serialize(query.ItemIds));
        AddParameter(command, "@limit", NormalizeLimit(query.Limit));
        AddParameter(command, "@offset", Math.Max(0, query.Offset));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<StoredFeedItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadStoredItem(reader));
        }

        return items;
    }

    public async Task<StoredFeedItem?> GetItemAsync(
        FeedItemKey key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(
            connection,
            null,
            """
            SELECT i.provider_id, i.source_id, i.feed_id, i.item_id, i.title,
                   i.creator_key, i.author_name, i.author_url, i.url, i.published_at,
                   i.summary, i.image_url, i.author_image_url, i.duration_text, i.metadata_json,
                   links.subscription_id, links.first_seen_at, links.last_seen_at,
                   COALESCE(states.is_read, 0) AS is_read,
                   COALESCE(states.is_saved, 0) AS is_saved
            FROM feed_items AS i
            INNER JOIN feed_item_subscriptions AS links
                ON links.provider_id = i.provider_id
               AND links.source_id = i.source_id
               AND links.feed_id = i.feed_id
               AND links.item_id = i.item_id
            LEFT JOIN item_states AS states
                ON states.provider_id = i.provider_id
               AND states.source_id = i.source_id
               AND states.feed_id = i.feed_id
               AND states.item_id = i.item_id
            WHERE i.provider_id = @provider_id
              AND i.source_id = @source_id
              AND i.feed_id = @feed_id
              AND i.item_id = @item_id
            ORDER BY links.subscription_id
            LIMIT 1;
            """);
        AddKeyParameters(command, key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStoredItem(reader)
            : null;
    }

    public async Task<IReadOnlyList<FeedSourceSummary>> GetSourceSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(
            connection,
            null,
            """
            SELECT sources.provider_id, sources.source_id, sources.display_name,
                   sources.credential_profile_id, sources.options_json,
                   (
                       SELECT COUNT(*)
                       FROM feed_items AS items
                       WHERE items.provider_id = sources.provider_id
                         AND items.source_id = sources.source_id
                         AND EXISTS (
                             SELECT 1
                             FROM feed_item_subscriptions AS links
                             WHERE links.provider_id = items.provider_id
                               AND links.source_id = items.source_id
                               AND links.feed_id = items.feed_id
                               AND links.item_id = items.item_id)
                   ) AS item_count,
                   (
                       SELECT COUNT(*)
                       FROM feed_items AS items
                       LEFT JOIN item_states AS states
                         ON states.provider_id = items.provider_id
                        AND states.source_id = items.source_id
                        AND states.feed_id = items.feed_id
                        AND states.item_id = items.item_id
                       WHERE items.provider_id = sources.provider_id
                         AND items.source_id = sources.source_id
                         AND COALESCE(states.is_read, 0) = 0
                         AND EXISTS (
                             SELECT 1
                             FROM feed_item_subscriptions AS links
                             WHERE links.provider_id = items.provider_id
                               AND links.source_id = items.source_id
                               AND links.feed_id = items.feed_id
                               AND links.item_id = items.item_id)
                   ) AS unread_count
            FROM sources
            ORDER BY sources.display_name COLLATE NOCASE, sources.source_id;
            """);

        var sourceRows = new List<SourceSummaryRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sourceRows.Add(new SourceSummaryRow(
                    reader.GetString(reader.GetOrdinal("provider_id")),
                    reader.GetString(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("display_name")),
                    GetNullableString(reader, "credential_profile_id"),
                    GetNullableString(reader, "options_json"),
                    reader.GetInt32(reader.GetOrdinal("item_count")),
                    reader.GetInt32(reader.GetOrdinal("unread_count"))));
            }
        }

        var summaries = new List<FeedSourceSummary>(sourceRows.Count);
        foreach (var row in sourceRows)
        {
            var source = new SourceDefinition(
                row.ProviderId,
                row.SourceId,
                row.DisplayName,
                row.CredentialProfileId,
                await ReadFeedDefinitionsAsync(connection, row.ProviderId, row.SourceId, cancellationToken).ConfigureAwait(false),
                DeserializeDictionary(row.OptionsJson));
            summaries.Add(new FeedSourceSummary(source, row.ItemCount, row.UnreadCount));
        }

        return summaries;
    }

    public async Task<FeedUpsertResult> UpsertItemsAsync(
        string subscriptionId,
        IReadOnlyList<FeedItem> items,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(items);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var normalizedItems = items
            .Select(item => item ?? throw new ArgumentException("The item collection contains null.", nameof(items)))
            .GroupBy(item => item.Key)
            .Select(group => group.Last())
            .ToList();
        var seenAtText = ToStorageDateTime(seenAt);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        var subscription = await ReadSubscriptionIdentityAsync(
            connection,
            transaction,
            subscriptionId,
            cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            throw new InvalidOperationException($"Subscription '{subscriptionId}' does not exist.");
        }

        var inserted = new List<FeedItem>();
        var updated = new List<FeedItem>();
        foreach (var item in normalizedItems)
        {
            ValidateItemForSubscription(item, subscription.Value);
            var exists = await ItemExistsAsync(connection, transaction, item.Key, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO feed_items (
                    provider_id, source_id, feed_id, item_id, title, creator_key, author_name,
                    author_url, url, published_at, summary, image_url, author_image_url,
                    duration_text, metadata_json, updated_at)
                VALUES (
                    @provider_id, @source_id, @feed_id, @item_id, @title, @creator_key, @author_name,
                    @author_url, @url, @published_at, @summary, @image_url, @author_image_url,
                    @duration_text, @metadata_json, @updated_at)
                ON CONFLICT (provider_id, source_id, feed_id, item_id) DO UPDATE SET
                    title = excluded.title,
                    creator_key = excluded.creator_key,
                    author_name = excluded.author_name,
                    author_url = excluded.author_url,
                    url = excluded.url,
                    published_at = excluded.published_at,
                    summary = excluded.summary,
                    image_url = excluded.image_url,
                    author_image_url = excluded.author_image_url,
                    duration_text = excluded.duration_text,
                    metadata_json = excluded.metadata_json,
                    updated_at = excluded.updated_at;
                """,
                cancellationToken,
                ("@provider_id", item.ProviderId),
                ("@source_id", item.SourceId),
                ("@feed_id", item.FeedId),
                ("@item_id", item.ItemId),
                ("@title", item.Title),
                ("@creator_key", item.CreatorKey),
                ("@author_name", item.AuthorName),
                ("@author_url", item.AuthorUrl),
                ("@url", item.Url),
                ("@published_at", ToStorageDateTime(item.PublishedAt)),
                ("@summary", item.Summary),
                ("@image_url", item.ImageUrl),
                ("@author_image_url", item.AuthorImageUrl),
                ("@duration_text", item.DurationText),
                ("@metadata_json", Serialize(item.Metadata)),
                ("@updated_at", seenAtText)).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO item_states (
                    provider_id, source_id, feed_id, item_id, is_read, read_at, is_saved)
                VALUES (
                    @provider_id, @source_id, @feed_id, @item_id, 0, NULL, 0)
                ON CONFLICT (provider_id, source_id, feed_id, item_id) DO NOTHING;
                """,
                cancellationToken,
                ("@provider_id", item.ProviderId),
                ("@source_id", item.SourceId),
                ("@feed_id", item.FeedId),
                ("@item_id", item.ItemId)).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO feed_item_subscriptions (
                    subscription_id, provider_id, source_id, feed_id, item_id, first_seen_at, last_seen_at)
                VALUES (
                    @subscription_id, @provider_id, @source_id, @feed_id, @item_id, @seen_at, @seen_at)
                ON CONFLICT (subscription_id, provider_id, source_id, feed_id, item_id) DO UPDATE SET
                    last_seen_at = excluded.last_seen_at;
                """,
                cancellationToken,
                ("@subscription_id", subscriptionId),
                ("@provider_id", item.ProviderId),
                ("@source_id", item.SourceId),
                ("@feed_id", item.FeedId),
                ("@item_id", item.ItemId),
                ("@seen_at", seenAtText)).ConfigureAwait(false);

            if (exists)
            {
                updated.Add(item);
            }
            else
            {
                inserted.Add(item);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FeedUpsertResult(inserted, updated);
    }

    public async Task MarkReadAsync(
        FeedItemKey key,
        bool isRead = true,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        var readAt = isRead ? ToStorageDateTime(DateTimeOffset.UtcNow) : null;

        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO item_states (
                provider_id, source_id, feed_id, item_id, is_read, read_at, is_saved)
            SELECT provider_id, source_id, feed_id, item_id, @is_read, @read_at, 0
            FROM feed_items
            WHERE provider_id = @provider_id
              AND source_id = @source_id
              AND feed_id = @feed_id
              AND item_id = @item_id
            ON CONFLICT (provider_id, source_id, feed_id, item_id) DO UPDATE SET
                is_read = excluded.is_read,
                read_at = excluded.read_at;
            """,
            cancellationToken,
            ("@provider_id", key.ProviderId),
            ("@source_id", key.SourceId),
            ("@feed_id", key.FeedId),
            ("@item_id", key.ItemId),
            ("@is_read", ToSqliteBoolean(isRead)),
            ("@read_at", readAt)).ConfigureAwait(false);
    }

    public async Task MarkSavedAsync(
        FeedItemKey key,
        bool isSaved = true,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO item_states (
                provider_id, source_id, feed_id, item_id, is_read, read_at, is_saved)
            SELECT provider_id, source_id, feed_id, item_id, 0, NULL, @is_saved
            FROM feed_items
            WHERE provider_id = @provider_id
              AND source_id = @source_id
              AND feed_id = @feed_id
              AND item_id = @item_id
            ON CONFLICT (provider_id, source_id, feed_id, item_id) DO UPDATE SET
                is_saved = excluded.is_saved;
            """,
            cancellationToken,
            ("@provider_id", key.ProviderId),
            ("@source_id", key.SourceId),
            ("@feed_id", key.FeedId),
            ("@item_id", key.ItemId),
            ("@is_saved", ToSqliteBoolean(isSaved))).ConfigureAwait(false);
    }

    public async Task SetSavedForFeedAsync(
        string providerId,
        string sourceId,
        string feedId,
        bool isSaved,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(providerId, nameof(providerId));
        ValidateIdentifier(sourceId, nameof(sourceId));
        ValidateIdentifier(feedId, nameof(feedId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            """
            UPDATE item_states
            SET is_saved = @is_saved
            WHERE provider_id = @provider_id
              AND source_id = @source_id
              AND feed_id = @feed_id;
            """,
            cancellationToken,
            ("@is_saved", ToSqliteBoolean(isSaved)),
            ("@provider_id", providerId),
            ("@source_id", sourceId),
            ("@feed_id", feedId)).ConfigureAwait(false);
    }

    public async Task SetSavedForItemAsync(
        string providerId,
        string sourceId,
        string itemId,
        bool isSaved,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(providerId, nameof(providerId));
        ValidateIdentifier(sourceId, nameof(sourceId));
        ValidateIdentifier(itemId, nameof(itemId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO item_states (
                provider_id, source_id, feed_id, item_id, is_read, read_at, is_saved)
            SELECT provider_id, source_id, feed_id, item_id, 0, NULL, @is_saved
            FROM feed_items
            WHERE provider_id = @provider_id
              AND source_id = @source_id
              AND item_id = @item_id
            ON CONFLICT (provider_id, source_id, feed_id, item_id) DO UPDATE SET
                is_saved = excluded.is_saved;
            """,
            cancellationToken,
            ("@is_saved", ToSqliteBoolean(isSaved)),
            ("@provider_id", providerId),
            ("@source_id", sourceId),
            ("@item_id", itemId)).ConfigureAwait(false);
    }

    public async Task DeleteSourceAsync(
        string providerId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            null,
            "DELETE FROM sources WHERE provider_id = @provider_id AND source_id = @source_id;",
            cancellationToken,
            ("@provider_id", providerId),
            ("@source_id", sourceId)).ConfigureAwait(false);
    }

    public async Task<FeedPollingState?> GetPollingStateAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(
            connection,
            null,
            """
            SELECT subscription_id, etag, last_modified, next_cursor, baseline_initialized,
                   last_attempt_at, last_success_at, failure_count, last_error
            FROM polling_state
            WHERE subscription_id = @subscription_id;
            """);
        AddParameter(command, "@subscription_id", subscriptionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPollingState(reader)
            : null;
    }

    public async Task SavePollingStateAsync(
        FeedPollingState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.SubscriptionId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO polling_state (
                subscription_id, etag, last_modified, next_cursor, baseline_initialized,
                last_attempt_at, last_success_at, failure_count, last_error)
            VALUES (
                @subscription_id, @etag, @last_modified, @next_cursor, @baseline_initialized,
                @last_attempt_at, @last_success_at, @failure_count, @last_error)
            ON CONFLICT (subscription_id) DO UPDATE SET
                etag = excluded.etag,
                last_modified = excluded.last_modified,
                next_cursor = excluded.next_cursor,
                baseline_initialized = excluded.baseline_initialized,
                last_attempt_at = excluded.last_attempt_at,
                last_success_at = excluded.last_success_at,
                failure_count = excluded.failure_count,
                last_error = excluded.last_error;
            """,
            cancellationToken,
            ("@subscription_id", state.SubscriptionId),
            ("@etag", state.ETag),
            ("@last_modified", ToNullableStorageDateTime(state.LastModified)),
            ("@next_cursor", state.NextCursor),
            ("@baseline_initialized", ToSqliteBoolean(state.BaselineInitialized)),
            ("@last_attempt_at", ToNullableStorageDateTime(state.LastAttemptAt)),
            ("@last_success_at", ToNullableStorageDateTime(state.LastSuccessAt)),
            ("@failure_count", Math.Max(0, state.FailureCount)),
            ("@last_error", state.LastError)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_memoryKeepAliveConnection is not null)
        {
            await _memoryKeepAliveConnection.DisposeAsync().ConfigureAwait(false);
        }

        _initializeGate.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var migrationTable = connection.CreateCommand())
        {
            migrationTable.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
            await migrationTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Serialize migration readers as well as writers across independent settings/monitor stores.
        using var transaction = connection.BeginTransaction();
        long currentVersion;
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            currentVersion = Convert.ToInt64(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (currentVersion > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The database schema version {currentVersion} is newer than the supported version {SchemaVersion}.");
        }

        if (currentVersion == 0)
        {
            await using var schema = CreateCommand(connection, transaction, SchemaV2Sql);
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // SQLite has no portable ALTER TABLE ... ADD COLUMN IF NOT EXISTS.
        // Inspecting first makes both a v1 upgrade and a repeated/partially
        // applied v2 migration safe and idempotent.
        if (!await HasColumnAsync(connection, transaction, "item_states", "is_saved", cancellationToken))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "ALTER TABLE item_states ADD COLUMN is_saved INTEGER NOT NULL DEFAULT 0 CHECK (is_saved IN (0, 1));",
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "CREATE INDEX IF NOT EXISTS ix_item_states_saved ON item_states (is_saved, provider_id, source_id);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, NotificationSchemaV3Sql, cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, CreatorNotificationSchemaV4Sql, cancellationToken)
            .ConfigureAwait(false);

        if (currentVersion < SchemaVersion)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (@version, @applied_at);",
                cancellationToken,
                ("@version", SchemaVersion),
                ("@applied_at", ToStorageDateTime(DateTimeOffset.UtcNow))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"PRAGMA table_info({tableName});");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string SchemaV2Sql =
        """
        CREATE TABLE IF NOT EXISTS sources (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            credential_profile_id TEXT,
            options_json TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (provider_id, source_id)
        );

        CREATE TABLE IF NOT EXISTS source_feeds (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            feed_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            supports_paging INTEGER NOT NULL CHECK (supports_paging IN (0, 1)),
            supports_notifications INTEGER NOT NULL CHECK (supports_notifications IN (0, 1)),
            PRIMARY KEY (provider_id, source_id, feed_id),
            FOREIGN KEY (provider_id, source_id)
                REFERENCES sources (provider_id, source_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS subscriptions (
            subscription_id TEXT PRIMARY KEY,
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            feed_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            endpoint TEXT,
            credential_profile_id TEXT,
            is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
            refresh_interval_ms INTEGER NOT NULL CHECK (refresh_interval_ms >= 0),
            options_json TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (provider_id, source_id)
                REFERENCES sources (provider_id, source_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS feed_items (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            feed_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            title TEXT NOT NULL,
            creator_key TEXT,
            author_name TEXT NOT NULL,
            author_url TEXT,
            url TEXT NOT NULL,
            published_at TEXT NOT NULL,
            summary TEXT,
            image_url TEXT,
            author_image_url TEXT,
            duration_text TEXT,
            metadata_json TEXT,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (provider_id, source_id, feed_id, item_id),
            FOREIGN KEY (provider_id, source_id)
                REFERENCES sources (provider_id, source_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS item_states (
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            feed_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            is_read INTEGER NOT NULL CHECK (is_read IN (0, 1)),
            read_at TEXT,
            is_saved INTEGER NOT NULL DEFAULT 0 CHECK (is_saved IN (0, 1)),
            PRIMARY KEY (provider_id, source_id, feed_id, item_id),
            FOREIGN KEY (provider_id, source_id, feed_id, item_id)
                REFERENCES feed_items (provider_id, source_id, feed_id, item_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS feed_item_subscriptions (
            subscription_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            feed_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            PRIMARY KEY (subscription_id, provider_id, source_id, feed_id, item_id),
            FOREIGN KEY (subscription_id)
                REFERENCES subscriptions (subscription_id)
                ON DELETE CASCADE,
            FOREIGN KEY (provider_id, source_id, feed_id, item_id)
                REFERENCES feed_items (provider_id, source_id, feed_id, item_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS polling_state (
            subscription_id TEXT PRIMARY KEY,
            etag TEXT,
            last_modified TEXT,
            next_cursor TEXT,
            baseline_initialized INTEGER NOT NULL CHECK (baseline_initialized IN (0, 1)),
            last_attempt_at TEXT,
            last_success_at TEXT,
            failure_count INTEGER NOT NULL CHECK (failure_count >= 0),
            last_error TEXT,
            FOREIGN KEY (subscription_id)
                REFERENCES subscriptions (subscription_id)
                ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_subscriptions_enabled
            ON subscriptions (is_enabled, updated_at);
        CREATE INDEX IF NOT EXISTS ix_feed_items_published
            ON feed_items (published_at DESC);
        CREATE INDEX IF NOT EXISTS ix_item_states_unread
            ON item_states (is_read, provider_id, source_id);
        CREATE INDEX IF NOT EXISTS ix_item_states_saved
            ON item_states (is_saved, provider_id, source_id);
        CREATE INDEX IF NOT EXISTS ix_feed_item_subscriptions_item
            ON feed_item_subscriptions (provider_id, source_id, feed_id, item_id);
        """;

    private static async Task<IReadOnlyList<FeedDefinition>> ReadFeedDefinitionsAsync(
        SqliteConnection connection,
        string providerId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            null,
            """
            SELECT feed_id, display_name, kind, supports_paging, supports_notifications
            FROM source_feeds
            WHERE provider_id = @provider_id AND source_id = @source_id
            ORDER BY feed_id;
            """);
        AddParameter(command, "@provider_id", providerId);
        AddParameter(command, "@source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var feeds = new List<FeedDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            feeds.Add(new FeedDefinition(
                reader.GetString(reader.GetOrdinal("feed_id")),
                reader.GetString(reader.GetOrdinal("display_name")),
                reader.GetString(reader.GetOrdinal("kind")),
                reader.GetInt64(reader.GetOrdinal("supports_paging")) != 0,
                reader.GetInt64(reader.GetOrdinal("supports_notifications")) != 0));
        }

        return feeds;
    }

    private static async Task<(string ProviderId, string SourceId, string FeedId)?> ReadSubscriptionIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT provider_id, source_id, feed_id FROM subscriptions WHERE subscription_id = @subscription_id;");
        AddParameter(command, "@subscription_id", subscriptionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task<bool> ItemExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedItemKey key,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT 1 FROM feed_items
                WHERE provider_id = @provider_id
                  AND source_id = @source_id
                  AND feed_id = @feed_id
                  AND item_id = @item_id);
            """);
        AddKeyParameters(command, key);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private sealed record SourceSummaryRow(
        string ProviderId,
        string SourceId,
        string DisplayName,
        string? CredentialProfileId,
        string? OptionsJson,
        int ItemCount,
        int UnreadCount);

    private static FeedSubscription ReadSubscription(SqliteDataReader reader)
    {
        return new FeedSubscription(
            reader.GetString(reader.GetOrdinal("subscription_id")),
            reader.GetString(reader.GetOrdinal("provider_id")),
            reader.GetString(reader.GetOrdinal("source_id")),
            reader.GetString(reader.GetOrdinal("feed_id")),
            reader.GetString(reader.GetOrdinal("display_name")),
            GetNullableString(reader, "endpoint"),
            GetNullableString(reader, "credential_profile_id"),
            reader.GetInt64(reader.GetOrdinal("is_enabled")) != 0,
            TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("refresh_interval_ms"))),
            DeserializeDictionary(GetNullableString(reader, "options_json")));
    }

    private static StoredFeedItem ReadStoredItem(SqliteDataReader reader)
    {
        var item = new FeedItem(
            reader.GetString(reader.GetOrdinal("provider_id")),
            reader.GetString(reader.GetOrdinal("source_id")),
            reader.GetString(reader.GetOrdinal("feed_id")),
            reader.GetString(reader.GetOrdinal("item_id")),
            reader.GetString(reader.GetOrdinal("title")),
            GetNullableString(reader, "creator_key"),
            reader.GetString(reader.GetOrdinal("author_name")),
            GetNullableString(reader, "author_url"),
            reader.GetString(reader.GetOrdinal("url")),
            FromStorageDateTime(reader.GetString(reader.GetOrdinal("published_at"))),
            GetNullableString(reader, "summary"),
            GetNullableString(reader, "image_url"),
            GetNullableString(reader, "author_image_url"),
            GetNullableString(reader, "duration_text"),
            DeserializeDictionary(GetNullableString(reader, "metadata_json")));

        return new StoredFeedItem(
            item,
            reader.GetString(reader.GetOrdinal("subscription_id")),
            reader.GetInt64(reader.GetOrdinal("is_read")) != 0,
            reader.GetInt64(reader.GetOrdinal("is_saved")) != 0,
            FromStorageDateTime(reader.GetString(reader.GetOrdinal("first_seen_at"))),
            FromStorageDateTime(reader.GetString(reader.GetOrdinal("last_seen_at"))));
    }

    private static FeedPollingState ReadPollingState(SqliteDataReader reader)
    {
        return new FeedPollingState(
            reader.GetString(reader.GetOrdinal("subscription_id")),
            GetNullableString(reader, "etag"),
            GetNullableDateTime(reader, "last_modified"),
            GetNullableString(reader, "next_cursor"),
            reader.GetInt64(reader.GetOrdinal("baseline_initialized")) != 0,
            GetNullableDateTime(reader, "last_attempt_at"),
            GetNullableDateTime(reader, "last_success_at"),
            reader.GetInt32(reader.GetOrdinal("failure_count")),
            GetNullableString(reader, "last_error"));
    }

    private static string? GetNullableString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? GetNullableDateTime(SqliteDataReader reader, string columnName)
    {
        var text = GetNullableString(reader, columnName);
        return text is null ? null : FromStorageDateTime(text);
    }

    private static IReadOnlyDictionary<string, string>? DeserializeDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Serialize<T>(T? value)
    {
        return value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static void AddKeyParameters(SqliteCommand command, FeedItemKey key)
    {
        AddParameter(command, "@provider_id", key.ProviderId);
        AddParameter(command, "@source_id", key.SourceId);
        AddParameter(command, "@feed_id", key.FeedId);
        AddParameter(command, "@item_id", key.ItemId);
    }

    private static int NormalizeLimit(int limit)
        => Math.Clamp(limit <= 0 ? DefaultItemLimit : limit, 1, MaxItemLimit);

    private static int ToSqliteBoolean(bool value) => value ? 1 : 0;

    private static string ToStorageDateTime(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? ToNullableStorageDateTime(DateTimeOffset? value)
        => value is null ? null : ToStorageDateTime(value.Value);

    private static DateTimeOffset FromStorageDateTime(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static bool LooksLikeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSourceAndSubscription(
        SourceDefinition source,
        FeedSubscription subscription)
    {
        ValidateIdentifier(source.ProviderId, nameof(source.ProviderId));
        ValidateIdentifier(source.SourceId, nameof(source.SourceId));
        ValidateIdentifier(subscription.SubscriptionId, nameof(subscription.SubscriptionId));
        ValidateIdentifier(subscription.FeedId, nameof(subscription.FeedId));
        if (!string.Equals(source.ProviderId, subscription.ProviderId, StringComparison.Ordinal)
            || !string.Equals(source.SourceId, subscription.SourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The subscription must target the supplied source.", nameof(subscription));
        }
    }

    private static void ValidateItemForSubscription(
        FeedItem item,
        (string ProviderId, string SourceId, string FeedId) subscription)
    {
        ValidateKey(item.Key);
        if (!string.Equals(item.ProviderId, subscription.ProviderId, StringComparison.Ordinal)
            || !string.Equals(item.SourceId, subscription.SourceId, StringComparison.Ordinal)
            || !string.Equals(item.FeedId, subscription.FeedId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The item does not belong to the target subscription.", nameof(item));
        }
    }

    private static bool SubscriptionIdentityMatches(
        (string ProviderId, string SourceId, string FeedId) existing,
        FeedSubscription requested)
    {
        return string.Equals(existing.ProviderId, requested.ProviderId, StringComparison.Ordinal)
            && string.Equals(existing.SourceId, requested.SourceId, StringComparison.Ordinal)
            && string.Equals(existing.FeedId, requested.FeedId, StringComparison.Ordinal);
    }

    private static void ValidateKey(FeedItemKey key)
    {
        ValidateIdentifier(key.ProviderId, nameof(key.ProviderId));
        ValidateIdentifier(key.SourceId, nameof(key.SourceId));
        ValidateIdentifier(key.FeedId, nameof(key.FeedId));
        ValidateIdentifier(key.ItemId, nameof(key.ItemId));
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The identifier cannot be empty.", name);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
