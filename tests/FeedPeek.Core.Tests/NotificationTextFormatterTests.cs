using FeedPeek.Core.Notifications;
using System.Globalization;

namespace FeedPeek.Core.Tests;

public sealed class NotificationTextFormatterTests
{
    [Fact]
    public void Update_UsesSourceAndTitleThenSummary()
    {
        var text = NotificationTextFormatter.FormatUpdate(
            "IT之家", "Windows 迎来更新", "微软发布了新的系统版本。");

        Assert.Equal("IT之家|Windows 迎来更新", text.Title);
        Assert.Equal("微软发布了新的系统版本。", text.Subtitle);
    }

    [Fact]
    public void Update_StripsHtmlDecodesEntitiesAndNormalizesWhitespace()
    {
        var text = NotificationTextFormatter.FormatUpdate(
            "来源", "标题", "<p>你好&nbsp; <strong>世界</strong></p><p>第二行</p>");

        Assert.Equal("你好 世界 第二行", text.Subtitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_OmitsMissingOrRepeatedSummary(string? summary)
    {
        var text = NotificationTextFormatter.FormatUpdate("来源", "标题", summary);

        Assert.Equal("来源|标题", text.Title);
        Assert.Empty(text.Subtitle);
    }

    [Fact]
    public void Update_DoesNotRepeatTitleAfterHtmlCleanup()
    {
        var text = NotificationTextFormatter.FormatUpdate("来源", "标题", "<p>标题</p>");

        Assert.Empty(text.Subtitle);
    }

    [Fact]
    public void Update_RemovesEncodedMarkupAndScriptContent()
    {
        var text = NotificationTextFormatter.FormatUpdate(
            "来源", "标题", "&lt;p&gt;正文&lt;/p&gt;&lt;script&gt;alert(1)&lt;/script&gt;");

        Assert.Equal("正文", text.Subtitle);
    }

    [Fact]
    public void Update_TruncatesTitleByUnicodeTextElementsAndKeepsSeparator()
    {
        var text = NotificationTextFormatter.FormatUpdate(
            "来源", new string('标', NotificationTextFormatter.MaxTitleLength + 20), null);

        Assert.Contains("|", text.Title);
        Assert.EndsWith("…", text.Title);
        Assert.Equal(NotificationTextFormatter.MaxTitleLength,
            StringInfo.ParseCombiningCharacters(text.Title).Length);
    }

    [Fact]
    public void Update_TruncatesWithoutSplittingChineseOrEmoji()
    {
        var summary = new string('中', NotificationTextFormatter.MaxSummaryLength)
            + "😀尾部内容";
        var text = NotificationTextFormatter.FormatUpdate("来源", "标题", summary);

        Assert.EndsWith("…", text.Subtitle);
        Assert.DoesNotContain("尾部内容", text.Subtitle);
        Assert.DoesNotContain('\uFFFD', text.Subtitle);
        var textElementCount = StringInfo.ParseCombiningCharacters(text.Subtitle).Length;
        Assert.Equal(NotificationTextFormatter.MaxSummaryLength, textElementCount);
        for (var index = 0; index < text.Subtitle.Length; index++)
        {
            if (char.IsHighSurrogate(text.Subtitle[index]))
                Assert.True(index + 1 < text.Subtitle.Length
                    && char.IsLowSurrogate(text.Subtitle[index + 1]));
        }
    }

    [Fact]
    public void Live_UsesCreatorTitleAndLiveText()
    {
        var text = NotificationTextFormatter.FormatLive("主播", "晚间直播", "正在直播");

        Assert.Equal("主播|晚间直播", text.Title);
        Assert.Equal("正在直播", text.Subtitle);
    }

    [Fact]
    public void Live_UsesStartFallbackWhenTitleIsMissing()
    {
        var text = NotificationTextFormatter.FormatLive("主播", null);

        Assert.Equal("主播|开播", text.Title);
        Assert.Equal("正在直播", text.Subtitle);
    }
}
