using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FeedPeek.PluginRuntime.Manifest;

/// <summary>
/// Describes a plugin folder written for the LiveParse host (<c>manifest.json</c> +
/// <c>globalThis.LiveParsePlugin</c>).
/// </summary>
public sealed record LiveParsePackageConversion(
    PluginManifest Manifest,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<string> DroppedPreloadScripts,
    IReadOnlyList<string> Hosts);

/// <summary>
/// Turns a LiveParse plugin folder into a FeedPeek plugin package by writing a
/// <c>plugin.json</c> next to the untouched plugin files. Only the live view is mapped: the
/// converted package declares <c>feed.live</c> and asks the user for the live room ids to pin per source.
/// </summary>
public static partial class LiveParsePackageConverter
{
    /// <summary>
    /// The converted manifest is written for a human to read (and to diff against the source package), so
    /// unset optional fields such as <c>icon</c> are omitted instead of serialized as null.
    /// </summary>
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(PluginJson.SerializerOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string SourceManifestFileName = "manifest.json";
    private const int MaxManifestBytes = 256 * 1024;

    /// <summary>
    /// The live state that means "actually broadcasting". Audited against every bundled platform
    /// mapping: Bilibili maps live_status 1 to "1" and re-broadcasts to "0", Douyu maps
    /// show_status 1 plus no video loop to "1" and re-broadcasts to "2"/"0", Douyin maps status 2 with
    /// a playable stream to "1" and everything else to "0"/"3".
    /// </summary>
    private const string LiveStateBroadcasting = "1";

    /// <summary>
    /// Platforms whose pinned-room detail and live state were measured to work without credentials.
    /// The source manifest's <c>auth.required</c> describes the platform as a whole (search, purchased
    /// rooms, danmaku), but FeedPeek only maps the live view, so copying it verbatim would wrongly make
    /// login a prerequisite for an anonymous source. Login stays offered, it just stops being required.
    /// </summary>
    private static readonly HashSet<string> AnonymousLiveVerified = new(StringComparer.Ordinal)
    {
        "douyin",
        "douyu",
    };

    /// <summary>
    /// Hosts are taken from this table when the plugin id is known so that the granted network
    /// permission is exactly the set the plugin was audited against. Unknown plugins fall back to
    /// extracting literal hosts from their declared scripts.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownHosts = new(StringComparer.Ordinal)
    {
        ["bilibili"] =
        [
            "api.bilibili.com",
            "api.live.bilibili.com",
            "live.bilibili.com",
            "passport.bilibili.com",
            "s1.hdslb.com",
            "www.bilibili.com",
        ],
        ["douyin"] =
        [
            "live.douyin.com",
            "ttwid.bytedance.com",
            "webcast.amemv.com",
            "www.douyin.com",
        ],
        ["douyu"] =
        [
            "apic.douyucdn.cn",
            "m.douyu.com",
            "www.douyu.com",
        ],
    };

    public static bool CanConvert(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return File.Exists(Path.Combine(directory, SourceManifestFileName))
            && !File.Exists(Path.Combine(directory, PluginManifestRules.FileName));
    }

    /// <summary>
    /// Copies <paramref name="sourceDirectory"/> into <paramref name="targetDirectory"/> and writes a
    /// FeedPeek <c>plugin.json</c> for it. Returns <see langword="null"/> when the folder is not a
    /// LiveParse package.
    /// </summary>
    public static async Task<LiveParsePackageConversion?> TryConvertAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!CanConvert(sourceDirectory))
            return null;

        var manifestPath = Path.Combine(sourceDirectory, SourceManifestFileName);
        if (new FileInfo(manifestPath).Length > MaxManifestBytes)
            throw new PluginManifestException("LIVEPARSE_MANIFEST_TOO_LARGE", "The LiveParse manifest exceeds the size limit.");

        LiveParseManifest source;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            source = await JsonSerializer.DeserializeAsync<LiveParseManifest>(
                    stream,
                    PluginJson.SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false)
            ?? throw new PluginManifestException("LIVEPARSE_MANIFEST_EMPTY", "The LiveParse manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException("LIVEPARSE_MANIFEST_INVALID", $"The LiveParse manifest is invalid: {ex.Message}");
        }

