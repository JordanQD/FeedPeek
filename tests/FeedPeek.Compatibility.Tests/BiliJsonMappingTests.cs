using BiliRadar.Services;

namespace FeedPeek.Compatibility.Tests;

public sealed class BiliJsonMappingTests
{
    [Fact]
    public void DynamicResponse_MapsArchivePgcAndOpusAndPreservesCursor()
    {
        using var document = FixtureLoader.Load("dynamic-video-page.json");

        var page = BiliLegacyJsonMapper.ParseVideoUpdatesResponse(document.RootElement);

        Assert.Equal("offset-page-2", page.NextOffset);
        Assert.True(page.HasMore);
        Assert.Collection(
            page.Items,
            archive =>
            {
                Assert.Equal("BV1ARCHIVE", archive.Id);
                Assert.Equal(101, archive.Aid);
                Assert.Equal(1001, archive.CreatorMid);
                Assert.Equal("Archive 作者", archive.CreatorName);
                Assert.Equal("Archive 标题", archive.Title);
                Assert.Equal("https://www.bilibili.com/video/BV1ARCHIVE", archive.Url);
                Assert.Equal("https://img.example/archive.jpg", archive.CoverUrl);
                Assert.Equal("https://img.example/avatar.jpg", archive.AvatarUrl);
                Assert.Equal("03:21", archive.DurationText);
                Assert.Equal("Archive 描述", archive.Description);
                Assert.Equal(7, archive.LikeCount);
                Assert.Equal(8, archive.CommentCount);
                Assert.True(archive.IsUnread);
            },
            pgc =>
            {
                Assert.Equal("BV1PGC", pgc.Id);
                Assert.Equal(202, pgc.Aid);
                Assert.Equal("https://www.bilibili.com/video/BV1PGC", pgc.Url);
            },
            opus =>
            {
                Assert.Equal("303", opus.Id);
                Assert.Equal(303, opus.Aid);
                Assert.Equal("https://img.example/opus.jpg", opus.CoverUrl);
                Assert.Equal("https://www.bilibili.com/av303", opus.Url);
            });
    }

    [Fact]
    public void HistoryResponse_FiltersNonArchiveItemsAndPreservesCursor()
    {
        using var document = FixtureLoader.Load("history-page.json");

        var page = BiliLegacyJsonMapper.ParseVideoHistoryResponse(document.RootElement, previousNextMax: 10);

        var item = Assert.Single(page.Items);
        Assert.Equal("BV1HISTORY", item.Id);
        Assert.Equal(404, item.Aid);
        Assert.Equal("历史作者", item.CreatorName);
        Assert.Equal("2:05", item.DurationText);
        Assert.Equal("P2/3 · 看到 0:45", item.Description);
        Assert.False(item.IsUnread);
        Assert.Equal(20, page.NextMax);
        Assert.Equal(1_700_000_100, page.NextViewAt);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void ViewLaterResponse_ComputesNextPageAndHasMoreFromTotalCount()
    {
        using var document = FixtureLoader.Load("view-later-page.json");

        var page = BiliLegacyJsonMapper.ParseViewLaterResponse(
            document.RootElement,
            loadedCount: 12,
            currentPageNumber: 2);

        var item = Assert.Single(page.Items);
        Assert.Equal("BV1LATER", item.Id);
        Assert.Equal(505, item.Aid);
        Assert.Equal("稍后作者", item.CreatorName);
        Assert.Equal("1:01", item.DurationText);
        Assert.Equal("已看完", item.Description);
        Assert.Equal(9, item.LikeCount);
        Assert.Equal(10, item.CommentCount);
        Assert.Equal(30, page.TotalCount);
        Assert.Equal(3, page.NextPageNumber);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void ApiError_IsKeptAsAnInvalidOperationContract()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"code":-101,"message":"账号未登录"}""");

        var error = Assert.Throws<InvalidOperationException>(
            () => BiliLegacyJsonMapper.ParseVideoUpdatesResponse(document.RootElement));

        Assert.Contains("账号未登录", error.Message);
        Assert.Contains("-101", error.Message);
    }
}
