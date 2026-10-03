using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class FeedConfigurationSnapshotTests
{
    [Fact]
    public async Task ExportImportRoundTripsNotificationsAndPreservesLocalHistory()
    {
        FeedConfigurationSnapshot snapshot;
        await using (var sourceStore = new SqliteFeedStore("Data Source=:memory:"))
        {
            await AddSourceAsync(sourceStore, "plugin.rule", "rule", "portable-source", "portable-sub");
            await AddSourceAsync(sourceStore, "plugin.policy", "policy", null, null);
            await AddSourceAsync(sourceStore, "builtin.rss", "suppressed", "ignored", "ignored-sub");
            await sourceStore.SaveSourceNotificationRuleAsync(new(
                "plugin.rule", "rule", ["updates"], [], true, false, true));
            await sourceStore.SaveCreatorNotificationPolicyAsync(new(
                "plugin.policy", "policy", CreatorNotificationScope.AllFollowing,
                CreatorNotificationScope.SelectedCreators, false, true));
            await sourceStore.DeleteSourceNotificationRuleAsync(new("builtin.rss", "suppressed"));

            snapshot = await sourceStore.ExportConfigurationAsync();
        }

        Assert.All(snapshot.Sources, source => Assert.Null(source.CredentialProfileId));
        Assert.All(snapshot.Subscriptions, subscription => Assert.Null(subscription.CredentialProfileId));

        await using var destination = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(destination, "plugin.rule", "rule", "local-source", "local-sub");
        await AddSourceAsync(destination, "keep.plugin", "keep", "keep-source", "keep-sub");
        await destination.UpsertItemsAsync("keep.plugin:keep:updates", [Item("keep.plugin", "keep")], DateTimeOffset.UtcNow);

        await destination.ImportConfigurationAsync(snapshot);

        var sources = await destination.GetSourceSummariesAsync();
        Assert.Equal(4, sources.Count);
        var restoredSource = sources.Single(item => item.Source.ProviderId == "plugin.rule").Source;
        Assert.Equal("local-source", restoredSource.CredentialProfileId);
        Assert.Equal("Portable rule", restoredSource.DisplayName);
        Assert.Equal("portable", restoredSource.Options!["mode"]);
        Assert.Equal("local-sub", (await destination.GetSubscriptionAsync("plugin.rule:rule:updates"))!.CredentialProfileId);
        Assert.Equal("default", sources.Single(item => item.Source.SourceId == "policy").Source.CredentialProfileId);
        Assert.Null(sources.Single(item => item.Source.SourceId == "suppressed").Source.CredentialProfileId);

        var rules = await destination.GetSourceNotificationRulesAsync();
        var restoredNotificationRule = Assert.Single(rules);
        Assert.Equal("plugin.rule", restoredNotificationRule.ProviderId);
        Assert.Equal("rule", restoredNotificationRule.SourceId);
        Assert.Equal(["updates"], restoredNotificationRule.UpdateFeedIds);
        Assert.Empty(restoredNotificationRule.LiveFeedIds);
        Assert.True(restoredNotificationRule.UpdateNotificationsEnabled);
        Assert.False(restoredNotificationRule.LiveNotificationsEnabled);
        Assert.True(restoredNotificationRule.IsAutomatic);
        Assert.Equal(new CreatorNotificationPolicy("plugin.policy", "policy",
            CreatorNotificationScope.AllFollowing, CreatorNotificationScope.SelectedCreators, false, true),
            Assert.Single(await destination.GetCreatorNotificationPoliciesAsync()));
        Assert.Equal(0, await destination.SynchronizeSourceNotificationRulesAsync([
            new SourceNotificationRule("builtin.rss", "suppressed", ["updates"], [], true, true, true)]));
        Assert.Single(await destination.GetItemsAsync(new FeedQuery(ProviderId: "keep.plugin", SourceId: "keep")));
    }

    [Fact]
    public async Task InvalidSnapshotDoesNotWriteAnything()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "keep.plugin", "keep", "local", "keep-sub");
        var invalid = new FeedConfigurationSnapshot(
            [Source("new.plugin", "new")],
            [Subscription("new.plugin", "new", "missing-feed")],
            [], [], []);

        Assert.Throws<ArgumentException>(() => SqliteFeedStore.ValidateConfiguration(invalid));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportConfigurationAsync(invalid));
        Assert.Single(await store.GetSourceSummariesAsync());
        Assert.Equal("keep.plugin", Assert.Single(await store.GetSubscriptionsAsync()).ProviderId);
    }

    [Fact]
    public async Task SubscriptionIdentityCollisionFailsBeforeAnySourceWrite()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        await AddSourceAsync(store, "existing.plugin", "existing", "local", "collision");
        var snapshot = new FeedConfigurationSnapshot(
            [Source("new.plugin", "new")],
            [new("existing.plugin:existing:updates", "new.plugin", "new", "updates", "New", null, null, true,
                TimeSpan.FromMinutes(5), null)],
            [], [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportConfigurationAsync(snapshot));
        Assert.Single(await store.GetSourceSummariesAsync());
        var subscription = Assert.Single(await store.GetSubscriptionsAsync());
        Assert.Equal("existing.plugin", subscription.ProviderId);
        Assert.Equal("existing", subscription.SourceId);
    }

    [Fact]
    public async Task BeforeCommitCallbackRunsAfterSqlAndRollsBackWhenItFails()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var snapshot = new FeedConfigurationSnapshot([Source("new.plugin", "new")], [], [], [], []);
        var callbackCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportConfigurationAsync(snapshot, () =>
        {
            callbackCalled = true;
            throw new InvalidOperationException("settings write failed");
        }));

        Assert.True(callbackCalled);
        Assert.Empty(await store.GetSourceSummariesAsync());
    }

    [Fact]
    public void ValidateRejectsDuplicateSubscriptionTupleAndMixedNotificationOwners()
    {
        var source = Source("demo.plugin", "source");
        var first = new FeedSubscription("first", "demo.plugin", "source", "updates", "Updates",
            null, null, true, TimeSpan.FromMinutes(5), null);
        var second = first with { SubscriptionId = "second" };
        Assert.Throws<ArgumentException>(() => SqliteFeedStore.ValidateConfiguration(
            new FeedConfigurationSnapshot([source], [first, second], [], [], [])));

        var rule = new SourceNotificationRule("demo.plugin", "source", ["updates"], [], true, true);
        var policy = new CreatorNotificationPolicy("demo.plugin", "source",
            CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.AllFollowing, true, true);
        Assert.Throws<ArgumentException>(() => SqliteFeedStore.ValidateConfiguration(
            new FeedConfigurationSnapshot([source], [first], [rule], [policy], [])));
        Assert.Throws<ArgumentException>(() => SqliteFeedStore.ValidateConfiguration(
            new FeedConfigurationSnapshot([source], [first], [rule], [], [rule.Key])));
    }

    [Fact]
    public async Task ImportReusesLocalTupleAndDisablesExtraSubscriptionsWhileKeepingHistory()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var localSource = new SourceDefinition("demo.plugin", "source", "Local", "local-source", [
            new FeedDefinition("updates", "Updates", "feed", true, true),
            new FeedDefinition("extra", "Extra", "feed", true, true),
        ], null);
        await store.SaveSourceAndSubscriptionAsync(localSource, new(
            "local-id", "demo.plugin", "source", "updates", "Local", null,
            "local-sub", true, TimeSpan.FromMinutes(15), null));
        await store.SaveSourceAndSubscriptionAsync(localSource, new(
            "extra-id", "demo.plugin", "source", "extra", "Extra", null,
            "extra-sub", true, TimeSpan.FromMinutes(15), null));
        await store.UpsertItemsAsync("extra-id", [Item("demo.plugin", "source") with { FeedId = "extra" }], DateTimeOffset.UtcNow);
        await store.SavePollingStateAsync(new FeedPollingState("local-id", "etag", null, "cursor", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        await store.SavePollingStateAsync(new FeedPollingState("extra-id", "etag", null, "cursor", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));

        var snapshot = new FeedConfigurationSnapshot(
            [Source("demo.plugin", "source")],
            [new("portable-id", "demo.plugin", "source", "updates", "Imported", null, null, true,
                TimeSpan.FromMinutes(5), null)],
            [], [], []);
        await store.ImportConfigurationAsync(snapshot);

        var subscriptions = await store.GetSubscriptionsAsync();
        Assert.Equal(2, subscriptions.Count);
        var reused = Assert.Single(subscriptions, item => item.SubscriptionId == "local-id");
        Assert.True(reused.IsEnabled);
        Assert.Equal("local-sub", reused.CredentialProfileId);
        var extra = Assert.Single(subscriptions, item => item.SubscriptionId == "extra-id");
        Assert.False(extra.IsEnabled);
        Assert.Null(await store.GetPollingStateAsync("local-id"));
        Assert.Null(await store.GetPollingStateAsync("extra-id"));
        Assert.Single(await store.GetItemsAsync(new FeedQuery(ProviderId: "demo.plugin", SourceId: "source", FeedId: "extra")));
        Assert.Null(await store.GetSubscriptionAsync("portable-id"));
    }

    private static async Task AddSourceAsync(
        SqliteFeedStore store,
        string providerId,
        string sourceId,
        string? sourceCredential,
        string? subscriptionCredential)
    {
        var source = Source(providerId, sourceId) with
        {
            CredentialProfileId = sourceCredential,
            Options = new Dictionary<string, string> { ["mode"] = "portable" },
        };
        var subscription = Subscription(providerId, sourceId, "updates") with
        {
            CredentialProfileId = subscriptionCredential,
        };
        await store.SaveSourceAndSubscriptionAsync(source, subscription);
    }

    private static SourceDefinition Source(string providerId, string sourceId)
        => new(providerId, sourceId, "Portable " + sourceId, null,
            [new FeedDefinition("updates", "Updates", "feed", true, true)], null);

    private static FeedSubscription Subscription(string providerId, string sourceId, string feedId)
        => new(providerId + ":" + sourceId + ":" + feedId, providerId, sourceId, feedId,
            "Portable " + sourceId, "https://example.test/" + sourceId, null, true,
            TimeSpan.FromMinutes(15), null);

    private static FeedItem Item(string providerId, string sourceId)
        => new(providerId, sourceId, "updates", "item-1", "Item", "creator", "Author", null,
            "https://example.test/item-1", DateTimeOffset.UtcNow, null, null, null, null, null);
}
