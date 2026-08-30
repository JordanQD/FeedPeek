using FeedPeek.Core.Models;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FeedPeek.Rss;

public sealed record ParsedFeed(
    string Title,
    IReadOnlyList<FeedItem> Items,
    string Format,
    string? IconUrl = null);

public static partial class RssFeedParser
{
    public static ParsedFeed Parse(Stream stream, Uri feedUri, string sourceId, int maxItems = RssProviderInfo.MaxItems)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(feedUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = RssProviderInfo.MaxFeedBytes * 2L,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(stream, settings, feedUri.AbsoluteUri);
        var root = XDocument.Load(reader).Root ?? throw new XmlException("The feed document has no root element.");
        return root.Name.LocalName switch
        {
            "rss" or "RDF" => ParseRss(root, feedUri, sourceId, maxItems),
            "feed" => ParseAtom(root, feedUri, sourceId, maxItems),
            _ => throw new XmlException($"Unsupported feed root element '{root.Name}'."),
        };
    }

    public static string CreateSourceId(Uri canonicalFeedUri)
        => $"rss-{Hash(NormalizeUri(canonicalFeedUri))}"[..28];

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var withoutUnsafeBlocks = UnsafeBlockRegex().Replace(html, " ");
        var withoutTags = HtmlTagRegex().Replace(withoutUnsafeBlocks, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static ParsedFeed ParseRss(XElement root, Uri feedUri, string sourceId, int maxItems)
    {
        var channel = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "channel")
            ?? throw new XmlException("The RSS document does not contain a channel.");
        var title = Value(channel, "title") ?? feedUri.Host;
        var iconUrl = FindFeedIcon(channel, feedUri);
        var items = channel.Elements().Where(element => element.Name.LocalName == "item")
            .Take(Math.Max(1, maxItems))
            .Select(item => MapRssItem(item, feedUri, sourceId, title))
            .OrderByDescending(item => item.PublishedAt)
            .ToList();
        return new ParsedFeed(title, items, "rss", iconUrl);
    }

    private static FeedItem MapRssItem(XElement item, Uri feedUri, string sourceId, string feedTitle)
    {
        var title = ToPlainText(Value(item, "title"));
        var link = ResolveUri(feedUri, Value(item, "link"))?.AbsoluteUri ?? feedUri.AbsoluteUri;
        var summaryHtml = Value(item, "description") ?? Value(item, "encoded");
        var summary = ToPlainText(summaryHtml);
        var author = ToPlainText(Value(item, "creator") ?? Value(item, "author"));
        var publishedAt = ParseDate(Value(item, "pubDate") ?? Value(item, "date"));
        return CreateItem(sourceId, Value(item, "guid"), link, title, author, null, publishedAt,
            summary, FindImage(item, feedUri, summaryHtml), FindDurationText(item), feedTitle, "rss");
    }

    private static ParsedFeed ParseAtom(XElement root, Uri feedUri, string sourceId, int maxItems)
    {
        var title = ToPlainText(Value(root, "title"));
        if (string.IsNullOrWhiteSpace(title)) title = feedUri.Host;
        var iconUrl = FindFeedIcon(root, feedUri);
        var items = root.Elements().Where(element => element.Name.LocalName == "entry")
            .Take(Math.Max(1, maxItems))
            .Select(item => MapAtomItem(item, feedUri, sourceId, title))
            .OrderByDescending(item => item.PublishedAt)
            .ToList();
        return new ParsedFeed(title, items, "atom", iconUrl);
    }

