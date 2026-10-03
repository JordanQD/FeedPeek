using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FeedPeek.Core.Notifications;

/// <summary>The two lines shown by a Windows app notification.</summary>
public sealed record NotificationText(string Title, string Subtitle);

/// <summary>Converts provider content into the compact, consistent notification copy.</summary>
public static partial class NotificationTextFormatter
{
    /// <summary>Maximum number of Unicode text elements kept in a notification title.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>Maximum number of Unicode text elements kept in an update summary.</summary>
    public const int MaxSummaryLength = 240;

    public static NotificationText FormatUpdate(string sourceName, string title, string? summary)
    {
        var displaySource = Normalize(sourceName);
        if (displaySource.Length == 0)
            displaySource = "来源";

        var displayTitle = Normalize(title);
        if (displayTitle.Length == 0)
            displayTitle = "更新";

        var displaySummary = CleanSummary(summary);
        if (string.Equals(displaySummary, displayTitle, StringComparison.OrdinalIgnoreCase))
            displaySummary = string.Empty;

        return new(FormatTitle(displaySource, displayTitle), displaySummary);
    }

    public static NotificationText FormatLive(
        string creatorName,
        string? title,
        string liveText = "正在直播")
    {
        var displayCreator = Normalize(creatorName);
        if (displayCreator.Length == 0)
            displayCreator = "主播";

        var displayTitle = Normalize(title ?? string.Empty);
        if (displayTitle.Length == 0)
            displayTitle = "开播";

        var displayLiveText = Normalize(liveText);
        if (displayLiveText.Length == 0)
            displayLiveText = "正在直播";

        return new(FormatTitle(displayCreator, displayTitle), displayLiveText);
    }

    private static string CleanSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        // Decode once so entity-encoded tags are treated like ordinary markup;
        // remove executable/content-bearing elements before stripping tags.
        var decoded = WebUtility.HtmlDecode(summary);
        var withoutEmbeddedContent = DangerousElementRegex().Replace(decoded, " ");
        var plainText = HtmlTagRegex().Replace(withoutEmbeddedContent, " ");
        plainText = Normalize(plainText);
        return TruncateTextElements(plainText, MaxSummaryLength);
    }

    private static string FormatTitle(string sourceName, string title)
    {
        var sourceLength = TextElementCount(sourceName);
        if (sourceLength <= MaxTitleLength - 3)
            return $"{sourceName}|{TruncateTextElements(title, MaxTitleLength - sourceLength - 1)}";

        var shortenedSource = TruncateTextElements(sourceName, MaxTitleLength - 3);
        return $"{shortenedSource}|{TruncateTextElements(title, 2)}";
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value.Trim(), " ");

    private static string TruncateTextElements(string value, int maxLength)
    {
        if (value.Length == 0 || maxLength <= 0)
            return string.Empty;

        var starts = StringInfo.ParseCombiningCharacters(value);
        if (starts.Length <= maxLength)
            return value;

        // Reserve one text element for the ellipsis. StringInfo keeps a
        // surrogate pair and common emoji sequences together at the cut.
        var keep = Math.Max(0, maxLength - 1);
        return value[..starts[keep]] + "…";
    }

    private static int TextElementCount(string value)
        => StringInfo.ParseCombiningCharacters(value).Length;

    [GeneratedRegex(@"<\s*(script|style|iframe|object)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousElementRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
