using BiliRadar.Models;
using BiliRadar.Services;

namespace FeedPeek.Compatibility.Tests;

public sealed class LegacyPagingAndStorageTests
{
    [Fact]
    public void VideoPageBuffer_SplitsApiBatchIntoTwelveItemPages()
    {
        var pageBuffer = new LegacyVideoPageBuffer<BiliVideoUpdate>(12);
        var items = Enumerable.Range(1, 25).Select(CreateUpdate).ToList();

        var first = pageBuffer.TakePage(items);
        var second = pageBuffer.TakePendingPage();
        var third = pageBuffer.TakePendingPage();
        var fourth = pageBuffer.TakePendingPage();

        Assert.Equal(Enumerable.Range(1, 12).Select(Id), first.Select(item => item.Id));
        Assert.Equal(Enumerable.Range(13, 12).Select(Id), second.Select(item => item.Id));
        Assert.Equal(Id(25), Assert.Single(third).Id);
        Assert.Empty(fourth);
    }

    [Fact]
    public void LegacyItemIds_AreDeduplicatedCaseInsensitively()
    {
        var tracker = new LegacyItemIdTracker();

        Assert.True(tracker.Add("BV1ABC"));
        Assert.False(tracker.Add("bv1abc"));
        Assert.True(tracker.Contains("Bv1AbC"));
        Assert.True(tracker.Add("BV1OTHER"));
    }

    [Fact]
    public void CookieNormalization_PreservesHeaderShapeAndLastDuplicateValue()
    {
        var normalized = CookieNormalizer.Normalize(
            " SESSDATA=first\n bili_jct=csrf ; ignored ; sessdata=second ; DedeUserID=42 ");

        Assert.Equal("SESSDATA=second; bili_jct=csrf; DedeUserID=42", normalized);
    }

    private static BiliVideoUpdate CreateUpdate(int index)
        => new(
            Id(index),
            index,
            1,
            "作者",
            $"标题 {index}",
            DateTimeOffset.UnixEpoch.AddMinutes(index),
            $"https://example.test/{index}",
            true,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0);

    private static string Id(int index) => $"BV{index:00}";
}
