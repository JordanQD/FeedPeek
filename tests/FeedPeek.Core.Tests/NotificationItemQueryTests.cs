using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class NotificationItemQueryTests
{
    [Fact]
    public async Task ItemIdsRespectProviderSourceAndFeedIdentity()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var identities = new[]
        {
            ("provider-a", "source-a", "feed-a"),
            ("provider-a", "source-b", "feed-a"),
            ("provider-a", "source-b", "feed-b"),
            ("provider-b", "source-a", "feed-a"),
        };

        foreach (var (providerId, sourceId, feedId) in identities)
        {
            await store.SaveSourceAndSubscriptionAsync(
                CreateSource(providerId, sourceId, feedId),
                CreateSubscription(providerId, sourceId, feedId));
            await store.UpsertItemsAsync(
                $"{providerId}-{sourceId}-{feedId}",
                [CreateItem(providerId, sourceId, feedId, "same")],
                DateTimeOffset.UtcNow);
        }

        var all = await store.GetItemsAsync(new FeedQuery(
            Limit: 20,
            ItemIds: ["same"]));
        Assert.Equal(identities.Length, all.Count);

        var exact = await store.GetItemsAsync(new FeedQuery(
            ProviderId: "provider-a",
            SourceId: "source-b",
            FeedId: "feed-b",
            Limit: 20,
            ItemIds: ["same"]));
        var exactItem = Assert.Single(exact);
        Assert.Equal(("provider-a", "source-b", "feed-b"),
            (exactItem.Item.ProviderId, exactItem.Item.SourceId, exactItem.Item.FeedId));

        var otherFeed = await store.GetItemsAsync(new FeedQuery(
            ProviderId: "provider-a",
            SourceId: "source-b",
            FeedId: "feed-a",
            Limit: 20,
            ItemIds: ["same"]));
        Assert.Equal("feed-a", Assert.Single(otherFeed).Item.FeedId);
    }

    [Fact]
    public async Task ItemIdsQueryPreservesFirstSeenAtAcrossUpdates()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = CreateSource("provider", "source", "feed");
        var subscription = CreateSubscription("provider", "source", "feed");
        await store.SaveSourceAndSubscriptionAsync(source, subscription);

        var firstSeen = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var lastSeen = firstSeen.AddHours(1);
        var item = CreateItem("provider", "source", "feed", "same");
        await store.UpsertItemsAsync(subscription.SubscriptionId, [item], firstSeen);
        await store.UpsertItemsAsync(subscription.SubscriptionId, [item with { Title = "Updated" }], lastSeen);

        var stored = Assert.Single(await store.GetItemsAsync(new FeedQuery(
            ProviderId: "provider",
            SourceId: "source",
            FeedId: "feed",
            ItemIds: ["same"])));
        Assert.Equal(firstSeen, stored.FirstSeenAt);
        Assert.Equal(lastSeen, stored.LastSeenAt);
        Assert.Equal("Updated", stored.Item.Title);
    }

    [Fact]
    public async Task NullItemIdsKeepsExistingQueryAndEmptyItemIdsReturnsNothing()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = CreateSource("provider", "source", "feed");
        var subscription = CreateSubscription("provider", "source", "feed");
        await store.SaveSourceAndSubscriptionAsync(source, subscription);
        await store.UpsertItemsAsync(subscription.SubscriptionId,
            [CreateItem("provider", "source", "feed", "item")], DateTimeOffset.UtcNow);

        Assert.Single(await store.GetItemsAsync(new FeedQuery(
            ProviderId: "provider", SourceId: "source", FeedId: "feed")));
        Assert.Empty(await store.GetItemsAsync(new FeedQuery(
            ProviderId: "provider", SourceId: "source", FeedId: "feed",
            ItemIds: Array.Empty<string>())));
    }

    [Fact]
    public async Task ItemIdsCanReadAnOlderItemBeyondTheNormalFiveThousandLimit()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = CreateSource("provider", "source", "feed");
        var subscription = CreateSubscription("provider", "source", "feed");
        await store.SaveSourceAndSubscriptionAsync(source, subscription);

        var publishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var items = Enumerable.Range(0, 5001)
            .Select(index => CreateItem("provider", "source", "feed",
                $"history-{index:00000}", publishedAt.AddMinutes(index)))
            .ToArray();
        await store.UpsertItemsAsync(subscription.SubscriptionId, items, publishedAt);

        var old = await store.GetItemsAsync(new FeedQuery(
            ProviderId: "provider",
            SourceId: "source",
            FeedId: "feed",
            Limit: 1,
            ItemIds: ["history-00000"]));
        Assert.Equal("history-00000", Assert.Single(old).Item.ItemId);
    }

    private static SourceDefinition CreateSource(
        string providerId,
        string sourceId,
        string feedId)
        => new(providerId, sourceId, sourceId, null,
            [new FeedDefinition(feedId, feedId, "feed", true, true)], null);

    private static FeedSubscription CreateSubscription(
        string providerId,
        string sourceId,
        string feedId)
        => new($"{providerId}-{sourceId}-{feedId}", providerId, sourceId, feedId,
            sourceId, $"https://example.test/{providerId}/{sourceId}/{feedId}",
            null, true, TimeSpan.FromMinutes(15), null);

    private static FeedItem CreateItem(
        string providerId,
        string sourceId,
        string feedId,
        string itemId,
        DateTimeOffset? publishedAt = null)
        => new(providerId, sourceId, feedId, itemId, itemId,
            null, "Author", null, $"https://example.test/{itemId}",
            publishedAt ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            null, null, null, null, null);
}
