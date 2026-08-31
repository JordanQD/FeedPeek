using BiliRadar.Models;
using BiliRadar.Services;
using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;

namespace FeedPeek.Compatibility.Tests;

public sealed class FeedCompatibilityTests
{
    [Fact]
    public void FeedBatch_AllItemsWithinLimitRemainNotificationCandidates()
    {
        var items = Enumerable.Range(1, 8)
            .Select(index => Feed("rss", "ithome", $"item-{index}", index))
            .ToList();

        var plan = FeedNotificationBaselinePlanner.ForFeedItems(items, [], true, 20, 80);

        Assert.Equal(8, plan.NewItems.Count);
        Assert.Equal(8, plan.KnownIds.Count);
    }

    [Fact]
    public void FeedIdentity_DistinguishesTheSameItemIdAcrossSources()
    {
        var plan = FeedNotificationBaselinePlanner.ForFeedItems(
            [Feed("rss", "source-a", "shared", 2), Feed("rss", "source-b", "shared", 1)],
            [],
            true,
            20,
            80);

        Assert.Equal(2, plan.NewItems.Count);
        Assert.Equal(2, plan.KnownIds.Count);
    }

    [Fact]
    public void LiveIdentity_DistinguishesSourcesAndAllowsRestartAfterOffline()
    {
        var first = Live("provider", "source-a", "creator", "room-1");
        var otherSource = Live("provider", "source-b", "creator", "room-1");
        var initial = FeedNotificationBaselinePlanner.ForLiveItems(
            [first, otherSource], [], false, 3, 80);
        var offline = FeedNotificationBaselinePlanner.ForLiveItems([], initial.KnownIds, true, 3, 80);
        var restarted = FeedNotificationBaselinePlanner.ForLiveItems(
            [first], offline.KnownIds, true, 3, 80);

        Assert.Equal(2, initial.KnownIds.Count);
        Assert.Empty(offline.KnownIds);
        Assert.Single(restarted.NewItems);
    }

    [Fact]
    public void LegacyCookieHeader_NormalizesForOneWayCredentialMigration()
    {
        var normalized = CookieNormalizer.Normalize(
            "  SESSDATA = abc ; bili_jct=token; malformed; DedeUserID = 42  ");

        Assert.Equal("SESSDATA=abc; bili_jct=token; DedeUserID=42", normalized);
    }

    private static FeedItem Feed(
        string providerId,
        string sourceId,
        string itemId,
        int minute)
        => new(
            providerId,
            sourceId,
            "updates",
            itemId,
            itemId,
            null,
            sourceId,
            null,
            $"https://example.test/{sourceId}/{itemId}",
            DateTimeOffset.UnixEpoch.AddMinutes(minute),
            null,
            null,
            null,
            null,
            null);

    private static FeedLiveNotificationItem Live(
        string providerId,
        string sourceId,
        string creatorKey,
        string roomId)
        => new(
            providerId,
            sourceId,
            new PluginLiveItem(
                creatorKey,
                creatorKey,
                $"https://live.example.test/{roomId}",
                "直播",
                null,
                null,
                null,
                null,
                new Dictionary<string, string> { ["roomId"] = roomId }));
}
