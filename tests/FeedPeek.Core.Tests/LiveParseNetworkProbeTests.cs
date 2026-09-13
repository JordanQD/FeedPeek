using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginHost;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using System.Net.Http;
using System.Text.Json;

namespace FeedPeek.Core.Tests;

/// <summary>
/// M2 of <c>docs/design/liveparse-compat-plan.md</c>: drive the real Douyu package against the live
/// service and check that only actually broadcasting rooms reach the live strip. Opt-in via
/// FEEDPEEK_LIVEPARSE_NETWORK=1 plus FEEDPEEK_LIVEPARSE_REFERENCES; the report goes to
/// %TEMP%/feedpeek-liveparse-network.txt.
/// </summary>
public sealed class LiveParseNetworkProbeTests
{
    private const string DouyuPackage = "douyu-2.0.5";

    /// <summary>The room the user asked about; it is a re-broadcast and must not be shown.</summary>
    private const string RebroadcastRoom = "74751";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/114.0.0.0 Safari/537.36 Edg/114.0.1823.43";

    [Fact]
    public async Task Douyu_RealService_ShowsOnlyBroadcastingRooms()
    {
        var root = Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_REFERENCES");
        if (Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_NETWORK") != "1"
            || string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var report = await ProbeDouyuAsync(root);
        var text = string.Join(Environment.NewLine, report);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "feedpeek-liveparse-network.txt"),
            text);

        Assert.NotEmpty(report);
        Assert.DoesNotContain("verdict=FAIL", text, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ProbeDouyuAsync(string root)
    {
        var notes = new List<string>();
        var source = Path.Combine(root, DouyuPackage);
        if (!Directory.Exists(source))
            return ["douyu: MISSING DIRECTORY " + source];

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

        using var converted = new TempDirectory();
        var conversion = await LiveParsePackageConverter.TryConvertAsync(source, converted.Path);
        if (conversion is null)
            return ["douyu: NOT A LIVEPARSE PACKAGE"];

        notes.Add("converted pluginId=" + conversion.Manifest.PluginId);
        notes.Add("allowedHosts=" + string.Join(",", conversion.Manifest.Network.AllowedHosts));
        notes.Add("liveStates=" + conversion.Manifest.RuntimeOptions!["liveStates"]);

        var package = await new PluginManifestLoader().LoadAsync(converted.Path);
        await using var runtime = await JintPluginTransport.CreateAsync(package, CreateHost());

        var handshake = await InvokeAsync(runtime, package.Manifest.PluginId, PluginMethods.Handshake, new { });
        notes.Add("handshake=" + (handshake.Success ? "ok" : handshake.Error?.Code + ": " + handshake.Error?.Message));

        var liveRoom = await FindBroadcastingRoomAsync(http, notes);
        var pinned = liveRoom is null ? RebroadcastRoom : RebroadcastRoom + "," + liveRoom;
        notes.Add("pinned=" + pinned);

        var subscription = new FeedSubscription(
            package.Manifest.PluginId + ":probe:live",
            package.Manifest.PluginId,
            "probe-source",
            "live",
            "Douyu probe",
            Endpoint: null,
            CredentialProfileId: null,
            IsEnabled: true,
            TimeSpan.FromMinutes(15),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["roomIds"] = pinned });

        List<PluginLiveItem> items;
        try
        {
            // FeedPeek's own validation layer: URL shape, unique creator keys, required names.
            var service = new PluginCreatorService(
                package.Manifest.PluginId,
                new PluginClient(new TransportInvoker(runtime)));
            items = [.. await service.GetLiveAsync(subscription)];
            notes.Add("FeedPeek validation=ok(" + items.Count + " live items)");
            foreach (var item in items)
            {
                notes.Add("  - " + item.DisplayName + " | " + (item.Title ?? "(no title)") + " | " + item.Url
                    + " | avatar=" + (item.AvatarUrl ?? "(none)"));
            }
        }
        catch (Exception ex)
        {
            notes.Add("FeedPeek validation=FAILED " + ex.GetType().Name + ": " + ex.Message);
            notes.Add("verdict=FAIL(validation threw)");
            return notes;
        }

        var keptIds = items.Select(item => item.CreatorKey).ToHashSet(StringComparer.Ordinal);
        notes.Add("kept=" + string.Join(",", keptIds));
        notes.Add(RebroadcastRoom + " kept=" + (keptIds.Contains(RebroadcastRoom) ? "yes" : "no"));

        if (liveRoom is null)
        {
            notes.Add("verdict=SKIP(no broadcasting room found to verify the positive case)");
            return notes;
        }

        notes.Add(liveRoom + " kept=" + (keptIds.Contains(liveRoom) ? "yes" : "no"));
        notes.Add(keptIds.Contains(RebroadcastRoom) || !keptIds.Contains(liveRoom) || keptIds.Count != 1
            ? "verdict=FAIL(expected exactly the broadcasting room " + liveRoom + ")"
            : "verdict=PASS");
        return notes;
    }

