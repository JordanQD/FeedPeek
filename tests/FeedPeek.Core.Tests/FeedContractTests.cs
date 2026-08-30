using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;
using System.Text.Json;

namespace FeedPeek.Core.Tests;

public sealed class FeedContractTests
{
    [Fact]
    public async Task MockProvider_PagesUsingOpaqueCursor()
    {
        var subscription = CreateSubscription();
        var provider = new MockFeedProvider(
            subscription.ProviderId,
            [CreateItem("1"), CreateItem("2"), CreateItem("3")]);

        var first = await provider.GetPageAsync(new FeedPageRequest(subscription, null, 2));
        var second = await provider.GetPageAsync(new FeedPageRequest(subscription, first.NextCursor, 2));

        Assert.Equal(new[] { "1", "2" }, first.Items.Select(item => item.ItemId));
        Assert.Equal("2", first.NextCursor);
        Assert.True(first.HasMore);
        Assert.Equal("3", Assert.Single(second.Items).ItemId);
        Assert.Null(second.NextCursor);
        Assert.False(second.HasMore);
    }

    [Fact]
    public void RequestEnvelope_UsesCamelCaseJsonAndRoundTrips()
    {
        var payload = new FeedPagePayload(
            new PluginInvocationContext(
                "subscription-01",
                "mock.provider",
                "source-01",
                "updates",
                null,
                null),
            null,
            30);
        var envelope = new PluginRequestEnvelope<FeedPagePayload>(
            "request-01",
            PluginProtocolVersion.Current,
            "mock.plugin",
            "feed.getUpdates",
            payload);

        var json = JsonSerializer.Serialize(envelope, PluginJson.SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<PluginRequestEnvelope<FeedPagePayload>>(
            json,
            PluginJson.SerializerOptions);

        Assert.Contains("\"requestId\"", json);
        Assert.Contains("\"apiVersion\":1", json);
        Assert.Equal(envelope, roundTrip);
    }

    private static FeedSubscription CreateSubscription()
        => new(
            "subscription-01",
            "mock.provider",
            "source-01",
            "updates",
            "Mock feed",
            null,
            null,
            true,
            TimeSpan.FromMinutes(15),
            null);

    private static FeedItem CreateItem(string id)
        => new(
            "mock.provider",
            "source-01",
            "updates",
            id,
            $"Item {id}",
            null,
            "Mock author",
            null,
            $"https://example.test/items/{id}",
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null,
            null);
}
