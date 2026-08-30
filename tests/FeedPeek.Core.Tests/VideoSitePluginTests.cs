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
        Assert.Contains("account.history", package.Manifest.Capabilities);
        Assert.Contains("collection.viewLater", package.Manifest.Capabilities);
        Assert.Contains("item.actions", package.Manifest.Capabilities);
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

    [Fact]
    public async Task HistoryAndViewLater_MapItemsAndUseOpaquePaging()
    {
        var historyFirst = JsonNode.Parse(await File.ReadAllTextAsync(HistoryPagePath))!.AsObject();
        historyFirst["data"]!["has_more"] = true;
        historyFirst["data"]!["cursor"]!["max"] = 10;
        historyFirst["data"]!["cursor"]!["view_at"] = 1699999999;
        historyFirst["data"]!["cursor"]!["has_more"] = true;
        var historyFinal = JsonNode.Parse(await File.ReadAllTextAsync(HistoryPagePath))!.AsObject();
        historyFinal["data"]!["list"] = new JsonArray();

        var viewLaterFirst = await File.ReadAllTextAsync(ViewLaterPagePath);
        var viewLaterFinal = JsonNode.Parse(viewLaterFirst)!.AsObject();
        viewLaterFinal["data"]!["count"] = 1;

        var bridge = new SectionSiteHttpBridge(
            historyFirst.ToJsonString(),
            historyFinal.ToJsonString(),
            viewLaterFirst,
            viewLaterFinal.ToJsonString());

        var historySubscription = CreateSubscription(PluginFeedIds.History);
        var (historyRuntime, historyProvider) = await CreateRuntimeAndProviderAsync(
            bridge,
            historySubscription);
        await using (historyRuntime)
        {
            var first = await historyProvider.GetPageAsync(new FeedPageRequest(historySubscription, null, 20));
            var item = Assert.Single(first.Items);
            Assert.Equal("BV1HISTORY", item.ItemId);
            Assert.Equal("02:05", item.DurationText);
            Assert.Equal("已观看 00:45 / 02:05", item.Summary);
            Assert.Equal("404", item.Metadata!["aid"]);
            Assert.Equal("10:1699999999", first.NextCursor);

            var final = await historyProvider.GetPageAsync(
                new FeedPageRequest(historySubscription, first.NextCursor, 20));
            Assert.Empty(final.Items);
            Assert.False(final.HasMore);
        }

        var viewLaterSubscription = CreateSubscription(PluginFeedIds.ViewLater);
        var (viewLaterRuntime, viewLaterProvider) = await CreateRuntimeAndProviderAsync(
            bridge,
            viewLaterSubscription);
        await using (viewLaterRuntime)
        {
            var first = await viewLaterProvider.GetPageAsync(
                new FeedPageRequest(viewLaterSubscription, null, 20));
            var item = Assert.Single(first.Items);
            Assert.Equal("BV1LATER", item.ItemId);
            Assert.Equal("01:01", item.DurationText);
            Assert.Equal("未观看 · 共 01:01", item.Summary);
            Assert.Equal("2:1", first.NextCursor);

            var final = await viewLaterProvider.GetPageAsync(
                new FeedPageRequest(viewLaterSubscription, first.NextCursor, 20));
            Assert.False(final.HasMore);
        }
    }

    [Fact]
    public async Task ViewLaterActions_UseCsrfAndPostOnlyAfterDescriptorLookup()
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var subscription = CreateSubscription(PluginFeedIds.Updates);
        var registry = new PluginInvocationRegistry();
        registry.Register(ToContext(subscription));
        var credentials = new InMemoryPluginCredentialStore();
        await credentials.SetOpaqueSecretAsync(
            PluginId,
            subscription.CredentialProfileId!,
            "csrf",
            "csrf-token",
            CancellationToken.None);
        var bridge = new MutableSiteHttpBridge("""{"code":0}""");
        await using var runtime = await JintPluginTransport.CreateAsync(
            package,
            new PluginHostServices(
                bridge,
                credentials,
                NullPluginLogSink.Instance,
                registry));
        var client = new PluginClient(new TransportInvoker(runtime));
        var actions = new PluginItemActionService(PluginId, client);
        var item = new FeedItem(
            PluginId,
            subscription.SourceId,
            subscription.FeedId,
            "BV1ACTION",
            "Action",
            null,
            "Author",
            null,
            "https://www.bilibili.com/video/BV1ACTION",
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            new Dictionary<string, string> { ["aid"] = "505" });

        var descriptors = await actions.GetActionsAsync(subscription, item);
        var add = Assert.Single(descriptors, action => action.ActionId == "viewLater.add");
        var remove = Assert.Single(descriptors, action => action.ActionId == "viewLater.remove");
        await actions.ExecuteAsync(subscription, add);
        await actions.ExecuteAsync(subscription, remove);

        Assert.Collection(
            bridge.Requests,
            request =>
            {
                Assert.EndsWith("/add", request.Url, StringComparison.Ordinal);
                Assert.Equal("POST", request.Method);
                Assert.Equal("aid=505&csrf=csrf-token", request.Body);
            },
            request =>
            {
                Assert.EndsWith("/del", request.Url, StringComparison.Ordinal);
                Assert.Equal("POST", request.Method);
                Assert.Equal("aid=505&csrf=csrf-token", request.Body);
            });
    }

    private static string FixturePluginRoot
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plugins", "feedpeek.video-site");

    private static string FirstPagePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteResponses", "dynamic-video-page.json");

    private static string FinalPagePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteResponses", "dynamic-video-final-page.json");

    private static string HistoryPagePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteResponses", "history-page.json");

    private static string ViewLaterPagePath
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteResponses", "view-later-page.json");

    private static FeedSubscription CreateSubscription(string feedId = PluginFeedIds.Updates)
        => new(
            $"subscription-video-site-{feedId}",
            PluginId,
            "account-default",
            feedId,
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
        CreateRuntimeAndProviderAsync(
            IPluginHttpBridge bridge,
            FeedSubscription? subscription = null)
    {
        var package = await new PluginManifestLoader().LoadAsync(FixturePluginRoot);
        var registry = new PluginInvocationRegistry();
        registry.Register(ToContext(subscription ?? CreateSubscription()));
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

        public List<HostHttpRequest> Requests { get; } = [];

        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new HostHttpResponse(
                200,
                request.Url,
                new Dictionary<string, string>(),
                Body,
                "utf8"));
        }
    }

    private sealed class SectionSiteHttpBridge(
        string historyFirst,
        string historyFinal,
        string viewLaterFirst,
        string viewLaterFinal) : IPluginHttpBridge
    {
        public Task<HostHttpResponse> SendAsync(
            PluginManifest manifest,
            PluginHostContext context,
            HostHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Url.Contains("history/cursor", StringComparison.Ordinal)
                ? request.Url.Contains("max=0", StringComparison.Ordinal) ? historyFirst : historyFinal
                : request.Url.Contains("pn=1", StringComparison.Ordinal) ? viewLaterFirst : viewLaterFinal;
            return Task.FromResult(new HostHttpResponse(
                200,
                request.Url,
                new Dictionary<string, string>(),
                body,
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