    /// <summary>
    /// Picks a room that Douyu currently reports as broadcasting: show_status 1 without a video loop,
    /// which is the combination the plugin maps to liveState "1".
    /// </summary>
    private static async Task<string?> FindBroadcastingRoomAsync(HttpClient http, List<string> notes)
    {
        List<string> candidates;
        try
        {
            var json = await http.GetStringAsync("https://www.douyu.com/gapi/rkc/directory/mixList/2_1/1");
            using var document = JsonDocument.Parse(json);
            candidates = [.. document.RootElement
                .GetProperty("data")
                .GetProperty("rl")
                .EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetInt32() == 1)
                .Select(item => item.GetProperty("rid").ToString())
                .Take(15)];
        }
        catch (Exception ex)
        {
            notes.Add("discovery FAILED " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }

        notes.Add("candidates=" + candidates.Count);
        foreach (var candidate in candidates)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://www.douyu.com/betard/" + candidate);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.douyu.com/" + candidate);
                using var response = await http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("room", out var room))
                    continue;
                var showStatus = room.TryGetProperty("show_status", out var show) ? show.GetInt32() : -1;
                var videoLoop = room.TryGetProperty("videoLoop", out var loop) ? loop.GetInt32() : -1;
                if (showStatus == 1 && videoLoop == 0)
                {
                    notes.Add("broadcasting room=" + candidate);
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                notes.Add("probe room " + candidate + " FAILED " + ex.GetType().Name);
            }
        }

        notes.Add("broadcasting room: NOT FOUND in " + candidates.Count + " candidates");
        return null;
    }

    private static PluginHostServices CreateHost()
        => new(
            new PluginHttpBridge(),
            new InMemoryPluginCredentialStore(),
            NullPluginLogSink.Instance,
            new PermissiveAuthorizer());

    private static async Task<PluginResponseEnvelope<JsonElement>> InvokeAsync(
        IPluginTransport transport,
        string pluginId,
        string method,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var request = new PluginRequestEnvelope<JsonElement>(
            Guid.NewGuid().ToString("N"),
            PluginProtocolVersion.Current,
            pluginId,
            method,
            JsonSerializer.SerializeToElement(payload, PluginJson.SerializerOptions));
        var responseJson = await transport.InvokeAsync(
            JsonSerializer.Serialize(request, PluginJson.SerializerOptions),
            cancellationToken);
        return JsonSerializer.Deserialize<PluginResponseEnvelope<JsonElement>>(
            responseJson,
            PluginJson.SerializerOptions)!;
    }

    private sealed class TransportInvoker(IPluginTransport transport) : IPluginRequestInvoker
    {
        public Task<string> InvokeAsync(string pluginId, string requestJson, CancellationToken cancellationToken = default)
            => transport.InvokeAsync(requestJson, cancellationToken);
    }

    private sealed class PermissiveAuthorizer : IPluginInvocationAuthorizer
    {
        public Task<PluginHostContext> AuthorizeAsync(
            string pluginId,
            string requestId,
            string method,
            PluginInvocationContext? requestedContext,
            CancellationToken cancellationToken)
            => Task.FromResult(new PluginHostContext(
                pluginId,
                requestId,
                method,
                requestedContext?.CredentialProfileId));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FeedPeek.Tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(_path);

        public string Path => _path;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_path))
                    Directory.Delete(_path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
