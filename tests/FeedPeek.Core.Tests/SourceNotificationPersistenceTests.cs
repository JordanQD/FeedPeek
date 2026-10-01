using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace FeedPeek.Core.Tests;

public sealed class SourceNotificationPersistenceTests
{
    [Fact]
    public async Task BackfillPreservesDisabledSwitchesOrderAndManualRulesAcrossSessions()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using (var store = OpenStore(database))
        {
            await AddSourceAsync(store, "a");
            await AddSourceAsync(store, "b");
            Assert.Equal(1, await store.SynchronizeSourceNotificationRulesAsync([Rule("a")]));
            await store.SaveSourceNotificationRuleAsync(Rule("a") with { UpdateNotificationsEnabled = false });
            await store.SaveSourceNotificationRuleAsync(Rule("b") with { IsAutomatic = false, LiveNotificationsEnabled = false });
        }
        await using var reopened = OpenStore(database);
        Assert.Equal(0, await reopened.SynchronizeSourceNotificationRulesAsync([Rule("b"), Rule("a"), Rule("a")]));
        var rules = await reopened.GetSourceNotificationRulesAsync();
        Assert.Equal(["a", "b"], rules.Select(rule => rule.SourceId));
        Assert.False(rules[0].UpdateNotificationsEnabled);
        Assert.True(rules[0].LiveNotificationsEnabled);
        Assert.False(rules[1].IsAutomatic);
        Assert.False(rules[1].LiveNotificationsEnabled);
        Assert.Equal(["news"], rules[0].UpdateFeedIds);
        Assert.Equal(["rooms"], rules[0].LiveFeedIds);
    }

    [Fact]
    public async Task DeletedNotificationStaysSuppressedUntilExplicitManualRestore()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using (var store = OpenStore(database))
        {
            await AddSourceAsync(store, "a");
            await store.SynchronizeSourceNotificationRulesAsync([Rule("a")]);
            await store.DeleteSourceNotificationRuleAsync(Rule("a").Key);
        }
        await using var reopened = OpenStore(database);
        Assert.Equal(0, await reopened.SynchronizeSourceNotificationRulesAsync([Rule("a")]));
        Assert.Empty(await reopened.GetSourceNotificationRulesAsync());
        await reopened.SaveSourceNotificationRuleAsync(Rule("a") with { IsAutomatic = false, UpdateNotificationsEnabled = false });
        Assert.Equal(0, await reopened.SynchronizeSourceNotificationRulesAsync([Rule("a")]));
        var restored = Assert.Single(await reopened.GetSourceNotificationRulesAsync());
        Assert.False(restored.IsAutomatic);
        Assert.False(restored.UpdateNotificationsEnabled);
        await using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_notification_suppressions;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeletingSourceCascadesRulesAndSuppressionsWithoutAffectingOtherSources()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "rule");
        await AddSourceAsync(store, "suppressed");
        await AddSourceAsync(store, "other");
        await store.SynchronizeSourceNotificationRulesAsync([Rule("rule"), Rule("suppressed"), Rule("other")]);
        await store.DeleteSourceNotificationRuleAsync(Rule("suppressed").Key);
        await store.DeleteSourceAsync("demo.plugin", "rule");
        await store.DeleteSourceAsync("demo.plugin", "suppressed");
        Assert.Equal("other", Assert.Single(await store.GetSourceNotificationRulesAsync()).SourceId);
        // RSS IDs can be deterministic. Deleting and re-adding even the same identity removes the old opt-out.
        await AddSourceAsync(store, "rule");
        await AddSourceAsync(store, "suppressed");
        Assert.Equal(2, await store.SynchronizeSourceNotificationRulesAsync([Rule("rule"), Rule("suppressed")]));
        Assert.Equal(3, (await store.GetSourceNotificationRulesAsync()).Count);
    }

    [Fact]
    public async Task AutomaticSyncSkipsManualCandidatesAndSourcesDeletedAfterSnapshot()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "a");
        Assert.Equal(0, await store.SynchronizeSourceNotificationRulesAsync([
            Rule("a") with { IsAutomatic = false }, Rule("deleted")
        ]));
        Assert.Empty(await store.GetSourceNotificationRulesAsync());
    }

    [Fact]
    public async Task FailedExplicitSaveCannotCreateMissingSourceOrDisturbOtherSuppressions()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "a");
        await store.DeleteSourceNotificationRuleAsync(Rule("a").Key);
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveSourceNotificationRuleAsync(Rule("missing")));
        Assert.Equal(0, await store.SynchronizeSourceNotificationRulesAsync([Rule("a")]));
    }

    [Fact]
    public async Task CapabilityRepairOnlyChangesCompatibilityLiveFeedsAndPreservesSavedSwitches()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "compat", supportsLive: false);
        await AddSourceAsync(store, "native", supportsLive: false, providerId: "native.plugin");
        await store.SaveSourceNotificationRuleAsync(Rule("compat") with { LiveNotificationsEnabled = false });
        await store.RepairLiveFeedNotificationCapabilitiesAsync(["demo.plugin"]);
        await store.RepairLiveFeedNotificationCapabilitiesAsync(["demo.plugin"]);
        var sources = await store.GetSourceSummariesAsync();
        var compat = sources.Single(item => item.Source.SourceId == "compat").Source;
        var native = sources.Single(item => item.Source.SourceId == "native").Source;
        Assert.True(compat.Feeds.Single(feed => feed.Kind == "live").SupportsNotifications);
        Assert.False(compat.Feeds.Single(feed => feed.FeedId == "history").SupportsNotifications);
        Assert.False(native.Feeds.Single(feed => feed.Kind == "live").SupportsNotifications);
        var subscriptions = await store.GetSubscriptionsAsync();
        Assert.True(SourceNotificationCapabilities.Resolve(compat, subscriptions).SupportsLive);
        await store.SynchronizeSourceNotificationRulesAsync([Rule("compat")]);
        Assert.False(Assert.Single(await store.GetSourceNotificationRulesAsync()).LiveNotificationsEnabled);
    }

    [Fact]
    public async Task VersionTwoDatabaseUpgradesIdempotentlyAndKeepsExistingSourceData()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using (var original = OpenStore(database))
            await AddSourceAsync(original, "existing");
        await RecreateVersionTwoAsync(database);
        await using (var upgraded = OpenStore(database))
        {
            await upgraded.InitializeAsync();
            Assert.Equal("existing", Assert.Single(await upgraded.GetSourceSummariesAsync()).Source.SourceId);
            Assert.Equal(1, await upgraded.SynchronizeSourceNotificationRulesAsync([Rule("existing")]));
        }
        await using var reopened = OpenStore(database);
        Assert.Equal(0, await reopened.SynchronizeSourceNotificationRulesAsync([Rule("existing")]));
        Assert.Single(await reopened.GetSourceNotificationRulesAsync());
    }

    [Fact]
    public async Task SettingsAndMonitorCanUpgradeTheSameOldDatabaseConcurrently()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using (var original = OpenStore(database))
            await AddSourceAsync(original, "existing");
        await RecreateVersionTwoAsync(database);
        await using var settings = OpenStore(database);
        await using var monitor = OpenStore(database);
        await Task.WhenAll(Task.Run(() => settings.InitializeAsync()), Task.Run(() => monitor.InitializeAsync()));
        Assert.Equal(1, await settings.SynchronizeSourceNotificationRulesAsync([Rule("existing")]));
        Assert.Single(await monitor.GetSourceNotificationRulesAsync());
        Assert.Equal("existing", Assert.Single(await monitor.GetSourceSummariesAsync()).Source.SourceId);
    }

    [Fact]
    public async Task ConcurrentStoresCannotDuplicateOrOverwriteAUserChoice()
    {
        using var directory = TemporaryDirectory.Create();
        var database = Path.Combine(directory.Path, "feeds.db");
        await using var first = OpenStore(database);
        await using var second = OpenStore(database);
        await AddSourceAsync(first, "a");
        await second.InitializeAsync();
        var inserted = await Task.WhenAll(
            Task.Run(() => first.SynchronizeSourceNotificationRulesAsync([Rule("a")])),
            Task.Run(() => second.SynchronizeSourceNotificationRulesAsync([Rule("a")])));
        Assert.Equal(1, inserted.Sum());
        await second.SaveSourceNotificationRuleAsync(Rule("a") with { LiveNotificationsEnabled = false });
        Assert.Equal(0, await first.SynchronizeSourceNotificationRulesAsync([Rule("a")]));
        Assert.False(Assert.Single(await first.GetSourceNotificationRulesAsync()).LiveNotificationsEnabled);
    }

    private static SourceNotificationRule Rule(string sourceId)
        => new("demo.plugin", sourceId, ["news"], ["rooms"], true, true, IsAutomatic: true);

    private static SqliteFeedStore OpenStore(string database)
        => new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());

    private static async Task RecreateVersionTwoAsync(string database)
    {
        await using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE source_notification_rules;
            DROP TABLE source_notification_suppressions;
            DELETE FROM schema_migrations;
            INSERT INTO schema_migrations VALUES (2, '2026-09-30T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FeedPeek.Notification.Tests"));
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test directory escaped its temporary root.");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task AddSourceAsync(IFeedStore store, string sourceId,
        bool supportsLive = true, string providerId = "demo.plugin")
    {
        var source = new SourceDefinition(providerId, sourceId, "Display " + sourceId, null, [
            new("news", "News", "feed", false, true),
            new("rooms", "Rooms", "live", false, supportsLive),
            new("history", "History", "feed", false, false),
        ], null);
        foreach (var feed in source.Feeds)
            await store.SaveSourceAndSubscriptionAsync(source, new(providerId + ":" + sourceId + ":" + feed.FeedId,
                providerId, sourceId, feed.FeedId, source.DisplayName, null, null, true, TimeSpan.FromMinutes(15), null));
    }
}
