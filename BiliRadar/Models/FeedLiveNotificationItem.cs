using FeedPeek.PluginContracts.Dtos;

namespace BiliRadar.Models;

public sealed record FeedLiveNotificationItem(
    string ProviderId,
    string SourceId,
    PluginLiveItem Item)
{
    public string StableKey
    {
        get
        {
            var roomId = Item.Metadata is not null
                && Item.Metadata.TryGetValue("roomId", out var value)
                    ? value
                    : Item.Url;
            return $"{ProviderId}\n{SourceId}\n{Item.CreatorKey}\n{roomId}";
        }
    }
}
