using System.Text.Json;

namespace FeedPeek.PluginContracts.Protocol;

public static class PluginJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
