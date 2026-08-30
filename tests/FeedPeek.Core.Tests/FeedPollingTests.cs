using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class FeedPollingTests
{
    [Fact]
    public async Task FirstSuccessfulPollEstablishesBaselineBeforeReportingNewItems()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var subscription = CreateSubscription();
        await store.SaveSourceAndSubscriptionAsync(CreateSource(), subscription);
        using var providers = new FeedProviderRegistry([
            new SequenceProvider(
                Page(CreateItem("first")),
                Page(CreateItem("first"), CreateItem("second")))
        ]);
        var polling = new FeedPollingService(store, providers);

        var first = await polling.PollAsync(subscription);
        var second = await polling.PollAsync(subscription);

        Assert.True(first.InitializedBaseline);
        Assert.Empty(first.NewItems);
        Assert.False(second.InitializedBaseline);
        Assert.Equal("second", Assert.Single(second.NewItems).ItemId);
    }

    [Fact]
    public async Task UpsertPreservesReadState()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var subscription = CreateSubscription();
        await store.SaveSourceAndSubscriptionAsync(CreateSource(), subscription);
        var original = CreateItem("first");
        await store.UpsertItemsAsync(subscription.SubscriptionId, [original], DateTimeOffset.UtcNow);
        await store.MarkReadAsync(original.Key);

        var updated = original with { Title = "Updated title" };
        await store.UpsertItemsAsync(subscription.SubscriptionId, [updated], DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Empty(await store.GetItemsAsync(new FeedQuery(UnreadOnly: true)));
        var stored = Assert.Single(await store.GetItemsAsync(new FeedQuery()));
        Assert.True(stored.IsRead);
        Assert.Equal("Updated title", stored.Item.Title);
    }

    [Fact]
    public async Task LoadMoreUsesStoredOpaqueCursorAndPersistsTheNextPage()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var subscription = CreateSubscription();
        await store.SaveSourceAndSubscriptionAsync(CreateSource(), subscription);
        var provider = new SequenceProvider(
            new FeedPage([CreateItem("first")], "opaque-next", true),
            new FeedPage([CreateItem("second")], null, false));
        using var providers = new FeedProviderRegistry([provider]);
        var polling = new FeedPollingService(store, providers);

        await polling.PollAsync(subscription);
        Assert.True(await polling.HasMoreAsync(subscription));

        var loaded = await polling.LoadMoreAsync(subscription);

        Assert.Equal("second", Assert.Single(loaded.Items).ItemId);
        Assert.False(loaded.HasMore);
        Assert.False(await polling.HasMoreAsync(subscription));
        Assert.Equal([null, "opaque-next"], provider.RequestedCursors);
        Assert.Equal(2, (await store.GetItemsAsync(new FeedQuery())).Count);

        var final = await polling.LoadMoreAsync(subscription);
        Assert.Empty(final.Items);
        Assert.Equal(2, provider.RequestedCursors.Count);
    }

    [Fact]
    public async Task LogicalItemSavedStateIsSynchronizedAcrossPluginFeeds()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var source = new SourceDefinition(
            "test.provider",
            "source",
            "Test source",
            null,
            [
                new FeedDefinition("updates", "Updates", "items", true, true),
                new FeedDefinition("viewLater", "View later", "collection", true, false),
            ],
            null);
        var updates = CreateSubscription();
        var viewLater = updates with
        {
            SubscriptionId = "subscription-view-later",
            FeedId = "viewLater",
        };
        await store.SaveSourceAndSubscriptionAsync(source, updates);
        await store.SaveSourceAndSubscriptionAsync(source, viewLater);
        await store.UpsertItemsAsync(updates.SubscriptionId, [CreateItem("shared")], DateTimeOffset.UtcNow);
        await store.UpsertItemsAsync(
            viewLater.SubscriptionId,
            [CreateItem("shared") with { FeedId = "viewLater" }],
            DateTimeOffset.UtcNow);

        await store.SetSavedForItemAsync("test.provider", "source", "shared", true);

        var saved = await store.GetItemsAsync(new FeedQuery(
            SavedOnly: true,
            ProviderId: "test.provider"));
        Assert.Equal(2, saved.Count);
        Assert.Single(await store.GetItemsAsync(new FeedQuery(
            SavedOnly: true,
            ProviderId: "test.provider",
            FeedId: "viewLater")));

        await store.SetSavedForFeedAsync("test.provider", "source", "viewLater", false);
        Assert.Single(await store.GetItemsAsync(new FeedQuery(SavedOnly: true)));
    }

    [Fact]
    public async Task DeleteSourceRemovesOnlyThatSourcesCachedItems()
    {
        await using var store = new SqliteFeedStore("Data Source=:memory:");
        var firstSource = CreateSource();
        var firstSubscription = CreateSubscription();
        var secondSource = firstSource with { SourceId = "other-source", DisplayName = "Other source" };
        var secondSubscription = firstSubscription with
        {
            SubscriptionId = "other-subscription",
            SourceId = secondSource.SourceId,
            DisplayName = secondSource.DisplayName,
        };
        await store.SaveSourceAndSubscriptionAsync(firstSource, firstSubscription);
        await store.SaveSourceAndSubscriptionAsync(secondSource, secondSubscription);
        await store.UpsertItemsAsync(firstSubscription.SubscriptionId, [CreateItem("first")], DateTimeOffset.UtcNow);
        await store.UpsertItemsAsync(
            secondSubscription.SubscriptionId,
            [CreateItem("second") with { SourceId = secondSource.SourceId }],
            DateTimeOffset.UtcNow);

        await store.DeleteSourceAsync(firstSource.ProviderId, firstSource.SourceId);

        var remaining = Assert.Single(await store.GetItemsAsync(new FeedQuery()));
        Assert.Equal(secondSource.SourceId, remaining.Item.SourceId);
        var remainingSource = Assert.Single(await store.GetSourceSummariesAsync()).Source;
        Assert.Equal(secondSource.ProviderId, remainingSource.ProviderId);
        Assert.Equal(secondSource.SourceId, remainingSource.SourceId);
    }

    private static SourceDefinition CreateSource() => new(
        "test.provider",
        "source",
        "Test source",
        null,
        [new FeedDefinition("updates", "Updates", "items", false, true)],
        null);

    private static FeedSubscription CreateSubscription() => new(
        "subscription",
        "test.provider",
        "source",
        "updates",
        "Test source",
        "https://example.test/feed.xml",
        null,
        true,
        TimeSpan.FromMinutes(15),
        null);

    private static FeedItem CreateItem(string itemId) => new(
        "test.provider",
        "source",
        "updates",
        itemId,
        itemId,
        null,
        "Author",
        null,
        $"https://example.test/{itemId}",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        null,
        null,
        null,
        null,
        null);

    private static FeedPage Page(params FeedItem[] items) => new(items, null, false);

    private sealed class SequenceProvider(params FeedPage[] pages) : IFeedProvider
    {
        private int _index;

        public List<string?> RequestedCursors { get; } = [];

        public string ProviderId => "test.provider";

        public Task<FeedPage> GetPageAsync(
            FeedPageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedCursors.Add(request.Cursor);
            var page = pages[Math.Min(_index, pages.Length - 1)];
            _index++;
            return Task.FromResult(page);
        }
    }
}
