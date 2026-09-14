using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using System.Text.Json;

namespace FeedPeek.Core.Tests;

public sealed class LiveParseCompatTests
{
    [Fact]
    public async Task Converter_TurnsALiveParseFolderIntoAnInstallablePackage()
    {
        using var source = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "manifest.json"), """
            {
              "pluginId": "demo",
              "version": "9.9.9",
              "apiVersion": 1,
              "displayName": "Demo Live",
              "liveTypes": ["9"],
              "entry": "index.js",
              "preloadScripts": ["login.js", "danmaku.js", "webmssdk.js"],
              "auth": { "required": true, "credentialKinds": ["cookie"] },
              "loginFlow": {
                "kind": "webview",
                "loginURL": "https://passport.demo.test/login",
                "cookieDomains": ["demo.test"],
                "authSignalCookies": ["SESSDATA"],
                "uidCookieNames": ["DedeUserID"],
                "successURLKeyword": "live.demo.test/area-tags",
                "websiteHost": "demo.test"
              },
              "capabilities": { "rooms": { "status": "available" } },
              "hostBehavior": { "externalRoomURLTemplate": "https://live.demo.test/{roomId}" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(source.Path, "index.js"), """
            globalThis.LiveParsePlugin = {
              apiVersion: 1,
              async getRoomDetail(payload) {
                await Host.http.request({ platformId: 'demo', request: { url: 'https://api.demo.test/room', method: 'GET' } });
                return { roomId: String(payload.roomId) };
              }
            };
            """);
        await File.WriteAllTextAsync(Path.Combine(source.Path, "login.js"), "globalThis.demoLogin = {};");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "danmaku.js"), "globalThis.demoDanmaku = {};");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "webmssdk.js"), "globalThis.demoSigner = {};");

        Directory.CreateDirectory(Path.Combine(source.Path, "assets"));
        await File.WriteAllBytesAsync(Path.Combine(source.Path, "assets", "tv_demo.png"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source.Path, "assets", "live_card_demo_dark.png"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source.Path, "assets", "live_card_demo.png"), [1, 2, 3]);

        Assert.True(LiveParsePackageConverter.CanConvert(source.Path));

        using var target = TemporaryDirectory.Create();
        var conversion = await LiveParsePackageConverter.TryConvertAsync(source.Path, target.Path);
        Assert.NotNull(conversion);

        var manifest = conversion!.Manifest;
        Assert.Equal("liveparse.demo", manifest.PluginId);
        Assert.Equal(PluginManifestRules.LiveParseRuntime, PluginManifestRules.ResolveRuntime(manifest));
        Assert.Equal(["feed.live", "source.configuration", "account.auth"], manifest.Capabilities);
        Assert.Equal("index.js", manifest.Entry);
        Assert.Equal(["login.js"], manifest.PreloadScripts);
        Assert.Contains("danmaku.js", conversion.DroppedPreloadScripts);
        Assert.Contains("webmssdk.js", conversion.DroppedPreloadScripts);
        Assert.Equal("live", Assert.Single(manifest.Views).Id);
        Assert.Equal("live", Assert.Single(manifest.SourceConfiguration!.DefaultFeedIds));
        Assert.True(Assert.Single(manifest.SourceConfiguration.Fields).Required);
        Assert.Equal("roomIds", Assert.Single(manifest.SourceConfiguration.Fields).Key);
        Assert.Equal("https://live.demo.test/{roomId}", manifest.RuntimeOptions!["roomUrlTemplate"]);

        // The platform's own list icon wins over the oversized TV artwork, and *_dark art is ignored.
        Assert.Equal("assets/live_card_demo.png", manifest.Icon);

        // Hosts come from the script literals (plus the login and success URLs) for unknown plugins.
        Assert.Contains("api.demo.test", manifest.Network.AllowedHosts);
        Assert.Contains("passport.demo.test", manifest.Network.AllowedHosts);
        Assert.Contains("live.demo.test", manifest.Network.AllowedHosts);

        Assert.Equal("webview-cookie", manifest.Auth!.Kind);
        Assert.True(manifest.Auth.Required);
        Assert.Equal("https://passport.demo.test/login", manifest.Auth.LoginFlow!.LoginUrl);
        Assert.Equal(["https://live.demo.test/area-tags"], manifest.Auth.LoginFlow.SuccessUrlPrefixes);
        Assert.Equal(["SESSDATA", "DedeUserID"], manifest.Auth.LoginFlow.SuccessCookieNames);

        // The written package passes the normal loader and runs through the dialect.
        var package = await new PluginManifestLoader().LoadAsync(target.Path);
        Assert.Equal("liveparse.demo", package.Manifest.PluginId);

        await using var runtime = await JintPluginTransport.CreateAsync(package, CreateHost());
        var handshake = await InvokeAsync(runtime, "liveparse.demo", PluginMethods.Handshake, new { });
        Assert.True(handshake.Success);
        Assert.Equal("liveparse.demo", handshake.Result.GetProperty("providerId").GetString());
    }

    [Fact]
    public async Task Converter_KeepsLoginOptionalForVerifiedAnonymousLivePlatforms()
    {
        using var source = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "manifest.json"), """
            {
              "pluginId": "douyin",
              "version": "2.0.5",
              "apiVersion": 1,
              "displayName": "Douyin",
              "entry": "index.js",
              "preloadScripts": ["lp_plugin_douyin_1.0.13_login.js"],
              "auth": { "required": true, "credentialKinds": ["cookie"] },
              "loginFlow": {
                "kind": "webview",
                "loginURL": "https://www.douyin.com/user/self",
                "cookieDomains": ["douyin.com"],
                "authSignalCookies": ["sessionid"],
                "uidCookieNames": ["uid_tt"]
              },
              "hostBehavior": { "externalRoomURLTemplate": "https://live.douyin.com/{roomId}" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(source.Path, "index.js"), "globalThis.LiveParsePlugin = { apiVersion: 1, async getRoomDetail() { return {}; } };");
        await File.WriteAllTextAsync(Path.Combine(source.Path, "lp_plugin_douyin_1.0.13_login.js"), "globalThis.dyLogin = {};");

        using var target = TemporaryDirectory.Create();
        var conversion = await LiveParsePackageConverter.TryConvertAsync(source.Path, target.Path);
        Assert.NotNull(conversion);

        var auth = conversion!.Manifest.Auth;
        Assert.NotNull(auth);
        // Login is offered but no longer a prerequisite for an anonymous live source.
        Assert.False(auth!.Required);
        Assert.NotNull(auth.LoginFlow);
        Assert.Contains("account.auth", conversion.Manifest.Capabilities);
    }

