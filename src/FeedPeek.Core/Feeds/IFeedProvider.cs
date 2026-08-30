using FeedPeek.Core.Models;

namespace FeedPeek.Core.Feeds;

public interface IFeedProvider
{
    string ProviderId { get; }

    Task<FeedPage> GetPageAsync(
        FeedPageRequest request,
        CancellationToken cancellationToken = default);
}
