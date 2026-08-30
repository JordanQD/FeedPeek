using BiliRadar.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Services;

internal sealed class LegacyBiliFeedProvider : IFeedProvider, IDisposable
{
    public const string LegacyProviderId = "legacy.biliradar";
    public const string LegacySourceId = "account-default";
    public const string UpdatesFeedId = "updates";
    private const string LegacyNextCursor = "legacy:next";

    private readonly IBiliDataProvider _dataProvider;

    public LegacyBiliFeedProvider(IBiliDataProvider dataProvider)
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
    }

    public string ProviderId => LegacyProviderId;

    public async Task<FeedPage> GetPageAsync(
        FeedPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSubscription(request.Subscription);

        if (string.IsNullOrWhiteSpace(request.Cursor))
        {
            var updates = await _dataProvider
                .GetRecentVideoUpdatesAsync(cancellationToken)
                .ConfigureAwait(false);
            var items = MapItems(updates);
            var hasMore = items.Count > 0;
            return new FeedPage(items, hasMore ? LegacyNextCursor : null, hasMore);
        }

        if (!string.Equals(request.Cursor, LegacyNextCursor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The cursor was not issued by this legacy feed provider.");
        }

        var page = await _dataProvider
            .GetMoreVideoUpdatesAsync(cancellationToken)
            .ConfigureAwait(false);
        return new FeedPage(
            MapItems(page.Items),
            page.HasMore ? LegacyNextCursor : null,
            page.HasMore);
    }

    public void Dispose()
    {
        if (_dataProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    internal static FeedItem MapItem(BiliVideoUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["legacy.aid"] = update.Aid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["legacy.likeCount"] = update.LikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["legacy.commentCount"] = update.CommentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (update.Page > 0)
        {
            metadata["legacy.page"] = update.Page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (update.VideoCount > 0)
        {
            metadata["legacy.videoCount"] = update.VideoCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new FeedItem(
            LegacyProviderId,
            LegacySourceId,
            UpdatesFeedId,
            update.Id,
            update.Title,
            update.CreatorMid > 0
                ? update.CreatorMid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
            update.CreatorName,
            update.CreatorMid > 0 ? $"https://space.bilibili.com/{update.CreatorMid}" : null,
            update.Url,
            update.PublishedAt,
            NullIfWhiteSpace(update.Description),
            NullIfWhiteSpace(update.CoverUrl),
            NullIfWhiteSpace(update.AvatarUrl),
            NullIfWhiteSpace(update.DurationText),
            metadata);
    }

    private static IReadOnlyList<FeedItem> MapItems(IReadOnlyList<BiliVideoUpdate> updates)
    {
        var items = new List<FeedItem>(updates.Count);
        foreach (var update in updates)
        {
            items.Add(MapItem(update));
        }

        return items;
    }

    private static void ValidateSubscription(FeedSubscription subscription)
    {
        if (!string.Equals(subscription.ProviderId, LegacyProviderId, StringComparison.Ordinal)
            || !string.Equals(subscription.SourceId, LegacySourceId, StringComparison.Ordinal)
            || !string.Equals(subscription.FeedId, UpdatesFeedId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The subscription does not target the legacy BiliRadar updates feed.");
        }
    }

    private static string? NullIfWhiteSpace(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
