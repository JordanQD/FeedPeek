using BiliRadar.Models;
using BiliRadar.Services;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;

namespace FeedPeek.Compatibility.Tests;

public sealed class LegacyAdapterMappingTests
{
    [Fact]
    public async Task LegacyProvider_ExposesExistingUpdatesThroughFeedContract()
    {
        using var provider = new LegacyBiliFeedProvider(new MockBiliDataProvider());
        var subscription = new FeedSubscription(
            "legacy-subscription",
            LegacyBiliFeedProvider.LegacyProviderId,
            LegacyBiliFeedProvider.LegacySourceId,
            LegacyBiliFeedProvider.UpdatesFeedId,
            "Legacy updates",
            null,
            null,
            true,
            TimeSpan.FromMinutes(15),
            null);

        var first = await provider.GetPageAsync(new FeedPageRequest(subscription, null, 30));
        var second = await provider.GetPageAsync(
            new FeedPageRequest(subscription, first.NextCursor, 30));

        Assert.Equal(3, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Empty(second.Items);
        Assert.False(second.HasMore);
    }

    [Fact]
    public void BiliVideoUpdate_MapsToNamespacedFeedItem()
    {
        var publishedAt = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
        var legacy = new BiliVideoUpdate(
            "BV1TEST",
            123,
            456,
            "Legacy creator",
            "Legacy title",
            publishedAt,
            "https://example.test/video/BV1TEST",
            true,
            "https://example.test/cover.jpg",
            "https://example.test/avatar.jpg",
            "刚刚",
            "1:23",
            "Legacy summary",
            1,
            2);

        var item = LegacyBiliFeedProvider.MapItem(legacy);

        Assert.Equal(LegacyBiliFeedProvider.LegacyProviderId, item.ProviderId);
        Assert.Equal(LegacyBiliFeedProvider.LegacySourceId, item.SourceId);
        Assert.Equal(LegacyBiliFeedProvider.UpdatesFeedId, item.FeedId);
        Assert.Equal("BV1TEST", item.ItemId);
        Assert.Equal("456", item.CreatorKey);
        Assert.Equal("123", item.Metadata?["legacy.aid"]);
        Assert.Equal(publishedAt, item.PublishedAt);
    }
}
