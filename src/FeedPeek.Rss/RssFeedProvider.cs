using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FeedPeek.Rss;

public sealed record RssDiscoveryResult(
    Uri FeedUri,
    string Title,
    string SourceId,
    string? IconUrl);

public sealed partial class RssFeedProvider : IFeedProvider, IDisposable
{
    private const int MaxRedirects = 5;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public RssFeedProvider(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            }) { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FeedPeekPreview/2.0 (+RSS reader)");
    }

    public string ProviderId => RssProviderInfo.ProviderId;

    public async Task<FeedPage> GetPageAsync(FeedPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Subscription.ProviderId, ProviderId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.Subscription.Endpoint)
            || !Uri.TryCreate(request.Subscription.Endpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("The subscription is not a valid RSS source.");

        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(request.ETag)) message.Headers.IfNoneMatch.TryParseAdd(request.ETag);
        message.Headers.IfModifiedSince = request.LastModified;
        using var response = await SendAsync(message, RssProviderInfo.MaxFeedBytes, cancellationToken);
        var etag = response.Headers.ETag?.ToString();
        var lastModified = response.Content.Headers.LastModified;
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new FeedPage([], null, false, etag, lastModified, true);

        response.EnsureSuccessStatusCode();
        var bytes = await ReadLimitedAsync(response.Content, RssProviderInfo.MaxFeedBytes, cancellationToken);
        var effectiveUri = response.RequestMessage?.RequestUri ?? endpoint;
        using var stream = new MemoryStream(bytes, writable: false);
        var parsed = RssFeedParser.Parse(stream, effectiveUri, request.Subscription.SourceId,
            Math.Min(Math.Max(1, request.PageSize), RssProviderInfo.MaxItems));
        return new FeedPage(parsed.Items, null, false, etag, lastModified);
    }

    public async Task<RssDiscoveryResult> DiscoverAsync(string input, CancellationToken cancellationToken = default)
    {
        var initialUri = NormalizeInput(input);
        using var initialRequest = new HttpRequestMessage(HttpMethod.Get, initialUri);
        using var initialResponse = await SendAsync(initialRequest, RssProviderInfo.MaxFeedBytes, cancellationToken);
        initialResponse.EnsureSuccessStatusCode();
        var effectiveUri = initialResponse.RequestMessage?.RequestUri ?? initialUri;
        var mediaType = initialResponse.Content.Headers.ContentType?.MediaType;
        var limit = IsHtml(mediaType) ? RssProviderInfo.MaxHtmlDiscoveryBytes : RssProviderInfo.MaxFeedBytes;
        var bytes = await ReadLimitedAsync(initialResponse.Content, limit, cancellationToken);
        if (!IsHtml(mediaType) && TryParse(bytes, effectiveUri, out var parsed))
            return Result(effectiveUri, parsed.Title, parsed.IconUrl);

        var discoveredUri = FindAlternateFeedUri(effectiveUri, Encoding.UTF8.GetString(bytes))
            ?? throw new InvalidOperationException("No RSS or Atom feed was found at this address.");
        using var feedRequest = new HttpRequestMessage(HttpMethod.Get, discoveredUri);
        using var feedResponse = await SendAsync(feedRequest, RssProviderInfo.MaxFeedBytes, cancellationToken);
        feedResponse.EnsureSuccessStatusCode();
        var feedBytes = await ReadLimitedAsync(feedResponse.Content, RssProviderInfo.MaxFeedBytes, cancellationToken);
        var finalUri = feedResponse.RequestMessage?.RequestUri ?? discoveredUri;
        if (!TryParse(feedBytes, finalUri, out parsed))
            throw new InvalidOperationException("The discovered document is not a supported RSS or Atom feed.");
        return Result(finalUri, parsed.Title, parsed.IconUrl);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, int maxBytes, CancellationToken token)
    {
        var currentUri = request.RequestUri!;
        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            await RssNetworkPolicy.ValidateAsync(currentUri, token);
            using var currentRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
            foreach (var header in request.Headers)
                currentRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var response = await _httpClient.SendAsync(
                currentRequest,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirectCount == MaxRedirects)
                    throw new InvalidOperationException("The RSS redirect chain is invalid or too long.");

                currentUri = new Uri(currentUri, location);
                continue;
            }

            try
            {
                await RssNetworkPolicy.ValidateAsync(
                    response.RequestMessage?.RequestUri ?? currentUri,
                    token);
                if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                    throw new InvalidOperationException("The RSS response is too large.");
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("The RSS redirect chain is invalid or too long.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MultipleChoices
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidOperationException("The RSS response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool TryParse(byte[] bytes, Uri uri, out ParsedFeed parsed)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            parsed = RssFeedParser.Parse(stream, uri, RssFeedParser.CreateSourceId(uri));
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            parsed = null!;
            return false;
        }
    }

    private static RssDiscoveryResult Result(Uri uri, string title, string? iconUrl)
        => new(uri, title, RssFeedParser.CreateSourceId(uri), iconUrl);

    private static Uri NormalizeInput(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = $"https://{value}";
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri : throw new InvalidOperationException("Enter a valid HTTP or HTTPS address.");
    }

    private static bool IsHtml(string? mediaType)
        => mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;

    private static Uri? FindAlternateFeedUri(Uri pageUri, string html)
    {
        foreach (Match tag in LinkTagRegex().Matches(html))
        {
            var attributes = AttributeRegex().Matches(tag.Value).Cast<Match>().ToDictionary(
                match => match.Groups[1].Value, match => WebUtility.HtmlDecode(match.Groups[2].Value),
                StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("rel", out var rel)
                || !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase)
                || !attributes.TryGetValue("type", out var type)
                || type is not ("application/rss+xml" or "application/atom+xml")
                || !attributes.TryGetValue("href", out var href)) continue;
            if (Uri.TryCreate(pageUri, href, out var uri) && uri.Scheme is "http" or "https") return uri;
        }
        return null;
    }

    [GeneratedRegex("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkTagRegex();
    [GeneratedRegex("([a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.Singleline)]
    private static partial Regex AttributeRegex();
}
