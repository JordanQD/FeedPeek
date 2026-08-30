using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;

namespace FeedPeek.PluginHost;

public sealed class PluginFeedProvider(string pluginId, PluginClient client) : IFeedProvider
{
    private readonly PluginClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string ProviderId { get; } = !string.IsNullOrWhiteSpace(pluginId)
        ? pluginId
        : throw new ArgumentException("Plugin id is required.", nameof(pluginId));

    public async Task<FeedPage> GetPageAsync(
        FeedPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subscription = request.Subscription;
        if (!string.Equals(subscription.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("The subscription provider does not match this plugin.");
        if (request.PageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "Plugin page size must be between 1 and 100.");

        var method = ResolveMethod(subscription.FeedId, request.Cursor);
        var context = new PluginInvocationContext(
            subscription.SubscriptionId,
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            subscription.CredentialProfileId,
            subscription.Options);
        var payload = new FeedPagePayload(context, request.Cursor, request.PageSize);
        var page = await _client.InvokeAsync<FeedPagePayload, PluginFeedPage>(
            ProviderId,
            method,
            payload,
            cancellationToken).ConfigureAwait(false);

        ValidatePage(page);
        return new FeedPage(
            page.Items.Select(item => MapItem(subscription, item)).ToArray(),
            page.NextCursor,
            page.HasMore);
    }

    private static string ResolveMethod(string feedId, string? cursor)
        => feedId switch
        {
            PluginFeedIds.Updates => cursor is null ? PluginMethods.FeedGetUpdates : PluginMethods.FeedGetMoreUpdates,
            PluginFeedIds.History => cursor is null ? PluginMethods.AccountGetHistory : PluginMethods.AccountGetMoreHistory,
            PluginFeedIds.ViewLater => cursor is null ? PluginMethods.CollectionGetViewLater : PluginMethods.CollectionGetMoreViewLater,
            _ => throw new InvalidOperationException($"Unsupported plugin feed id '{feedId}'."),
        };

    private static void ValidatePage(PluginFeedPage page)
    {
        if (page.Items is null)
            throw InvalidResult("Plugin feed items are required.");
        if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor))
            throw InvalidResult("A plugin page with more items must return a cursor.");
        if (!page.HasMore && page.NextCursor is not null)
            throw InvalidResult("A final plugin page must not return a cursor.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in page.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemId) || !ids.Add(item.ItemId))
                throw InvalidResult("Plugin feed item ids must be non-empty and unique within a page.");
            if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.AuthorName))
                throw InvalidResult("Plugin feed item title and author are required.");
            RequireWebUrl(item.Url, "item URL");
            ValidateOptionalWebUrl(item.AuthorUrl, "author URL");
            ValidateOptionalWebUrl(item.ImageUrl, "image URL");
            ValidateOptionalWebUrl(item.AuthorImageUrl, "author image URL");
        }
    }

    private static FeedItem MapItem(FeedSubscription subscription, PluginFeedItem item)
        => new(
            subscription.ProviderId,
            subscription.SourceId,
            subscription.FeedId,
            item.ItemId,
            item.Title,
            item.CreatorKey,
            item.AuthorName,
            item.AuthorUrl,
            item.Url,
            item.PublishedAt,
            item.Summary,
            item.ImageUrl,
            item.AuthorImageUrl,
            item.DurationText,
            item.Metadata);

    private static void ValidateOptionalWebUrl(string? value, string field)
    {
        if (!string.IsNullOrWhiteSpace(value)) RequireWebUrl(value, field);
    }

    private static void RequireWebUrl(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidResult($"The plugin returned an invalid {field}.");
        }
    }

    private static PluginInvocationException InvalidResult(string message)
        => new("PLUGIN_RESULT_INVALID", message);
}