    private static FeedItem MapAtomItem(XElement entry, Uri feedUri, string sourceId, string feedTitle)
    {
        var title = ToPlainText(Value(entry, "title"));
        var linkElement = entry.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "link" && ((string?)element.Attribute("rel") is null or "alternate"))
            ?? entry.Elements().FirstOrDefault(element => element.Name.LocalName == "link");
        var link = ResolveUri(feedUri, (string?)linkElement?.Attribute("href"))?.AbsoluteUri ?? feedUri.AbsoluteUri;
        var summaryHtml = Value(entry, "summary") ?? Value(entry, "content");
        var summary = ToPlainText(summaryHtml);
        var authorElement = entry.Elements().FirstOrDefault(element => element.Name.LocalName == "author");
        var author = ToPlainText(authorElement is null ? null : Value(authorElement, "name"));
        var authorUrl = ResolveUri(feedUri, authorElement is null ? null : Value(authorElement, "uri"))?.AbsoluteUri;
        var publishedAt = ParseDate(Value(entry, "published") ?? Value(entry, "updated"));
        return CreateItem(sourceId, Value(entry, "id"), link, title, author, authorUrl, publishedAt,
            summary, FindImage(entry, feedUri, summaryHtml), FindDurationText(entry), feedTitle, "atom");
    }

    private static FeedItem CreateItem(
        string sourceId, string? stableId, string link, string title, string author, string? authorUrl,
        DateTimeOffset publishedAt, string summary, string? image, string? durationText,
        string feedTitle, string format)
    {
        var itemId = !string.IsNullOrWhiteSpace(stableId)
            ? $"guid:{Hash(stableId.Trim())}"
            : !string.IsNullOrWhiteSpace(link)
                ? $"link:{Hash(link)}"
                : $"hash:{Hash($"{title}\n{author}\n{publishedAt:O}\n{summary}")}";
        return new FeedItem(
            RssProviderInfo.ProviderId, sourceId, RssProviderInfo.UpdatesFeedId, itemId,
            string.IsNullOrWhiteSpace(title) ? link : title, null,
            string.IsNullOrWhiteSpace(author) ? feedTitle : author, authorUrl, link, publishedAt,
            NullIfWhiteSpace(summary), image, null, durationText,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["rss.format"] = format });
    }

    private static string? FindImage(XElement item, Uri feedUri, string? html)
    {
        foreach (var element in item.DescendantsAndSelf())
        {
            if (element.Name.LocalName == "enclosure"
                && ((string?)element.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var enclosure = ResolveUri(feedUri, (string?)element.Attribute("url"));
                if (enclosure is not null) return enclosure.AbsoluteUri;
            }

            if (element.Name.LocalName is "thumbnail" or "content")
            {
                var type = (string?)element.Attribute("type");
                var media = ResolveUri(feedUri, (string?)element.Attribute("url"));
                if (media is not null && (element.Name.LocalName == "thumbnail"
                    || type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)) return media.AbsoluteUri;
            }
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            var match = ImageSourceRegex().Match(html);
            if (match.Success) return ResolveUri(feedUri, WebUtility.HtmlDecode(match.Groups[1].Value))?.AbsoluteUri;
        }
        return null;
    }

    private static string? FindFeedIcon(XElement container, Uri feedUri)
    {
        var atomIcon = container.Elements().FirstOrDefault(element =>
            element.Name.LocalName is "icon" or "logo")?.Value;
        var resolvedAtomIcon = ResolveUri(feedUri, atomIcon);
        if (resolvedAtomIcon is not null)
        {
            return resolvedAtomIcon.AbsoluteUri;
        }

        var rssImageUrl = container.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "image")?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "url")?
            .Value;
        var resolvedRssImage = ResolveUri(feedUri, rssImageUrl);
        if (resolvedRssImage is not null)
        {
            return resolvedRssImage.AbsoluteUri;
        }

        var attributedImage = container.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "image" && element.Attribute("href") is not null);
        return ResolveUri(feedUri, (string?)attributedImage?.Attribute("href"))?.AbsoluteUri;
    }

    private static string? FindDurationText(XElement item)
    {
        var duration = item.DescendantsAndSelf()
            .FirstOrDefault(element => element.Name.LocalName == "duration");
        var rawValue = duration?.Value;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            rawValue = item.DescendantsAndSelf()
                .Where(element => element.Name.LocalName == "content")
                .Select(element => (string?)element.Attribute("duration"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            return FormatDuration(TimeSpan.FromSeconds(seconds));
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var durationValue)
            ? FormatDuration(durationValue)
            : null;
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private static string? Value(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value?.Trim();

    private static DateTimeOffset ParseDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime() : DateTimeOffset.UnixEpoch;

    private static Uri? ResolveUri(Uri baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(baseUri, value.Trim(), out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri : null;
    }

    private static string NormalizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if ((builder.Scheme == "https" && builder.Port == 443) || (builder.Scheme == "http" && builder.Port == 80))
            builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("<(script|style)\\b[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnsafeBlockRegex();
    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex("<img\\b[^>]*?src\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageSourceRegex();
}
