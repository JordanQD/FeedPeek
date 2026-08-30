using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginHost;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeedPeek.Core.Tests;

public sealed class VideoSitePluginTests
{
    private const string PluginId = "feedpeek.video-site";

    [Fact]
    public async Task BuiltInPackage_LoadsAndMapsUpdateKindsWithOpaquePaging()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        Assert.Equal(PluginId, package.Manifest.PluginId);
        Assert.Contains("feed.updates", package.Manifest.Capabilities);
        Assert.DoesNotContain(package.RootPath, package.Scripts.Values);

        var bridge = new FixtureSiteHttpBridge(
            await File.ReadAllTextAsync(FirstPagePath),
            await File.ReadAllTextAsync(FinalPagePath));
        var registry = new PluginInvocationRegistry();
        var subscription = CreateSubscription();
        registry.Register(ToContext(subscription));
        await using var runtime = await JintPluginTransport.CreateAsync(
            package,
            new PluginHostServices(
                bridge,
                new InMemoryPluginCredentialStore(),
                NullPluginLogSink.Instance,
                registry));
        var provider = new PluginFeedProvider(
            PluginId,
            new PluginClient(new TransportInvoker(runtime)));

        var first = await provider.GetPageAsync(
            new FeedPageRequest(subscription, null, PluginProtocolLimits.MaxPageSize));
        var second = await provider.GetPageAsync(
            new FeedPageRequest(subscription, first.NextCursor, PluginProtocolLimits.MaxPageSize));

        Assert.Equal("offset-page-2", first.NextCursor);
        Assert.True(first.HasMore);
        Assert.Collection(
            first.Items,
            archive =>
            {
                Assert.Equal("BV1ARCHIVE", archive.ItemId);
                Assert.Equal("1001", archive.CreatorKey);
                Assert.Equal("Archive 作者", archive.AuthorName);
                Assert.Equal("https://www.bilibili.com/video/BV1ARCHIVE", archive.Url);
                Assert.Equal("https://img.example/archive.jpg", archive.ImageUrl);
                Assert.Equal("https://img.example/avatar.jpg", archive.AuthorImageUrl);
                Assert.Equal("03:21", archive.DurationText);
                Assert.Equal("Archive 描述", archive.Summary);
                Assert.Equal("101", archive.Metadata!["aid"]);
                Assert.Equal("7", archive.Metadata["likeCount"]);
                Assert.Equal("8", archive.Metadata["commentCount"]);
            },
            pgc => Assert.Equal("BV1PGC", pgc.ItemId),
            opus =>
            {
                Assert.Equal("303", opus.ItemId);
                Assert.Equal("https://img.example/opus.jpg", opus.ImageUrl);
                Assert.Equal("https://www.bilibili.com/av303", opus.Url);
            });
        Assert.Equal("BV1FINAL", Assert.Single(second.Items).ItemId);
        Assert.Null(second.NextCursor);
        Assert.False(second.HasMore);

        Assert.Equal(2, bridge.Requests.Count);
        Assert.DoesNotContain("&offset=", bridge.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("&offset=offset-page-2", bridge.Requests[1].Url, StringComparison.Ordinal);
        Assert.Equal("GET", bridge.Requests[0].Method);
        var headers = bridge.Requests[0].Headers!;
        Assert.Contains("Mozilla/5.0", headers["User-Agent"], StringComparison.Ordinal);
        Assert.Equal("https://www.bilibili.com/", headers["Referer"]);
    }

    [Fact]
    public async Task ApiAuthenticationError_IsStructuredAndDoesNotFaultPluginRealm()
    {
        var bridge = new MutableSiteHttpBridge("""{"code":-101,"message":"账号未登录"}""");
        var (runtime, provider) = await CreateRuntimeAndProviderAsync(bridge);
        await using (runtime)
        {
            var error = await Assert.ThrowsAsync<PluginInvocationException>(() => provider.GetPageAsync(
                new FeedPageRequest(CreateSubscription(), null, 20)));
            Assert.Equal("AUTH_EXPIRED", error.Code);
            Assert.Contains("账号未登录", error.Message, StringComparison.Ordinal);

            bridge.Body = await File.ReadAllTextAsync(FinalPagePath);
            var recovered = await provider.GetPageAsync(
                new FeedPageRequest(CreateSubscription(), null, 20));
            Assert.Equal("BV1FINAL", Assert.Single(recovered.Items).ItemId);
        }
    }

