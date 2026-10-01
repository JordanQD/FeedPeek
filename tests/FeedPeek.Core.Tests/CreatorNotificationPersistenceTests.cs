using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace FeedPeek.Core.Tests;

public sealed class CreatorNotificationPersistenceTests
{
    [Fact]
    public async Task VersionThreeUpgradePreservesOldRulesAndMigrationDoesNotReopenEditedGates()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using (var original = OpenStore(database))
        {
            await AddSourceAsync(original, "creator");
            await original.SaveSourceNotificationRuleAsync(SourceRule("creator") with { LiveNotificationsEnabled = false });
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE creator_notification_policies; DELETE FROM schema_migrations WHERE version > 3; "
                + "INSERT OR IGNORE INTO schema_migrations (version, applied_at) VALUES (3, '2026-10-01T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }
        var candidate = Policy("creator", CreatorNotificationScope.AllFollowing,
            CreatorNotificationScope.AllFollowing, UpdatesEnabled: true, LiveEnabled: false);
        var edited = candidate with { UpdatesEnabled = false, LiveScope = CreatorNotificationScope.SelectedCreators };
        await using (var upgraded = OpenStore(database))
        {
            Assert.Empty(await upgraded.GetCreatorNotificationPoliciesAsync());
            Assert.False(Assert.Single(await upgraded.GetSourceNotificationRulesAsync()).LiveNotificationsEnabled);
            Assert.Equal(2, await upgraded.MigrateCreatorNotificationPoliciesAsync([candidate]));
            await upgraded.SaveCreatorNotificationPolicyAsync(edited);
            Assert.Equal(0, await upgraded.MigrateCreatorNotificationPoliciesAsync([candidate]));
            Assert.Equal(edited, Assert.Single(await upgraded.GetCreatorNotificationPoliciesAsync()));
        }
        await using var reopened = OpenStore(database);
        Assert.Equal(edited, Assert.Single(await reopened.GetCreatorNotificationPoliciesAsync()));
        Assert.Empty(await reopened.GetSourceNotificationRulesAsync());
    }

