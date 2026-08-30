using BiliRadar.Models;
using BiliRadar.Services;

namespace FeedPeek.Compatibility.Tests;

public sealed class NotificationBaselinePlannerTests
{
    [Fact]
    public void FirstVideoRefresh_InitializesBaselineWithoutNotifications()
    {
        var updates = new[] { Video("BV2", 2), Video("BV1", 1) };

        var plan = NotificationBaselinePlanner.ForVideos(updates, [], false, 3, 80);

        Assert.True(plan.InitializesBaseline);
        Assert.Empty(plan.NewItems);
        Assert.Equal(new[] { "BV2", "BV1" }, plan.KnownIds);
    }

    [Fact]
    public void LaterVideoRefresh_IsCaseInsensitiveAndCapsNewNotificationsAtThree()
    {
        var updates = new[]
        {
            Video("BV5", 5),
            Video("BV4", 4),
            Video("BV3", 3),
            Video("BV2", 2),
            Video("bv1", 1),
        };

        var plan = NotificationBaselinePlanner.ForVideos(updates, ["BV1"], true, 3, 80);

        Assert.False(plan.InitializesBaseline);
        Assert.Equal(new[] { "BV5", "BV4", "BV3" }, plan.NewItems.Select(item => item.Id));
        Assert.Equal(new[] { "BV5", "BV4", "BV3", "BV2", "bv1" }, plan.KnownIds);
    }

    [Fact]
    public void LiveBaseline_DeduplicatesRoomsAndOnlyRetainsCurrentlyLiveRooms()
    {
        var creators = new[]
        {
            Live(100, "首个"),
            Live(100, "重复"),
            Live(200, "第二个"),
        };

        var first = NotificationBaselinePlanner.ForLiveCreators(creators, [], false, 3, 80);
        var later = NotificationBaselinePlanner.ForLiveCreators(creators, ["100", "300"], true, 3, 80);

        Assert.True(first.InitializesBaseline);
        Assert.Empty(first.NewItems);
        Assert.Equal(new[] { "100", "200" }, first.KnownIds);
        Assert.Equal(200, Assert.Single(later.NewItems).RoomId);
        Assert.Equal(new[] { "100", "200" }, later.KnownIds);
    }

    [Fact]
    public void LiveRoom_IsNewAgainAfterGoingOffline()
    {
        var initial = NotificationBaselinePlanner.ForLiveCreators([Live(100, "首播")], [], false, 3, 80);
        var offline = NotificationBaselinePlanner.ForLiveCreators([], initial.KnownIds, true, 3, 80);
        var restarted = NotificationBaselinePlanner.ForLiveCreators(
            [Live(100, "再次开播")],
            offline.KnownIds,
            true,
            3,
            80);

        Assert.Empty(offline.KnownIds);
        Assert.Equal(100, Assert.Single(restarted.NewItems).RoomId);
    }

    [Fact]
    public void KnownVideoIds_AreCappedAtEighty()
    {
        var updates = Enumerable.Range(1, 100).Select(index => Video($"BV{index:000}", index)).ToList();

        var plan = NotificationBaselinePlanner.ForVideos(updates, [], false, 3, 80);

        Assert.Equal(80, plan.KnownIds.Count);
        Assert.Equal("BV100", plan.KnownIds[0]);
        Assert.Equal("BV021", plan.KnownIds[^1]);
    }

    private static BiliVideoUpdate Video(string id, int minute)
        => new(
            id,
            minute,
            1,
            "作者",
            id,
            DateTimeOffset.UnixEpoch.AddMinutes(minute),
            $"https://example.test/{id}",
            true,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0);

    private static BiliLiveCreator Live(long roomId, string name)
        => new(1, roomId, name, string.Empty, "直播", $"https://live.example.test/{roomId}");
}