    [Fact]
    public async Task DuplicateVideoIdsInOneSitePage_AreCollapsedBeforeHostValidation()
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(FirstPagePath))!.AsObject();
        var items = root["data"]!["items"]!.AsArray();
        items.Add(items[0]!.DeepClone());
        var (runtime, provider) = await CreateRuntimeAndProviderAsync(
            new MutableSiteHttpBridge(root.ToJsonString()));
        await using (runtime)
        {
            var page = await provider.GetPageAsync(
                new FeedPageRequest(CreateSubscription(), null, 20));

            Assert.Equal(3, page.Items.Count);
            Assert.Equal(page.Items.Count, page.Items.Select(item => item.ItemId).Distinct().Count());
        }
    }

    [Fact]
    public async Task UpdateRequest_CancellationFlowsThroughSitePlugin()
    {
        var (runtime, provider) = await CreateRuntimeAndProviderAsync(new BlockingSiteHttpBridge());
        await using (runtime)
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetPageAsync(
                new FeedPageRequest(CreateSubscription(), null, 20),
                cancellation.Token));
        }
    }

    private static string FixturePluginRoot
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plugins", "feedpeek.video-site");

    private static string FirstPagePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteResponses", "dynamic-video-page.json");

    private static string FinalPagePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteResponses", "dynamic-video-final-page.json");

    private static FeedSubscription CreateSubscription()
        => new(
            "subscription-video-site",
            PluginId,
            "account-default",
            PluginFeedIds.Updates,
            "视频更新",
            null,
            "credential-default",
            true,
            TimeSpan.FromMinutes(15),
            null);

    private static PluginInvocationContext ToContext(FeedSubscription subscription)
        => new(
            subscription.SubscriptionId,
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            subscription.CredentialProfileId,
            subscription.Options);

    private static async Task<(JintPluginTransport Runtime, PluginFeedProvider Provider)>
        CreateRuntimeAndProviderAsync(IPluginHttpBridge bridge)
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var registry = new PluginInvocationRegistry();
        registry.Register(ToContext(CreateSubscription()));
        var runtime = await JintPluginTransport.CreateAsync(
            package,
            new PluginHostServices(
                bridge,
                new InMemoryPluginCredentialStore(),
                NullPluginLogSink.Instance,
                registry));
        return (
            runtime,
            new PluginFeedProvider(PluginId, new PluginClient(new TransportInvoker(runtime))));
    }

    private sealed class TransportInvoker(IPluginTransport transport) : IPluginRequestInvoker
    {
        private readonly IPluginTransport _transport = transport;

        public Task<string> InvokeAsync(
            string pluginId,
            string requestJson,
            CancellationToken cancellationToken = default)
            => _transport.InvokeAsync(requestJson, cancellationToken);
    }

    private sealed class FixtureSiteHttpBridge(string firstPage, string finalPage) : IPluginHttpBridge
    {
        public List<HostHttpRequest> Requests { get; } = [];

        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PluginId, context.PluginId);
            Assert.Contains("api.bilibili.com", manifest.Network.AllowedHosts);
            Assert.Equal("credential-default", context.CredentialProfileId);
            Requests.Add(request);
            var body = request.Url.Contains("&offset=", StringComparison.Ordinal) ? finalPage : firstPage;
            return Task.FromResult(new HostHttpResponse(
                200,
                request.Url,
                new Dictionary<string, string>(),
                body,
                "utf8"));
        }
    }

    private sealed class MutableSiteHttpBridge(string body) : IPluginHttpBridge
    {
        public string Body { get; set; } = body;

        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HostHttpResponse(
                200,
                request.Url,
                new Dictionary<string, string>(),
                Body,
                "utf8"));
        }
    }

    private sealed class BlockingSiteHttpBridge : IPluginHttpBridge
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
}
