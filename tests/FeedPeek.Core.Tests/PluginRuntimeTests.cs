using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginRuntime.Discovery;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeedPeek.Core.Tests;

public sealed class PluginRuntimeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("{}", false)]
    [InlineData("{\"autoAddToCustom\":false}", false)]
    [InlineData("{\"autoAddToCustom\":true}", true)]
    public async Task ManifestLoader_UsesNativeNotificationOptIn(string? notifications, bool expected)
    {
        using var temporary = CopyFixturePlugin();
        var path = Path.Combine(temporary.Path, PluginManifestRules.FileName);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        if (notifications is not null)
            manifest["notifications"] = JsonNode.Parse(notifications);
        await File.WriteAllTextAsync(path, manifest.ToJsonString());

        var package = await new PluginManifestLoader().LoadAsync(temporary.Path);
        Assert.Equal(expected, PluginManifestRules.ShouldAutoAddToCustomNotifications(package.Manifest));
    }

    [Fact]
    public async Task ManifestLoader_RejectsNonBooleanNotificationOptIn()
    {
        using var temporary = CopyFixturePlugin();
        var path = Path.Combine(temporary.Path, PluginManifestRules.FileName);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        manifest["notifications"] = JsonNode.Parse("{\"autoAddToCustom\":\"true\"}");
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        var error = await Assert.ThrowsAsync<PluginManifestException>(
            () => new PluginManifestLoader().LoadAsync(temporary.Path));
        Assert.Equal("MANIFEST_INVALID_JSON", error.Code);
    }

    [Fact]
    public async Task ManifestLoader_ValidatesPackageAndRejectsTraversal()
    {
        var loader = new PluginManifestLoader();
        var package = await loader.LoadAsync(FixturePluginRoot);

        Assert.Equal("mock.feed", package.Manifest.PluginId);
        Assert.Equal(64, package.PackageHash.Length);

        using var temporary = CopyFixturePlugin();
        var manifestPath = Path.Combine(temporary.Path, PluginManifestRules.FileName);
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"index.js\"", "\"../index.js\"", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<PluginManifestException>(() => loader.LoadAsync(temporary.Path));
        Assert.Equal("PLUGIN_PATH_TRAVERSAL", error.Code);

        using var missingViews = CopyFixturePlugin();
        var missingViewsPath = Path.Combine(missingViews.Path, PluginManifestRules.FileName);
        var manifestNode = JsonNode.Parse(await File.ReadAllTextAsync(missingViewsPath))!.AsObject();
        manifestNode.Remove("views");
        await File.WriteAllTextAsync(missingViewsPath, manifestNode.ToJsonString());
        var missingField = await Assert.ThrowsAsync<PluginManifestException>(() => loader.LoadAsync(missingViews.Path));
        Assert.Equal("MANIFEST_FIELD_MISSING", missingField.Code);
    }

    [Fact]
    public async Task ManifestLoader_ValidatesTheOptionalPluginIcon()
    {
        var loader = new PluginManifestLoader();
        using var root = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "index.js"), "globalThis.FeedPeekPlugin = { apiVersion: 1 };");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "icon.txt"), "not an image");

        var manifestPath = Path.Combine(root.Path, PluginManifestRules.FileName);
        await File.WriteAllTextAsync(manifestPath, IconManifest("icon.txt"));
        var unsupported = await Assert.ThrowsAsync<PluginManifestException>(() => loader.LoadAsync(root.Path));
        Assert.Equal("ICON_EXTENSION_INVALID", unsupported.Code);

        await File.WriteAllTextAsync(manifestPath, IconManifest("../icon.png"));
        var traversal = await Assert.ThrowsAsync<PluginManifestException>(() => loader.LoadAsync(root.Path));
        Assert.Equal("PLUGIN_PATH_TRAVERSAL", traversal.Code);

        // A valid icon resolves, and the manifest keeps the package-relative path.
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "icon.png"), [1, 2, 3]);
        await File.WriteAllTextAsync(manifestPath, IconManifest("icon.png"));
        var package = await loader.LoadAsync(root.Path);
        Assert.Equal("icon.png", package.Manifest.Icon);
    }

    private static string IconManifest(string icon) => $$"""
        {
          "pluginId": "demo.icon",
          "version": "1.0.0",
          "apiVersion": 1,
          "displayName": "Demo Icon",
          "entry": "index.js",
          "preloadScripts": [],
          "capabilities": [],
          "auth": null,
          "network": { "allowedHosts": ["api.demo.test"], "allowRedirectHosts": [] },
          "views": [{ "id": "feed", "kind": "feed", "title": "更新" }],
          "icon": "{{icon}}"
        }
        """;

    [Fact]
    public async Task ManifestLoader_RequiresHttpsForDeclaredWebsite()
    {
        using var temporary = CopyFixturePlugin();
        var manifestPath = Path.Combine(temporary.Path, PluginManifestRules.FileName);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["website"] = "https://www.example.test/";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var loader = new PluginManifestLoader();
        var package = await loader.LoadAsync(temporary.Path);
        Assert.Equal("https://www.example.test/", package.Manifest.Website);

        manifest["website"] = "file:///C:/private.txt";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var invalid = await Assert.ThrowsAsync<PluginManifestException>(() => loader.LoadAsync(temporary.Path));
        Assert.Equal("URL_INVALID", invalid.Code);
    }

    [Fact]
    public async Task Discovery_DisablesValidPluginAndIsolatesBrokenPlugin()
    {
        using var root = TemporaryDirectory.Create();
        CopyDirectory(FixturePluginRoot, Path.Combine(root.Path, "valid"));
        var broken = Path.Combine(root.Path, "broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, PluginManifestRules.FileName), "{ invalid json");

        var results = await new PluginDiscoveryService().DiscoverAsync(
            root.Path,
            new HashSet<string>(StringComparer.Ordinal) { "mock.feed" });

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.Package?.Manifest.PluginId == "mock.feed" && result.IsDisabled);
        Assert.Contains(results, result => result.ErrorCode == "MANIFEST_INVALID_JSON");
    }

    [Fact]
    public async Task Catalog_LoadsAndDisablesMockPlugin()
    {
        var host = CreateHost();
        await using var catalog = new PluginRuntimeCatalog(host);

        var results = await catalog.LoadAsync(Path.GetDirectoryName(FixturePluginRoot)!);

        Assert.Contains(results, result => result.IsAvailable && result.Package?.Manifest.PluginId == "mock.feed");
        Assert.Contains("mock.feed", catalog.LoadedPluginIds);
        await catalog.DisableAsync("mock.feed");
        Assert.DoesNotContain("mock.feed", catalog.LoadedPluginIds);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.InvokeAsync("mock.feed", "{}"));

        await catalog.LoadAsync(
            Path.GetDirectoryName(FixturePluginRoot)!,
            new HashSet<string>(StringComparer.Ordinal) { "mock.feed" });
        Assert.DoesNotContain("mock.feed", catalog.LoadedPluginIds);
    }

    [Fact]
    public async Task Catalog_IsolatesInitializationFailureAndSupportsTransportFactoryInjection()
    {
        using var root = TemporaryDirectory.Create();
        CopyDirectory(FixturePluginRoot, Path.Combine(root.Path, "valid"));
        var brokenPath = Path.Combine(root.Path, "broken");
        CopyDirectory(FixturePluginRoot, brokenPath);
        var manifestPath = Path.Combine(brokenPath, PluginManifestRules.FileName);
        var manifest = (await File.ReadAllTextAsync(manifestPath)).Replace("mock.feed", "broken.feed", StringComparison.Ordinal);
        await File.WriteAllTextAsync(manifestPath, manifest);
        await File.WriteAllTextAsync(Path.Combine(brokenPath, "index.js"), "globalThis.FeedPeekPlugin = {");

        var host = CreateHost();
        await using (var catalog = new PluginRuntimeCatalog(host))
        {
            var results = await catalog.LoadAsync(root.Path);
            Assert.Contains("mock.feed", catalog.LoadedPluginIds);
            Assert.Contains(results, result => result.ErrorCode == "PLUGIN_INITIALIZATION_FAILED");
        }

        var factory = new RecordingTransportFactory();
        await using (var catalog = new PluginRuntimeCatalog(host, transportFactory: factory))
        {
            var results = await catalog.LoadAsync(root.Path);
            Assert.Equal(2, factory.CreatedPluginIds.Count);
            Assert.Equal(2, results.Count(result => result.IsAvailable));
            Assert.Equal("{}", await catalog.InvokeAsync("mock.feed", "{}"));
        }
        Assert.All(factory.CreatedTransports, transport => Assert.True(transport.IsDisposed));
    }

    [Fact]
    public async Task MockPlugin_ReturnsFeedPaginationActionsAndAsyncHostResults()
    {
        var credentials = new InMemoryPluginCredentialStore();
        var host = CreateHost(credentials: credentials);
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        await using var runtime = await JintPluginTransport.CreateAsync(package, host);

        var first = await InvokeAsync(runtime, PluginMethods.FeedGetUpdates, FeedPayload(null, 2));
        Assert.True(first.Success);
        var firstPage = first.Result.Deserialize<PluginFeedPage>(PluginJson.SerializerOptions)!;
        Assert.Equal(new[] { "item-1", "item-2" }, firstPage.Items.Select(item => item.ItemId));
        Assert.Equal("2", firstPage.NextCursor);
        Assert.True(firstPage.HasMore);

        var second = await InvokeAsync(runtime, PluginMethods.FeedGetMoreUpdates, FeedPayload(firstPage.NextCursor, 2));
        var secondPage = second.Result.Deserialize<PluginFeedPage>(PluginJson.SerializerOptions)!;
        Assert.Equal("item-3", Assert.Single(secondPage.Items).ItemId);
        Assert.False(secondPage.HasMore);

        var actions = await InvokeAsync(runtime, PluginMethods.ItemGetActions, new
        {
            context = InvocationContext(),
            itemId = "item-1",
        });
        var action = actions.Result.Deserialize<ActionDescriptor[]>(PluginJson.SerializerOptions)!.Single();
        Assert.Equal("mock.save", action.ActionId);
        Assert.Equal("Save", action.FallbackText);
        Assert.Equal("none", action.DangerLevel);
        Assert.False(action.RequiresConfirmation);
        Assert.False(action.IsRepeatable);
        Assert.Throws<InvalidDataException>(() => PluginActionValidator.Validate([
            action with { DangerLevel = "unknown" },
        ]));

        var executed = await InvokeAsync(runtime, PluginMethods.ItemExecuteAction, new
        {
            context = InvocationContext(),
            actionId = "mock.save",
            payload = new { itemId = "item-1" },
        });
        Assert.True(executed.Result.GetProperty("completed").GetBoolean());
        Assert.Equal("mock.save", executed.Result.GetProperty("savedAction").GetString());

        var spoofed = await InvokeAsync(runtime, PluginMethods.ItemExecuteAction, new
        {
            context = InvocationContext() with { CredentialProfileId = "credential-other" },
            actionId = "mock.save",
            payload = new { itemId = "item-1" },
        });
        Assert.False(spoofed.Success);
        Assert.Equal("CREDENTIAL_SCOPE_FORBIDDEN", spoofed.Error?.Code);
    }

    [Fact]
    public async Task StructuredPluginError_DoesNotBreakFollowingInvocation()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var host = CreateHost();
        await using var runtime = await JintPluginTransport.CreateAsync(package, host);

        var failed = await InvokeAsync(runtime, PluginMethods.GetStatus, new { mode = "throw" });
        Assert.False(failed.Success);
        Assert.Equal("MOCK_FAILURE", failed.Error?.Code);

        var handshake = await InvokeAsync(runtime, PluginMethods.Handshake, new { });
        Assert.True(handshake.Success);
        Assert.True(handshake.Result.GetProperty("preloaded").GetBoolean());
    }

    [Fact]
    public async Task InvalidContextAndAuthorizerFailure_ReturnStandardErrors()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        await using (var runtime = await JintPluginTransport.CreateAsync(package, CreateHost()))
        {
            var invalid = await InvokeAsync(runtime, PluginMethods.FeedGetUpdates, new { context = "invalid" });
            Assert.False(invalid.Success);
            Assert.Equal("REQUEST_CONTEXT_INVALID", invalid.Error?.Code);
        }

        var failingHost = new PluginHostServices(
            new MockHttpBridge(),
            new InMemoryPluginCredentialStore(),
            NullPluginLogSink.Instance,
            new ThrowingInvocationAuthorizer());
        await using var failingRuntime = await JintPluginTransport.CreateAsync(package, failingHost);
        var denied = await InvokeAsync(failingRuntime, PluginMethods.Handshake, new { });
        Assert.False(denied.Success);
        Assert.Equal("HOST_AUTHORIZATION_ERROR", denied.Error?.Code);
    }

    [Fact]
    public async Task ExecutionBudgets_StopInfiniteLoopAndMemoryGrowth()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var host = CreateHost();
        var spinOptions = new PluginRuntimeOptions
        {
            InvocationTimeout = TimeSpan.FromSeconds(1),
            PromiseTimeout = TimeSpan.FromSeconds(1),
            MaxStatements = 20_000,
            MemoryLimitBytes = 8 * 1024 * 1024,
        };
        await using var spinRuntime = await JintPluginTransport.CreateAsync(package, host, spinOptions);

        var spin = await InvokeAsync(spinRuntime, PluginMethods.GetStatus, new { mode = "spin" });
        Assert.False(spin.Success);
        Assert.Contains(spin.Error?.Code, new[] { "PLUGIN_BUDGET_EXCEEDED", "PLUGIN_TIMEOUT" });

        var memoryOptions = new PluginRuntimeOptions
        {
            InvocationTimeout = TimeSpan.FromSeconds(10),
            PromiseTimeout = TimeSpan.FromSeconds(10),
            MaxStatements = 5_000_000,
            MemoryLimitBytes = 4 * 1024 * 1024,
        };
        await using var memoryRuntime = await JintPluginTransport.CreateAsync(package, host, memoryOptions);
        var allocation = await InvokeAsync(memoryRuntime, PluginMethods.GetStatus, new { mode = "allocate" });
        Assert.False(allocation.Success);
        Assert.Equal("PLUGIN_MEMORY_LIMIT", allocation.Error?.Code);
    }

    [Fact]
    public async Task CancellationFlowsIntoAsyncHostCall()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var host = CreateHost(http: new BlockingHttpBridge());
        await using var runtime = await JintPluginTransport.CreateAsync(package, host);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InvokeAsync(runtime, PluginMethods.FeedGetUpdates, FeedPayload(null, 2), cancellation.Token));
    }

    [Fact]
    public async Task HostTaskThatIgnoresCancellation_QuarantinesRuntimeWithoutHanging()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var options = new PluginRuntimeOptions { HostTaskDrainTimeout = TimeSpan.FromMilliseconds(50) };
        await using var runtime = await JintPluginTransport.CreateAsync(
            package,
            CreateHost(http: new IgnoringCancellationHttpBridge()),
            options);

        var first = await InvokeAsync(runtime, PluginMethods.GetStatus, new { mode = "fireAndForget" });
        Assert.True(first.Success);
        var quarantined = await InvokeAsync(runtime, PluginMethods.Handshake, new { });
        Assert.False(quarantined.Success);
        Assert.Equal("PLUGIN_RUNTIME_UNAVAILABLE", quarantined.Error?.Code);
    }

    [Fact]
    public async Task HttpBridge_RejectsUndeclaredHostsAndReservedHeaders()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        using var bridge = new PluginHttpBridge();
        var context = new PluginHostContext("mock.feed", "request-01", PluginMethods.FeedGetUpdates, null);

        var hostError = await Assert.ThrowsAsync<PluginHostBridgeException>(() => bridge.SendAsync(
            package.Manifest,
            context,
            new HostHttpRequest("https://outside.example.test/", "GET", null, null, null, null, null),
            CancellationToken.None));
        Assert.Equal("HTTP_HOST_FORBIDDEN", hostError.Code);

        var headerError = await Assert.ThrowsAsync<PluginHostBridgeException>(() => bridge.SendAsync(
            package.Manifest,
            context,
            new HostHttpRequest(
                "https://api.example.test/",
                "GET",
                new Dictionary<string, string> { ["Cookie"] = "secret" },
                null,
                null,
                null,
                null),
            CancellationToken.None));
        Assert.Equal("HTTP_HEADER_FORBIDDEN", headerError.Code);
    }

    [Fact]
    public void LogRedactor_RemovesHeaderQueryAndStructuredSecrets()
    {
        var redacted = PluginLogRedactor.Redact(
            "Authorization: Bearer actual-token\nCookie: a=one; b=two\nhttps://example.test/?api_key=three&safe=yes password=four");

        Assert.DoesNotContain("actual-token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("a=one", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("b=two", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("three", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("four", redacted, StringComparison.Ordinal);
        var values = PluginLogRedactor.Redact(new Dictionary<string, string>
        {
            ["x-api-key"] = "five",
            ["message"] = "safe",
        });
        Assert.Equal("[REDACTED]", values!["x-api-key"]);
        Assert.Equal("safe", values["message"]);
    }

    [Fact]
    public async Task ManifestLoader_RejectsUnsupportedRuntime()
    {
        using var temporary = CopyFixturePlugin();
        var manifestPath = Path.Combine(temporary.Path, PluginManifestRules.FileName);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        node["runtime"] = "unknown-v1";
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        var error = await Assert.ThrowsAsync<PluginManifestException>(
            () => new PluginManifestLoader().LoadAsync(temporary.Path));
        Assert.Equal("RUNTIME_UNSUPPORTED", error.Code);
    }

    [Fact]
    public async Task ManifestLoader_RequiresALiveViewForLiveParseRuntime()
    {
        using var temporary = CopyFixturePlugin(LiveFixturePluginRoot);
        var manifestPath = Path.Combine(temporary.Path, PluginManifestRules.FileName);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        node["capabilities"] = new JsonArray("feed.updates", "source.configuration");
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        var error = await Assert.ThrowsAsync<PluginManifestException>(
            () => new PluginManifestLoader().LoadAsync(temporary.Path));
        Assert.Equal("RUNTIME_VIEW_UNSUPPORTED", error.Code);
    }

    [Fact]
    public async Task LiveParsePlugin_CreatesRoomSourceWithNameAndAvatarEvenWhenOffline()
    {
        var package = await new PluginManifestLoader().LoadAsync(LiveFixturePluginRoot);
        var http = new RecordingLiveHttpBridge();
        await using var runtime = await JintPluginTransport.CreateAsync(package, CreateHost(http: http));

        async Task<PluginSourceDefinition> CreateRoomAsync(string roomId)
        {
            var created = await InvokeAsync(runtime, LivePluginId, PluginMethods.SourceCreate, new
            {
                context = LiveInvocationContext(),
                displayName = "Mock Live · " + roomId,
                options = new Dictionary<string, string> { ["roomIds"] = roomId },
            });
            Assert.True(created.Success, created.Error?.Code + ": " + created.Error?.Message);
            return created.Result.Deserialize<PluginSourceDefinition>(PluginJson.SerializerOptions)!;
        }

        var live = await CreateRoomAsync("12345");
        Assert.Equal("Streamer", live.DisplayName);
        Assert.Equal("12345", live.Options!["roomIds"]);
        Assert.Equal("https://i0.example.test/face.jpg", live.Options["source.icon"]);

        var offline = await CreateRoomAsync("7777");
        Assert.Equal("Room 7777", offline.DisplayName);
        Assert.False(offline.Options!.ContainsKey("source.icon"));

        var unavailable = await CreateRoomAsync("404");
        Assert.Equal("Mock Live · 404", unavailable.DisplayName);
        Assert.Equal("404", unavailable.Options!["roomIds"]);
        Assert.Equal(2, http.RequestedUrls.Count);
    }

    [Fact]
    public async Task LiveParsePlugin_ServesLiveRoomsThroughTheDialect()
    {
        var package = await new PluginManifestLoader().LoadAsync(LiveFixturePluginRoot);
        Assert.Equal(PluginManifestRules.LiveParseRuntime, PluginManifestRules.ResolveRuntime(package.Manifest));
        // Previously converted packages without a policy still use the compatibility-room default.
        Assert.Null(package.Manifest.Notifications);
        Assert.True(PluginManifestRules.ShouldAutoAddToCustomNotifications(package.Manifest));

        var http = new RecordingLiveHttpBridge();
        await using var runtime = await JintPluginTransport.CreateAsync(package, CreateHost(http: http));

        var handshake = await InvokeAsync(runtime, LivePluginId, PluginMethods.Handshake, new { });
        Assert.True(handshake.Success);
        Assert.Equal(LivePluginId, handshake.Result.GetProperty("providerId").GetString());

        var created = await InvokeAsync(runtime, LivePluginId, PluginMethods.SourceCreate, new
        {
            context = LiveInvocationContext(),
            displayName = "Mock category",
            options = new { categoryId = "100" },
        });
        Assert.True(created.Success);
        var definition = created.Result.Deserialize<PluginSourceDefinition>(PluginJson.SerializerOptions)!;
        Assert.Equal(LivePluginId, definition.ProviderId);
        Assert.Equal("live", Assert.Single(definition.Feeds).FeedId);
        Assert.Equal("live", Assert.Single(definition.Feeds).Kind);
        Assert.True(Assert.Single(definition.Feeds).SupportsNotifications);

        var live = await InvokeAsync(runtime, LivePluginId, PluginMethods.FeedGetLive, new
        {
            context = LiveInvocationContext() with
            {
                Options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // A pasted link and a duplicate id fold into unique pinned rooms; 9999 is a
                    // re-broadcast and 7777 is offline, so both must be filtered out.
                    ["roomIds"] = "12345, https://live.example.test/room/9999, 12345, 7777",
                },
            },
        });
        Assert.True(live.Success, live.Error?.Code + ": " + live.Error?.Message);
        var room = Assert.Single(live.Result.Deserialize<PluginLivePage>(PluginJson.SerializerOptions)!.Items);
        Assert.Equal("12345", room.CreatorKey);
        Assert.Equal("Streamer", room.DisplayName);
        Assert.Equal("Mock stream", room.Title);
        Assert.Equal("https://live.example.test/room/12345", room.Url);
        Assert.Equal("https://i0.example.test/face.jpg", room.AvatarUrl);
        Assert.Equal("https://i0.example.test/cover.jpg", room.CoverUrl);
        Assert.Equal(12_000, room.ViewerCount);

        // The LiveParse call shape was translated into FeedPeek host requests: seconds to
        // milliseconds, headers passed through, one detail request per pinned room.
        Assert.Equal(3, http.RequestedUrls.Count);
        Assert.Contains("https://live.example.test/room?id=12345", http.RequestedUrls);
        Assert.Contains("https://live.example.test/room?id=9999", http.RequestedUrls);
        Assert.Contains("https://live.example.test/room?id=7777", http.RequestedUrls);
        Assert.Equal(20_000, http.LastRequest?.TimeoutMs);
        Assert.Equal("mock-agent", http.LastRequest?.Headers?["User-Agent"]);

        var missingOption = await InvokeAsync(runtime, LivePluginId, PluginMethods.FeedGetLive, new
        {
            context = LiveInvocationContext(),
        });
        Assert.False(missingOption.Success);
        Assert.Equal("SOURCE_OPTION_MISSING", missingOption.Error?.Code);

        // A source whose rooms are all off air succeeds with an empty strip instead of showing them.
        var offAir = await InvokeAsync(runtime, LivePluginId, PluginMethods.FeedGetLive, new
        {
            context = LiveInvocationContext() with
            {
                Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["roomIds"] = "9999,7777" },
            },
        });
        Assert.True(offAir.Success);
        Assert.Empty(offAir.Result.Deserialize<PluginLivePage>(PluginJson.SerializerOptions)!.Items);

        var pluginFailure = await InvokeAsync(runtime, LivePluginId, PluginMethods.FeedGetLive, new
        {
            context = LiveInvocationContext() with
            {
                Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["roomIds"] = "404" },
            },
        });
        Assert.False(pluginFailure.Success);
        Assert.Equal("MOCK_LIVE_FAILURE", pluginFailure.Error?.Code);

        var completed = await InvokeAsync(runtime, LivePluginId, PluginMethods.AuthComplete, new
        {
            context = LiveInvocationContext(),
        });
        Assert.Equal(
            "authenticated",
            completed.Result.Deserialize<PluginAuthState>(PluginJson.SerializerOptions)!.State);

        var validated = await InvokeAsync(runtime, LivePluginId, PluginMethods.AuthValidate, new
        {
            context = LiveInvocationContext(),
        });
        Assert.Equal(
            "authenticated",
            validated.Result.Deserialize<PluginAuthState>(PluginJson.SerializerOptions)!.State);
    }

    private const string LivePluginId = "mock.live";

    private static string FixturePluginRoot
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plugins", "mock.feed");

    private static string LiveFixturePluginRoot
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plugins", "mock.live");

    private static object FeedPayload(string? cursor, int pageSize) => new
    {
        context = InvocationContext(),
        cursor,
        pageSize,
    };

    private static PluginInvocationContext InvocationContext() => new(
        "subscription-01",
        "mock.feed",
        "source-01",
        "updates",
        "credential-01",
        null);

    private static PluginInvocationContext LiveInvocationContext() => new(
        "subscription-live",
        LivePluginId,
        "source-01",
        "live",
        "credential-01",
        null);

    private static PluginHostServices CreateHost(
        IPluginHttpBridge? http = null,
        IPluginCredentialStore? credentials = null)
        => new(
            http ?? new MockHttpBridge(),
            credentials ?? new InMemoryPluginCredentialStore(),
            NullPluginLogSink.Instance,
            new FixedInvocationAuthorizer());

    private static Task<PluginResponseEnvelope<JsonElement>> InvokeAsync(
        IPluginTransport transport,
        string method,
        object payload,
        CancellationToken cancellationToken = default)
        => InvokeAsync(transport, "mock.feed", method, payload, cancellationToken);

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
        var requestJson = JsonSerializer.Serialize(request, PluginJson.SerializerOptions);
        var responseJson = await transport.InvokeAsync(requestJson, cancellationToken);
        return JsonSerializer.Deserialize<PluginResponseEnvelope<JsonElement>>(responseJson, PluginJson.SerializerOptions)!;
    }

    private static TemporaryDirectory CopyFixturePlugin()
        => CopyFixturePlugin(FixturePluginRoot);

    private static TemporaryDirectory CopyFixturePlugin(string source)
    {
        var temporary = TemporaryDirectory.Create();
        CopyDirectory(source, temporary.Path);
        return temporary;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private sealed class MockHttpBridge : IPluginHttpBridge
    {
        public async Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("api.example.test", new Uri(request.Url).Host);
            var body = JsonSerializer.Serialize(new
            {
                items = Enumerable.Range(1, 3).Select(index => new PluginFeedItem(
                    "item-" + index,
                    "Item " + index,
                    "https://example.test/items/" + index,
                    "Mock author",
                    DateTimeOffset.UnixEpoch.AddMinutes(index),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)),
            }, PluginJson.SerializerOptions);
            return new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), body, "utf8");
        }
    }

    private sealed class RecordingLiveHttpBridge : IPluginHttpBridge
    {
        public List<string> RequestedUrls { get; } = [];

        public HostHttpRequest? LastRequest { get; private set; }

        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.Url);
            LastRequest = request;
            var roomId = request.Url.Split("id=", StringSplitOptions.RemoveEmptyEntries).Last();
            var body = JsonSerializer.Serialize(new
            {
                userName = roomId == "12345" ? "Streamer" : "Room " + roomId,
                roomTitle = roomId == "12345" ? "Mock stream" : "Idle room",
                roomCover = roomId == "12345" ? "http://i0.example.test/cover.jpg" : null,
                userHeadImg = roomId == "12345" ? "//i0.example.test/face.jpg" : null,
                userId = "999",
                // 9999 is a re-broadcast and 7777 is offline, so neither may reach the live strip.
                liveState = roomId == "12345" ? "1" : roomId == "9999" ? "2" : "0",
                liveWatchedCount = "1.2万",
            }, PluginJson.SerializerOptions);
            return Task.FromResult(new HostHttpResponse(
                200,
                request.Url,
                new Dictionary<string, string>(),
                body,
                "utf8"));
        }
    }

    private sealed class BlockingHttpBridge : IPluginHttpBridge
    {
        public async Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class IgnoringCancellationHttpBridge : IPluginHttpBridge
    {
        private readonly TaskCompletionSource<HostHttpResponse> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
            => _never.Task;
    }

    private sealed class FixedInvocationAuthorizer : IPluginInvocationAuthorizer
    {
        public Task<PluginHostContext> AuthorizeAsync(
            string pluginId,
            string requestId,
            string method,
            PluginInvocationContext? requestedContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedContext is not null
                && (requestedContext.ProviderId != pluginId || requestedContext.SourceId != "source-01"))
            {
                throw new PluginHostBridgeException("INVOCATION_FORBIDDEN", "The source is not authorized for this plugin.");
            }
            return Task.FromResult(new PluginHostContext(
                pluginId,
                requestId,
                method,
                requestedContext is null ? null : "credential-01"));
        }
    }

    private sealed class ThrowingInvocationAuthorizer : IPluginInvocationAuthorizer
    {
        public Task<PluginHostContext> AuthorizeAsync(
            string pluginId,
            string requestId,
            string method,
            PluginInvocationContext? requestedContext,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Sensitive host detail");
    }

    private sealed class RecordingTransportFactory : IPluginTransportFactory
    {
        public List<string> CreatedPluginIds { get; } = [];
        public List<RecordingTransport> CreatedTransports { get; } = [];

        public Task<IPluginTransport> CreateAsync(
            PluginPackage package,
            PluginHostServices host,
            PluginRuntimeOptions options,
            CancellationToken cancellationToken)
        {
            CreatedPluginIds.Add(package.Manifest.PluginId);
            var transport = new RecordingTransport();
            CreatedTransports.Add(transport);
            return Task.FromResult<IPluginTransport>(transport);
        }
    }

    private sealed class RecordingTransport : IPluginTransport
    {
        public bool IsDisposed { get; private set; }

        public Task<string> InvokeAsync(string requestJson, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return Task.FromResult(requestJson);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FeedPeek.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
