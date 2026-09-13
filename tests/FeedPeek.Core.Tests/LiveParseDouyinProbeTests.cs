using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginHost;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using System.Text.Json;

namespace FeedPeek.Core.Tests;

/// <summary>
/// M4 of <c>docs/design/liveparse-compat-plan.md</c>: drive the real Douyin package against the live
/// service. M0 showed the bundled a_bogus SDK does not load in Jint, so this measures what the
/// plugin's own anonymous path achieves. Opt-in via FEEDPEEK_LIVEPARSE_NETWORK=1 plus
/// FEEDPEEK_LIVEPARSE_REFERENCES; the room can be pinned with FEEDPEEK_LIVEPARSE_DOUYIN_ROOM.
/// Two passes are reported: the shipping filter (liveStates=1) and an unfiltered diagnostic pass that
/// exposes the platform's raw liveState, so "off air" can be told apart from "broken".
/// </summary>
public sealed class LiveParseDouyinProbeTests
{
    private const string DouyinPackage = "douyin-2.0.5";
    private const string FallbackRoom = "180109685512";

    [Fact]
    public async Task Douyin_RealService_PinnedRoom()
    {
        var root = Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_REFERENCES");
        if (Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_NETWORK") != "1"
            || string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var room = Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_DOUYIN_ROOM");
        var roomId = string.IsNullOrWhiteSpace(room) ? FallbackRoom : room.Trim();
        var report = await ProbeAsync(root, roomId);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "feedpeek-liveparse-douyin.txt"),
            string.Join(Environment.NewLine, report));
        Assert.NotEmpty(report);
    }

    private static async Task<List<string>> ProbeAsync(string root, string roomId)
    {
        var notes = new List<string> { "roomId=" + roomId };
        var source = Path.Combine(root, DouyinPackage);
        if (!Directory.Exists(source))
            return ["douyin: MISSING DIRECTORY " + source];

        using var converted = new TempDirectory();
        var conversion = await LiveParsePackageConverter.TryConvertAsync(source, converted.Path);
        if (conversion is null)
            return ["douyin: NOT A LIVEPARSE PACKAGE"];

        notes.Add("pluginId=" + conversion.Manifest.PluginId);
        notes.Add("kept=" + string.Join("+", conversion.Manifest.PreloadScripts));
        notes.Add("dropped=" + conversion.DroppedPreloadScripts.Count);

        var http = new RecordingHttpBridge(new PluginHttpBridge());
        notes.Add("--- shipping filter (liveStates=1) ---");
        notes.AddRange(await RunPassAsync(converted.Path, roomId, http, passLabel: null));

        var manifestPath = Path.Combine(converted.Path, PluginManifestRules.FileName);
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        if (manifestJson.Contains("\"liveStates\":\"1\"", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(
                manifestPath,
                manifestJson.Replace("\"liveStates\":\"1\"", "\"liveStates\":\"0,1,2,3\"", StringComparison.Ordinal));
            notes.Add("--- diagnostic (liveStates=0,1,2,3) ---");
            notes.AddRange(await RunPassAsync(converted.Path, roomId, http, passLabel: "diagnostic"));
        }

        notes.Add("requests=" + http.Entries.Count);
        foreach (var entry in http.Entries)
            notes.Add("  " + entry);
        return notes;
    }

    private static async Task<List<string>> RunPassAsync(
        string packageRoot,
        string roomId,
        RecordingHttpBridge http,
        string? passLabel)
    {
        var lines = new List<string>();
        var package = await new PluginManifestLoader().LoadAsync(packageRoot);
        await using var runtime = await JintPluginTransport.CreateAsync(package, CreateHost(http));

        var handshake = await InvokeAsync(runtime, package.Manifest.PluginId, PluginMethods.Handshake, new { });
        lines.Add("handshake=" + (handshake.Success ? "ok" : handshake.Error?.Code + ": " + handshake.Error?.Message));

        // FeedPeek's own validation layer: URL shape, unique creator keys, required names.
        var subscription = new FeedSubscription(
            package.Manifest.PluginId + ":probe:live",
            package.Manifest.PluginId,
            "probe-source",
            "live",
            "Douyin probe",
            Endpoint: null,
            CredentialProfileId: null,
            IsEnabled: true,
            TimeSpan.FromMinutes(15),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["roomIds"] = roomId });
        try
        {
            var service = new PluginCreatorService(
                package.Manifest.PluginId,
                new PluginClient(new TransportInvoker(runtime)));
            var items = await service.GetLiveAsync(subscription);
            lines.Add("FeedPeek validation=ok(" + items.Count + " live items)");
            foreach (var item in items.Take(3))
            {
                var state = item.Metadata is not null && item.Metadata.TryGetValue("liveState", out var value)
                    ? value
                    : "(none)";
                lines.Add("  - " + item.DisplayName + " | " + (item.Title ?? "(no title)") + " | " + item.Url
                    + " | liveState=" + state
                    + " | broadcasting=" + (state == "1" ? "yes" : "no")
                    + " | avatar=" + (item.AvatarUrl ?? "(none)"));
            }
        }
        catch (Exception ex)
        {
            lines.Add("FeedPeek validation=FAILED " + ex.GetType().Name + ": " + ex.Message);
        }

        return lines;
    }

    private static PluginHostServices CreateHost(IPluginHttpBridge http)
        => new(
            http,
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

    /// <summary>Real network plus an audit trail of what the plugin actually requested.</summary>
    private sealed class RecordingHttpBridge(IPluginHttpBridge inner) : IPluginHttpBridge
    {
        public List<string> Entries { get; } = [];

        public async Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await inner.SendAsync(manifest, context, request, cancellationToken);
                var body = (response.Body ?? string.Empty).Replace("\n", " ", StringComparison.Ordinal);
                if (Entries.Count < 12)
                {
                    Entries.Add(request.Method + " " + Shorten(request.Url, 130)
                        + " -> " + response.Status + " body=" + Shorten(body, 90));
                }

                return response;
            }
            catch (Exception ex)
            {
                if (Entries.Count < 12)
                    Entries.Add(request.Method + " " + Shorten(request.Url, 130) + " -> FAILED " + ex.GetType().Name);
                throw;
            }
        }

        private static string Shorten(string value, int max)
            => value.Length <= max ? value : value[..max] + "...";
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
