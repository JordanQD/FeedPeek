using FeedPeek.Core.Models;
using System.Globalization;

namespace FeedPeek.Core.Feeds;

public sealed class MockFeedProvider : IFeedProvider
{
    private readonly IReadOnlyList<FeedItem> _items;

    public MockFeedProvider(string providerId, IReadOnlyList<FeedItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ProviderId = providerId;
        _items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public string ProviderId { get; }

    public Task<FeedPage> GetPageAsync(
        FeedPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Subscription.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The subscription does not belong to this provider.");
        }

        var pageSize = Math.Max(1, request.PageSize);
        var offset = ParseCursor(request.Cursor);
        var items = _items.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + items.Count;
        var hasMore = nextOffset < _items.Count;
        return Task.FromResult(new FeedPage(
            items,
            hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            hasMore));
    }

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            && offset >= 0
                ? offset
                : throw new InvalidOperationException("The mock feed cursor is invalid.");
    }
}
