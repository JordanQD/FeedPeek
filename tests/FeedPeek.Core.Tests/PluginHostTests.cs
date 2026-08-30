using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginHost;
using FeedPeek.PluginRuntime.Host;
using System.Text.Json;

namespace FeedPeek.Core.Tests;

public sealed class PluginHostTests
{
    [Fact]
    public async Task FeedProvider_MapsUpdatesAndUsesOpaquePagingCursor()
    {
        var invoker = new RecordingInvoker(request =>
        {
            var page = request.Method switch
            {
                PluginMethods.FeedGetUpdates => CreatePage("item-1", "next", true),
                PluginMethods.FeedGetMoreUpdates => CreatePage("item-2", null, false),
                _ => throw new InvalidOperationException(request.Method),
            };
            return Success(request.RequestId, page);
        });
        var provider = new PluginFeedProvider("mock.feed", new PluginClient(invoker));
        var subscription = CreateSubscription();

        var first = await provider.GetPageAsync(new FeedPageRequest(subscription, null, 20));
        var second = await provider.GetPageAsync(new FeedPageRequest(subscription, first.NextCursor, 20));

        Assert.Equal(new[] { PluginMethods.FeedGetUpdates, PluginMethods.FeedGetMoreUpdates }, invoker.Methods);
        Assert.Equal("item-1", Assert.Single(first.Items).ItemId);
        Assert.Equal("mock.feed", first.Items[0].ProviderId);
        Assert.Equal("source-01", first.Items[0].SourceId);
        Assert.Equal("updates", first.Items[0].FeedId);
        Assert.Equal("next", first.NextCursor);
        Assert.Equal("item-2", Assert.Single(second.Items).ItemId);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task FeedProvider_RejectsInvalidPagingAndItemResults()
    {
        var invalidPaging = new RecordingInvoker(request => Success(
            request.RequestId,
            CreatePage("item-1", null, true)));
        var provider = new PluginFeedProvider("mock.feed", new PluginClient(invalidPaging));

        var pagingError = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            provider.GetPageAsync(new FeedPageRequest(CreateSubscription(), null, 20)));
        Assert.Equal("PLUGIN_RESULT_INVALID", pagingError.Code);

        var duplicateItems = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginFeedPage(
                [CreateItem("same"), CreateItem("same")],
                null,
                false)));
        provider = new PluginFeedProvider("mock.feed", new PluginClient(duplicateItems));
        var itemError = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            provider.GetPageAsync(new FeedPageRequest(CreateSubscription(), null, 20)));
        Assert.Equal("PLUGIN_RESULT_INVALID", itemError.Code);
    }

    [Fact]
    public async Task Client_RejectsResponseMismatchAndPreservesStructuredPluginError()
    {
        var mismatch = new RecordingInvoker(_ => JsonSerializer.Serialize(
            PluginResponseEnvelope<PluginStatusResult>.Succeeded(
                "different-request",
                new PluginStatusResult("ready", null, null)),
            PluginJson.SerializerOptions));
        var client = new PluginClient(mismatch);
        var mismatchError = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            client.InvokeAsync<object, PluginStatusResult>(
                "mock.feed",
                PluginMethods.GetStatus,
                new { }));
        Assert.Equal("PLUGIN_RESPONSE_MISMATCH", mismatchError.Code);

        var failed = new RecordingInvoker(request => JsonSerializer.Serialize(
            PluginResponseEnvelope<PluginStatusResult>.Failed(
                request.RequestId,
                new PluginError("AUTH_EXPIRED", "Sign in again.", false, null)),
            PluginJson.SerializerOptions));
        client = new PluginClient(failed);
        var pluginError = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            client.InvokeAsync<object, PluginStatusResult>(
                "mock.feed",
                PluginMethods.GetStatus,
                new { }));
        Assert.Equal("AUTH_EXPIRED", pluginError.Code);
        Assert.False(pluginError.Retryable);
    }

    [Fact]
    public async Task InvocationRegistry_UsesCanonicalBindingAndRejectsSpoofing()
    {
        var registry = new PluginInvocationRegistry();
        var context = CreateContext();
        registry.Register(context);

        var authorized = await registry.AuthorizeAsync(
            "mock.feed",
            "request-01",
            PluginMethods.FeedGetUpdates,
            context,
            CancellationToken.None);
        Assert.Equal("credential-01", authorized.CredentialProfileId);

        var spoofed = context with { CredentialProfileId = "credential-other" };
        var error = await Assert.ThrowsAsync<PluginHostBridgeException>(() => registry.AuthorizeAsync(
            "mock.feed",
            "request-02",
            PluginMethods.FeedGetUpdates,
            spoofed,
            CancellationToken.None));
        Assert.Equal("INVOCATION_FORBIDDEN", error.Code);

        Assert.True(registry.Remove(context.ProviderId, context.SubscriptionId));
        await Assert.ThrowsAsync<PluginHostBridgeException>(() => registry.AuthorizeAsync(
            "mock.feed",
            "request-03",
            PluginMethods.FeedGetUpdates,
            context,
            CancellationToken.None));
    }

    private static FeedSubscription CreateSubscription()
        => new(
            "subscription-01",
            "mock.feed",
            "source-01",
            "updates",
            "Mock feed",
            null,
            "credential-01",
            true,
            TimeSpan.FromMinutes(15),
            new Dictionary<string, string> { ["mode"] = "all" });

    private static PluginInvocationContext CreateContext()
        => new(
            "subscription-01",
            "mock.feed",
            "source-01",
            "updates",
            "credential-01",
            new Dictionary<string, string> { ["mode"] = "all" });

    private static PluginFeedPage CreatePage(string itemId, string? cursor, bool hasMore)
        => new([CreateItem(itemId)], cursor, hasMore);

    private static PluginFeedItem CreateItem(string itemId)
        => new(
            itemId,
            "Title " + itemId,
            "https://example.test/items/" + itemId,
            "Mock author",
            DateTimeOffset.UnixEpoch,
            "creator-01",
            "https://example.test/creators/creator-01",
            "Summary",
            "https://example.test/images/cover.jpg",
            "https://example.test/images/avatar.jpg",
            "01:30",
            null);

    private static string Success(string requestId, PluginFeedPage page)
        => JsonSerializer.Serialize(
            PluginResponseEnvelope<PluginFeedPage>.Succeeded(requestId, page),
            PluginJson.SerializerOptions);

    private sealed class RecordingInvoker(
        Func<PluginRequestEnvelope<JsonElement>, string> responseFactory) : IPluginRequestInvoker
    {
        private readonly Func<PluginRequestEnvelope<JsonElement>, string> _responseFactory = responseFactory;

        public List<string> Methods { get; } = [];

        public Task<string> InvokeAsync(
            string pluginId,
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = JsonSerializer.Deserialize<PluginRequestEnvelope<JsonElement>>(
                requestJson,
                PluginJson.SerializerOptions)!;
            Assert.Equal(pluginId, request.PluginId);
            Methods.Add(request.Method);
            return Task.FromResult(_responseFactory(request));
        }
    }
}