        var conversion = BuildConversion(sourceDirectory, source);
        CopyDirectory(sourceDirectory, targetDirectory);
        var targetManifestPath = Path.Combine(targetDirectory, PluginManifestRules.FileName);
        await File.WriteAllTextAsync(
                targetManifestPath,
                JsonSerializer.Serialize(conversion.Manifest, ManifestJsonOptions) + Environment.NewLine,
                cancellationToken)
            .ConfigureAwait(false);
        return conversion;
    }

    /// <summary>
    /// Picks the small square icon LiveParse packages ship next to their oversized TV artwork. The
    /// assets are what the AngelLive host renders in its list, so reusing one keeps FeedPeek's rows
    /// visually identical to the platform it was converted from; <c>*_dark</c> and <c>tv_*</c> art is
    /// only used when nothing better exists.
    /// </summary>
    private static string? ResolveIcon(string sourceDirectory)
    {
        var candidates = new List<(int Rank, string RelativePath)>();
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!PluginManifestRules.SupportedIconExtensions.Contains(Path.GetExtension(name)))
                continue;
            if (name.Contains("_dark", StringComparison.OrdinalIgnoreCase))
                continue;
            if (new FileInfo(file).Length > PluginManifestRules.MaxIconBytes)
                continue;

            var rank = name.Contains("pad_live_card", StringComparison.OrdinalIgnoreCase) ? 1
                : name.Contains("mini_live_card", StringComparison.OrdinalIgnoreCase) ? 2
                : name.Contains("live_card", StringComparison.OrdinalIgnoreCase) ? 0
                : name.Contains("logo", StringComparison.OrdinalIgnoreCase) ? 1
                : name.Contains("tv_", StringComparison.OrdinalIgnoreCase) ? 4
                : 3;
            candidates.Add((rank, Path.GetRelativePath(sourceDirectory, file)));
        }

        return candidates
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.RelativePath.Length)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.RelativePath.Replace('\\', '/'))
            .FirstOrDefault();
    }

    private static LiveParsePackageConversion BuildConversion(string sourceDirectory, LiveParseManifest source)
    {
        var pluginId = (source.PluginId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(pluginId) || !SimplePluginIdPattern().IsMatch(pluginId))
            throw new PluginManifestException("LIVEPARSE_PLUGIN_ID_INVALID", "The LiveParse manifest has an unsupported pluginId.");
        if (string.IsNullOrWhiteSpace(source.Entry) || !File.Exists(Path.Combine(sourceDirectory, source.Entry)))
            throw new PluginManifestException("LIVEPARSE_ENTRY_MISSING", "The LiveParse manifest entry script is missing.");
        if (string.IsNullOrWhiteSpace(source.Version) || string.IsNullOrWhiteSpace(source.DisplayName))
            throw new PluginManifestException("LIVEPARSE_MANIFEST_INCOMPLETE", "The LiveParse manifest needs version and displayName.");

        var roomUrlTemplate = source.HostBehavior?.ExternalRoomUrlTemplate?.Trim();
        if (string.IsNullOrWhiteSpace(roomUrlTemplate) || !roomUrlTemplate.Contains("{roomId}", StringComparison.Ordinal))
        {
            throw new PluginManifestException(
                "LIVEPARSE_ROOM_URL_TEMPLATE_MISSING",
                "The LiveParse manifest needs hostBehavior.externalRoomURLTemplate with a {roomId} placeholder.");
        }

        // Only the login preload survives: the live view needs no danmaku, no archive decoder and no
        // in-process signing SDK, and those bundles are what break a strict ECMAScript engine.
        var preloads = new List<string>();
        var dropped = new List<string>();
        foreach (var script in source.PreloadScripts ?? [])
        {
            if (string.IsNullOrWhiteSpace(script) || !File.Exists(Path.Combine(sourceDirectory, script)))
                continue;
            if (Path.GetFileNameWithoutExtension(script).EndsWith("login", StringComparison.OrdinalIgnoreCase))
                preloads.Add(script);
            else
                dropped.Add(script);
        }

        var scripts = new List<string>(preloads) { source.Entry };
        var hosts = ResolveHosts(pluginId, sourceDirectory, scripts, source);
        var declaresLogin = source.Auth?.Required == true || source.LoginFlow is not null;
        var authRequired = source.Auth?.Required == true && !AnonymousLiveVerified.Contains(pluginId);
        var capabilities = new List<string> { "feed.live", "source.configuration" };
        if (declaresLogin)
            capabilities.Add("account.auth");

        var signalCookies = (source.LoginFlow?.AuthSignalCookies ?? [])
            .Concat(source.LoginFlow?.UidCookieNames ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var manifest = new PluginManifest(
            "liveparse." + pluginId,
            source.Version.Trim(),
            PluginProtocolVersion.Current,
            source.DisplayName.Trim(),
            source.Entry,
            preloads,
            capabilities,
            BuildAuth(source, hosts, signalCookies, authRequired),
            new PluginNetworkManifest(hosts, []),
            [new PluginViewManifest("live", "live", "正在直播")],
            new PluginSourceConfigurationManifest(
                [
                    new PluginSourceFieldManifest(
                        "roomIds",
                        "直播间 ID",
                        "text",
                        true,
                        "每行一个或用逗号分隔，例如 74751；也可以直接粘贴直播间链接",
                        null),
                ],
                ["live"]),
            PluginManifestRules.LiveParseRuntime,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["roomUrlTemplate"] = roomUrlTemplate,
                ["liveStates"] = LiveStateBroadcasting,
            },
            ResolveIcon(sourceDirectory));

        return new LiveParsePackageConversion(manifest, scripts, dropped, hosts);
    }

    private static PluginAuthManifest? BuildAuth(
        LiveParseManifest source,
        IReadOnlyList<string> hosts,
        IReadOnlyList<string> signalCookies,
        bool requiresLogin)
    {
        var loginFlow = source.LoginFlow;
        if (loginFlow is null || string.IsNullOrWhiteSpace(loginFlow.LoginUrl))
            return null;

        var loginUrl = loginFlow.LoginUrl.Trim();
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var loginUri)
            || !string.Equals(loginUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginManifestException("LIVEPARSE_LOGIN_URL_INVALID", "The LiveParse login URL must be an absolute HTTPS URL.");
        }

        var cookieNames = signalCookies.Count > 0 ? signalCookies : ["SESSION"];
        var successPrefixes = new List<string>();
        var keyword = loginFlow.SuccessUrlKeyword?.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
            successPrefixes.Add("https://" + keyword.TrimStart('/'));

        if (successPrefixes.Count == 0)
        {
            // Without a success keyword the host falls back to the login origin: FeedPeek captures the
            // cookies as soon as the flow returns there with every signal cookie present.
            successPrefixes.Add(loginUri.GetLeftPart(UriPartial.Authority) + "/");
        }

        return new PluginAuthManifest(
            requiresLogin,
            "webview-cookie",
            ResolveCredentialDomains(source, loginUri, hosts),
            cookieNames,
            [],
            new PluginLoginFlowManifest(loginUrl, successPrefixes, cookieNames));
    }

    /// <summary>
    /// Credential scope: the domains the plugin manifest declares, the login host (a login flow may
    /// set cookies on a registration host outside those domains) and the audited request hosts, so that
    /// WebView capture visits every origin the plugin actually uses. Injection itself accepts
    /// subdomains, which is how the LiveParse host behaves.
    /// </summary>
    private static IReadOnlyList<string> ResolveCredentialDomains(
        LiveParseManifest source,
        Uri loginUri,
        IReadOnlyList<string> auditedHosts)
    {
        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in source.LoginFlow?.CookieDomains ?? [])
        {
            var value = candidate?.Trim().TrimStart('.').Trim();
            if (!string.IsNullOrWhiteSpace(value) && Uri.CheckHostName(value) == UriHostNameType.Dns)
                domains.Add(value);
        }

        if (Uri.CheckHostName(loginUri.IdnHost) == UriHostNameType.Dns)
            domains.Add(loginUri.IdnHost);
        foreach (var host in auditedHosts)
            domains.Add(host);
        return domains.ToArray();
    }

    private static IReadOnlyList<string> ResolveHosts(
        string pluginId,
        string sourceDirectory,
        IReadOnlyList<string> scripts,
        LiveParseManifest source)
    {
        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        if (KnownHosts.TryGetValue(pluginId, out var known))
        {
            foreach (var host in known)
                hosts.Add(host);
        }
        else
        {
            foreach (var script in scripts)
            {
                var path = Path.Combine(sourceDirectory, script);
                if (!File.Exists(path))
                    continue;
                foreach (Match match in HostPattern().Matches(File.ReadAllText(path)))
                {
                    var host = match.Groups[1].Value.ToLowerInvariant();
                    if (Uri.CheckHostName(host) == UriHostNameType.Dns)
                        hosts.Add(host);
                }
            }
        }

        if (source.LoginFlow?.SuccessUrlKeyword is { } keyword
            && Uri.TryCreate("https://" + keyword.TrimStart('/'), UriKind.Absolute, out var keywordUri)
            && Uri.CheckHostName(keywordUri.IdnHost) == UriHostNameType.Dns)
        {
            hosts.Add(keywordUri.IdnHost);
        }

        if (source.LoginFlow?.LoginUrl is { } loginUrl
            && Uri.TryCreate(loginUrl, UriKind.Absolute, out var loginUri)
            && Uri.CheckHostName(loginUri.IdnHost) == UriHostNameType.Dns)
        {
            hosts.Add(loginUri.IdnHost);
        }

        if (hosts.Count == 0)
            throw new PluginManifestException("LIVEPARSE_HOSTS_UNKNOWN", "No network host could be derived for this LiveParse plugin.");

        return hosts.ToArray();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    [GeneratedRegex("^[a-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SimplePluginIdPattern();

    [GeneratedRegex(@"https?://([A-Za-z0-9][A-Za-z0-9.-]*[A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    private sealed record LiveParseManifest(
        string? PluginId,
        string? Version,
        int ApiVersion,
        string? DisplayName,
        string? Entry,
        IReadOnlyList<string>? PreloadScripts,
        LiveParseAuth? Auth,
        LiveParseLoginFlow? LoginFlow,
        LiveParseHostBehavior? HostBehavior);

    private sealed record LiveParseAuth(bool Required);

    private sealed record LiveParseLoginFlow(
        string? Kind,
        string? LoginUrl,
        IReadOnlyList<string>? CookieDomains,
        IReadOnlyList<string>? AuthSignalCookies,
        IReadOnlyList<string>? UidCookieNames,
        string? SuccessUrlKeyword,
        string? WebsiteHost);

    private sealed record LiveParseHostBehavior(string? ExternalRoomUrlTemplate);
}