    [Fact]
    public async Task EnsureIsIdempotentAndPreservesDisabledSwitchesAndScopes()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "creators");
        var saved = Policy("creators", CreatorNotificationScope.AllFollowing,
            CreatorNotificationScope.SelectedCreators, UpdatesEnabled: false, LiveEnabled: true);

        await store.EnsureCreatorNotificationPolicyAsync(saved);
        await store.EnsureCreatorNotificationPolicyAsync(saved with
        {
            UpdatesScope = CreatorNotificationScope.SelectedCreators,
            LiveScope = CreatorNotificationScope.AllFollowing,
            UpdatesEnabled = true,
            LiveEnabled = false,
        });

        var actual = Assert.Single(await store.GetCreatorNotificationPoliciesAsync());
        Assert.Equal(saved, actual);
    }

    [Fact]
    public async Task ConcurrentStoresCreateOnlyOnePolicy()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using var first = OpenStore(database);
        await using var second = OpenStore(database);
        await AddSourceAsync(first, "creators");
        var policy = Policy("creators");

        await Task.WhenAll(
            first.EnsureCreatorNotificationPolicyAsync(policy),
            second.EnsureCreatorNotificationPolicyAsync(policy));

        Assert.Single(await first.GetCreatorNotificationPoliciesAsync());
        Assert.Single(await second.GetCreatorNotificationPoliciesAsync());
    }

    [Fact]
    public async Task ExplicitSaveClearsSuppressionButAutomaticSyncCannotDuplicatePolicy()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using var store = OpenStore(database);
        await AddSourceAsync(store, "creators");
        await store.DeleteSourceNotificationRuleAsync(new("demo.plugin", "creators"));

        await store.SaveCreatorNotificationPolicyAsync(Policy("creators"));

        Assert.Single(await store.GetCreatorNotificationPoliciesAsync());
        Assert.Equal(0, await store.SynchronizeSourceNotificationRulesAsync([SourceRule("creators")]));
        Assert.Empty(await store.GetSourceNotificationRulesAsync());
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_notification_suppressions;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeletingPolicyRemovesOldRuleAndDoesNotAllowAutomaticRecreation()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "creators");
        await store.SaveSourceNotificationRuleAsync(SourceRule("creators"));
        await store.SaveCreatorNotificationPolicyAsync(Policy("creators"));

        await store.DeleteCreatorNotificationPolicyAsync(new("demo.plugin", "creators"));

        Assert.Empty(await store.GetCreatorNotificationPoliciesAsync());
        Assert.Empty(await store.GetSourceNotificationRulesAsync());
        Assert.Equal(0, await store.SynchronizeSourceNotificationRulesAsync([SourceRule("creators")]));
        Assert.Empty(await store.GetSourceNotificationRulesAsync());
    }

    [Fact]
    public async Task MigrationCreatesPolicyAndRetiresOnlyEligibleOldRule()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "creator");
        await AddSourceAsync(store, "other", providerId: "rss");
        await store.SaveSourceNotificationRuleAsync(SourceRule("creator"));
        await store.SaveSourceNotificationRuleAsync(SourceRule("other", "rss"));

        var changed = await store.MigrateCreatorNotificationPoliciesAsync([Policy("creator")]);

        Assert.Equal(2, changed);
        Assert.Single(await store.GetCreatorNotificationPoliciesAsync());
        var remaining = Assert.Single(await store.GetSourceNotificationRulesAsync());
        Assert.Equal("rss", remaining.ProviderId);
        Assert.Equal("other", remaining.SourceId);
    }

    [Fact]
    public async Task MigrationRespectsSuppressionAndLeavesOldRuleWhenPolicyWasNotCreated()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using var store = OpenStore(database);
        await AddSourceAsync(store, "creator");
        await store.SaveSourceNotificationRuleAsync(SourceRule("creator"));
        await InsertSuppressionAsync(database, "demo.plugin", "creator");

        var changed = await store.MigrateCreatorNotificationPoliciesAsync([Policy("creator")]);

        Assert.Equal(0, changed);
        Assert.Empty(await store.GetCreatorNotificationPoliciesAsync());
        Assert.Single(await store.GetSourceNotificationRulesAsync());
    }

    [Fact]
    public async Task DeletingSourceCascadesPolicyAndSuppression()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "creator");
        await store.SaveCreatorNotificationPolicyAsync(Policy("creator"));
        await store.DeleteCreatorNotificationPolicyAsync(new("demo.plugin", "creator"));
        await store.DeleteSourceAsync("demo.plugin", "creator");

        Assert.Empty(await store.GetCreatorNotificationPoliciesAsync());

        await AddSourceAsync(store, "creator");
        Assert.Equal(1, await store.MigrateCreatorNotificationPoliciesAsync([Policy("creator")]));
        Assert.Single(await store.GetCreatorNotificationPoliciesAsync());
    }

    [Fact]
    public async Task InvalidIdentifiersAndScopesAreRejected()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "creator");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.EnsureCreatorNotificationPolicyAsync(Policy("creator") with { ProviderId = "" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveCreatorNotificationPolicyAsync(Policy("creator") with { UpdatesScope = (CreatorNotificationScope)99 }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteCreatorNotificationPolicyAsync(new("demo.plugin", "")));
    }

    private static CreatorNotificationPolicy Policy(
        string sourceId,
        CreatorNotificationScope updatesScope = CreatorNotificationScope.SelectedCreators,
        CreatorNotificationScope liveScope = CreatorNotificationScope.SelectedCreators,
        bool UpdatesEnabled = true,
        bool LiveEnabled = true,
        string providerId = "demo.plugin")
        => new(providerId, sourceId, updatesScope, liveScope, UpdatesEnabled, LiveEnabled);

    private static SourceNotificationRule SourceRule(string sourceId, string providerId = "demo.plugin")
        => new(providerId, sourceId, ["news"], ["rooms"], true, true, IsAutomatic: true);

    private static SqliteFeedStore OpenStore(string database)
        => new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());

    private static async Task AddSourceAsync(
        IFeedStore store,
        string sourceId,
        string providerId = "demo.plugin")
    {
        var source = new SourceDefinition(providerId, sourceId, "Display " + sourceId, null, [
            new("news", "News", "feed", false, true),
            new("rooms", "Rooms", "live", false, true),
        ], null);
        foreach (var feed in source.Feeds)
        {
            await store.SaveSourceAndSubscriptionAsync(source, new(
                providerId + ":" + sourceId + ":" + feed.FeedId,
                providerId,
                sourceId,
                feed.FeedId,
                source.DisplayName,
                null,
                null,
                true,
                TimeSpan.FromMinutes(15),
                null));
        }
    }

    private static async Task InsertSuppressionAsync(string database, string providerId, string sourceId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO source_notification_suppressions (provider_id, source_id) VALUES ($provider, $source);";
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$source", sourceId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "FeedPeek.CreatorPolicy.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
