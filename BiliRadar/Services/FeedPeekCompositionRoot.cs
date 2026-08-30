using FeedPeek.Core.Feeds;

namespace BiliRadar.Services;

internal static class FeedPeekCompositionRoot
{
    public static IBiliDataProvider CreateDefaultLegacyDataProvider(CookieStore cookieStore)
        => CreateLegacyWebDataProvider(cookieStore);

    public static BiliWebDataProvider CreateLegacyWebDataProvider(CookieStore cookieStore)
        => new(cookieStore);

    public static IFeedProvider CreateLegacyFeedProvider(CookieStore cookieStore)
        => new LegacyBiliFeedProvider(CreateDefaultLegacyDataProvider(cookieStore));
}
