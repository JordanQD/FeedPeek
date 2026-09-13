using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FeedPeek.PluginRuntime.Host;

public sealed class PluginHttpBridge : IPluginHttpBridge, IDisposable
{
    private const int DefaultMaxResponseBytes = 4 * 1024 * 1024;
    private const int AbsoluteMaxResponseBytes = 16 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
        { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Cookie", "Set-Cookie", "Authorization", "Proxy-Authorization", "Content-Length", "Transfer-Encoding", "Connection" };

    private readonly HttpClient _client;
    private readonly IPluginCookieProvider _cookies;

    public PluginHttpBridge(IPluginCookieProvider? cookies = null)
    {
        _cookies = cookies ?? NullPluginCookieProvider.Instance;
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        });
    }

    public async Task<HostHttpResponse> SendAsync(
        PluginManifest manifest,
        PluginHostContext context,
        HostHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!AllowedMethods.Contains(request.Method))
            throw new PluginHostBridgeException("HTTP_METHOD_FORBIDDEN", "The requested HTTP method is not allowed.");
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new PluginHostBridgeException("HTTP_URL_INVALID", "Plugin HTTP requests require an absolute HTTPS URL.");
        EnsureHostAllowed(uri, manifest.Network.AllowedHosts, "HTTP_HOST_FORBIDDEN");
        ValidateHeaders(request.Headers, PluginManifestRules.ResolveRuntime(manifest));

        var maxBytes = Math.Clamp(request.MaxResponseBytes ?? DefaultMaxResponseBytes, 1, AbsoluteMaxResponseBytes);
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs ?? 15_000, 100, 60_000));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var method = new HttpMethod(request.Method.ToUpperInvariant());
        var body = DecodeBody(request.Body, request.BodyEncoding);
        var currentUri = uri;

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var message = new HttpRequestMessage(method, currentUri);
            if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
                message.Content = new ByteArrayContent(body);
            AddHeaders(message, request.Headers);

            if (!string.IsNullOrWhiteSpace(context.CredentialProfileId)
                && IsCredentialDomain(manifest.Auth?.CookieDomains, currentUri.IdnHost))
            {
                var cookie = await _cookies.GetCookieHeaderAsync(
                    context.PluginId,
                    context.CredentialProfileId,
                    currentUri,
                    timeoutSource.Token);
                if (!string.IsNullOrWhiteSpace(cookie))
                    message.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                if (redirectCount >= MaxRedirects)
                    throw new PluginHostBridgeException("HTTP_REDIRECT_LIMIT", "The HTTP redirect chain is too long.", true);
                var redirectUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                if (redirectUri.Scheme != Uri.UriSchemeHttps)
                    throw new PluginHostBridgeException("HTTP_REDIRECT_FORBIDDEN", "Redirects must remain on HTTPS.");
                EnsureHostAllowed(
                    redirectUri,
                    manifest.Network.AllowedHosts.Concat(manifest.Network.AllowRedirectHosts),
                    "HTTP_REDIRECT_HOST_FORBIDDEN");

                if (response.StatusCode == HttpStatusCode.SeeOther
                    || ((response.StatusCode == HttpStatusCode.Moved || response.StatusCode == HttpStatusCode.Redirect)
                        && method == HttpMethod.Post))
                {
                    method = HttpMethod.Get;
                    body = null;
                }

                currentUri = redirectUri;
                continue;
            }

            var bytes = await ReadBoundedAsync(response, maxBytes, timeoutSource.Token);
            var (responseBody, encoding) = EncodeBody(response.Content.Headers.ContentType, bytes);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .Where(header => !string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(", ", group.SelectMany(header => header.Value)),
                    StringComparer.OrdinalIgnoreCase);
            return new HostHttpResponse((int)response.StatusCode, currentUri.AbsoluteUri, headers, responseBody, encoding);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// A declared credential domain also covers its subdomains, matching the LiveParse host rule
    /// (<c>host == domain || host.hasSuffix("." + domain)</c>). Request routing still uses the exact
    /// <c>network.allowedHosts</c> list.
    /// </summary>
    private static bool IsCredentialDomain(IReadOnlyList<string>? cookieDomains, string host)
    {
        if (cookieDomains is null || host.Length == 0)
            return false;
        foreach (var domain in cookieDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
                continue;
            if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.Length > domain.Length
                && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureHostAllowed(Uri uri, IEnumerable<string> allowedHosts, string errorCode)
    {
        if (!allowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new PluginHostBridgeException(errorCode, "The requested host is not declared by the plugin manifest.");
    }

    /// <summary>
    /// The LiveParse host lets a plugin set its own <c>Cookie</c>/<c>Authorization</c> headers; it only
    /// strips them for credential-bearing requests (which the dialect already does before calling here).
    /// Anonymous Douyin calls depend on that: they carry a self-managed ttwid cookie.
    /// </summary>
    private static readonly HashSet<string> LiveParseCredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Cookie", "Cookie2", "Authorization" };

    private static void ValidateHeaders(IReadOnlyDictionary<string, string>? headers, string runtime)
    {
        var allowsCredentialHeaders = string.Equals(
            runtime,
            PluginManifestRules.LiveParseRuntime,
            StringComparison.Ordinal);
        foreach (var name in headers?.Keys ?? [])
        {
            if (allowsCredentialHeaders && LiveParseCredentialHeaders.Contains(name))
                continue;
            if (ForbiddenHeaders.Contains(name)
                || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginHostBridgeException("HTTP_HEADER_FORBIDDEN", "The plugin attempted to set a reserved HTTP header.");
            }
        }
    }

    private static void AddHeaders(HttpRequestMessage message, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var (name, value) in headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content is not null)
                message.Content.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static byte[]? DecodeBody(string? body, string? encoding)
    {
        if (body is null)
            return null;
        try
        {
            return string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(body)
                : Encoding.UTF8.GetBytes(body);
        }
        catch (FormatException)
        {
            throw new PluginHostBridgeException("HTTP_BODY_INVALID", "The HTTP request body is not valid base64.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new PluginHostBridgeException("HTTP_RESPONSE_TOO_LARGE", "The HTTP response exceeds the configured size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > maxBytes)
                throw new PluginHostBridgeException("HTTP_RESPONSE_TOO_LARGE", "The HTTP response exceeds the configured size limit.");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static (string Body, string Encoding) EncodeBody(MediaTypeHeaderValue? contentType, byte[] bytes)
    {
        var mediaType = contentType?.MediaType ?? string.Empty;
        var isText = mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        return isText
            ? (Encoding.UTF8.GetString(bytes), "utf8")
            : (Convert.ToBase64String(bytes), "base64");
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