    /// <summary>
    /// Opt-in conformance probe (M0 of <c>docs/design/liveparse-compat-plan.md</c>). Point
    /// FEEDPEEK_LIVEPARSE_REFERENCES at the LiveParse references folder to load the real third-party
    /// packages; without it the probe is a no-op so the suite stays hermetic. The report is written to
    /// %TEMP%/feedpeek-liveparse-probe.txt.
    /// </summary>
    [Fact]
    public async Task ReferencePackages_ConvertAndInitialize()
    {
        var root = Environment.GetEnvironmentVariable("FEEDPEEK_LIVEPARSE_REFERENCES");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        var pinnedRooms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["douyu-2.0.5"] = "74751",
            ["bilibili-2.0.0"] = "1",
            ["douyin-2.0.5"] = "1",
        };
        var report = new List<string>();
        foreach (var name in new[] { "douyu-2.0.5", "bilibili-2.0.0", "douyin-2.0.5" })
        {
            var directory = Path.Combine(root, name);
            report.Add(Directory.Exists(directory)
                ? await ProbeAsync(name, directory, pinnedRooms[name])
                : name + ": MISSING DIRECTORY");
        }

        var reportPath = Path.Combine(Path.GetTempPath(), "feedpeek-liveparse-probe.txt");
        await File.WriteAllTextAsync(reportPath, string.Join(Environment.NewLine, report));
        Assert.Equal(3, report.Count);
    }

    private static async Task<string> ProbeAsync(string name, string source, string roomId)
    {
        using var converted = TemporaryDirectory.Create();
        var notes = new List<string>();
        try
        {
            var conversion = await LiveParsePackageConverter.TryConvertAsync(source, converted.Path);
            if (conversion is null)
                return name + ": NOT A LIVEPARSE PACKAGE";
            notes.Add("pluginId=" + conversion.Manifest.PluginId);
            notes.Add("hosts=" + conversion.Hosts.Count);
            notes.Add("kept=" + string.Join('+', conversion.Manifest.PreloadScripts));
            notes.Add("dropped=" + conversion.DroppedPreloadScripts.Count);
            notes.Add("auth=" + (conversion.Manifest.Auth?.Required == true ? "required" : "none"));
            notes.Add("icon=" + (conversion.Manifest.Icon ?? "none"));
        }
        catch (Exception ex)
        {
            return name + ": CONVERT FAILED " + ex.GetType().Name + ": " + ex.Message;
        }

        PluginPackage package;
        try
        {
            package = await new PluginManifestLoader().LoadAsync(converted.Path);
            notes.Add("manifest=ok");
        }
        catch (Exception ex)
        {
            return name + ": MANIFEST FAILED " + ex.GetType().Name + ": " + ex.Message;
        }

        JintPluginTransport runtime;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            runtime = await JintPluginTransport.CreateAsync(package, CreateHost());
        }
        catch (Exception ex)
        {
            return name + ": INIT FAILED after " + started.ElapsedMilliseconds + "ms "
                + ex.GetType().Name + ": " + ex.Message;
        }

        notes.Add("init=ok(" + started.ElapsedMilliseconds + "ms)");
        await using (runtime)
        {
            var handshake = await InvokeAsync(runtime, package.Manifest.PluginId, PluginMethods.Handshake, new { });
            notes.Add(handshake.Success ? "handshake=ok" : "handshake=" + handshake.Error?.Code);

            var created = await InvokeAsync(runtime, package.Manifest.PluginId, PluginMethods.SourceCreate, new
            {
                context = new PluginInvocationContext(
                    "probe-subscription", package.Manifest.PluginId, "probe-source", "live", null, null),
                displayName = name,
                options = new Dictionary<string, string>(StringComparer.Ordinal) { ["roomIds"] = roomId },
            });
            notes.Add(created.Success ? "source.create=ok" : "source.create=" + created.Error?.Code);

            var live = await InvokeAsync(runtime, package.Manifest.PluginId, PluginMethods.FeedGetLive, new
            {
                context = new PluginInvocationContext(
                    "probe-subscription",
                    package.Manifest.PluginId,
                    "probe-source",
                    "live",
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["roomIds"] = roomId }),
            });
            if (live.Success)
            {
                var page = live.Result.Deserialize<PluginLivePage>(PluginJson.SerializerOptions)!;
                notes.Add("getLive=ok(" + page.Items.Count + " rooms)");
            }
            else
            {
                notes.Add("getLive=" + live.Error?.Code + ": " + (live.Error?.Message ?? string.Empty));
            }
        }

        return name + ": " + string.Join(" | ", notes);
    }

    private static PluginHostServices CreateHost()
        => new(
            new ProbeHttpBridge(),
            new InMemoryPluginCredentialStore(),
            NullPluginLogSink.Instance,
            new ProbeAuthorizer());

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

    /// <summary>Answers every request with an empty JSON object so the probe never touches the network.</summary>
    private sealed class ProbeHttpBridge : IPluginHttpBridge
    {
        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HostHttpResponse(
                200,
                request.Url,
                new Dictionary<string, string>(),
                "{}",
                "utf8"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FeedPeek.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ProbeAuthorizer : IPluginInvocationAuthorizer
    {
        public Task<PluginHostContext> AuthorizeAsync(
            string pluginId,
            string requestId,
            string method,
            PluginInvocationContext? requestedContext,
            CancellationToken cancellationToken)
            => Task.FromResult(new PluginHostContext(pluginId, requestId, method, null));
    }
}
