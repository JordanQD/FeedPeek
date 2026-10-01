using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class SourceConfigurationTests
{
    [Fact]
    public async Task AddressChangeKeepsIdentityRulesAndDisabledSwitchesButResetsQueryState()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = Source();
        var subscription = Subscription() with { IsEnabled = false };
        await store.SaveSourceAndSubscriptionAsync(source, subscription);
        var rule = new SourceNotificationRule(source.ProviderId, source.SourceId, ["updates"], [], false, false, true);
        await store.SaveSourceNotificationRuleAsync(rule);
        await store.SavePollingStateAsync(new FeedPollingState(subscription.SubscriptionId, "old-etag", null, "old-cursor", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        FeedPollSnapshotCache.Record(subscription, []);
        var updated = source with { DisplayName = "New room", Options = new Dictionary<string, string> { ["source.icon"] = "https://example.test/new.png" } };
        await store.UpdateSourceConfigurationAsync(updated,
            [subscription with { Endpoint = "https://example.test/new", Options = updated.Options, DisplayName = "New room" }]);

        var stored = Assert.Single(await store.GetSourceSummariesAsync());
        Assert.Equal(source.SourceId, stored.Source.SourceId);
        Assert.Equal("New room", stored.Source.DisplayName);
        Assert.Equal("https://example.test/new.png", stored.Source.Options!["source.icon"]);
        Assert.Equal(rule, Assert.Single(await store.GetSourceNotificationRulesAsync()) with
        {
            UpdateFeedIds = rule.UpdateFeedIds, LiveFeedIds = rule.LiveFeedIds,
        });
        Assert.Null(await store.GetPollingStateAsync(subscription.SubscriptionId));
        var saved = await store.GetSubscriptionAsync(subscription.SubscriptionId);
        Assert.False(saved!.IsEnabled);
        Assert.Equal(subscription.RefreshInterval, saved.RefreshInterval);
        Assert.Equal("https://example.test/new", saved.Endpoint);
    }

    [Fact]
    public async Task FailedBatchRollsBackAllChangesAndDoesNotReviveDeletedSubscriptions()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = Source();
        var subscription = Subscription();
        await store.SaveSourceAndSubscriptionAsync(source, subscription);
        var updated = source with { DisplayName = "New" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateSourceConfigurationAsync(updated,
            [subscription with { Endpoint = "https://example.test/new" }, subscription with { SubscriptionId = "missing" }]));
        Assert.Equal(source.DisplayName, Assert.Single(await store.GetSourceSummariesAsync()).Source.DisplayName);
        Assert.Equal(subscription.Endpoint, (await store.GetSubscriptionAsync(subscription.SubscriptionId))!.Endpoint);

        await store.DeleteSourceAsync(source.ProviderId, source.SourceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateSourceConfigurationAsync(updated, [subscription]));
        Assert.Empty(await store.GetSourceSummariesAsync());
    }

    [Fact]
    public async Task PollWithOldSnapshotUsesCommittedNewAddressAndNoOldEtag()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = Source();
        var old = Subscription();
        await store.SaveSourceAndSubscriptionAsync(source, old);
        await store.SavePollingStateAsync(new FeedPollingState(old.SubscriptionId, "old-etag", null, null, true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        await store.UpdateSourceConfigurationAsync(source, [old with { Endpoint = "https://example.test/new" }]);
        var provider = new RequestProbe();
        using var registry = new FeedProviderRegistry([provider]);
        var poll = await new FeedPollingService(store, registry).PollAsync(old);
        Assert.Equal("https://example.test/new", provider.Request!.Subscription.Endpoint);
        Assert.Null(provider.Request.ETag);
        Assert.True(poll.InitializedBaseline);
    }

    [Fact]
    public async Task AddressCommitWaitsForOldPollSoItsEtagCannotOverwriteTheReset()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = Source() with { SourceId = "concurrent-source" };
        var old = Subscription() with { SourceId = source.SourceId, SubscriptionId = "concurrent-sub" };
        await store.SaveSourceAndSubscriptionAsync(source, old);
        var provider = new RequestProbe(blockFirst: true);
        using var registry = new FeedProviderRegistry([provider]);
        var polling = new FeedPollingService(store, registry);
        var inFlight = polling.PollAsync(old);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var update = store.UpdateSourceConfigurationAsync(source, [old with { Endpoint = "https://example.test/new" }]);
        Assert.False(update.IsCompleted);
        provider.Release.TrySetResult(true);
        await inFlight;
        await update;
        Assert.Null(await store.GetPollingStateAsync(old.SubscriptionId));
        await polling.PollAsync(old);
        Assert.Equal("https://example.test/new", provider.Request!.Subscription.Endpoint);
        Assert.Null(provider.Request.ETag);
    }

    private static SourceDefinition Source() => new("configuration.test", "source", "Old", null,
        [new FeedDefinition("updates", "Updates", "rss", false, true)], null);
    private static FeedSubscription Subscription() => new("configuration-test-sub", "configuration.test", "source", "updates", "Old",
        "https://example.test/old", null, true, TimeSpan.FromMinutes(15), null);
    private sealed class RequestProbe(bool blockFirst = false) : IFeedProvider
    {
        public string ProviderId => "configuration.test";
        public FeedPageRequest? Request { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async Task<FeedPage> GetPageAsync(FeedPageRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (++_calls == 1 && blockFirst)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new FeedPage([], null, false, "etag");
        }
    }
}
