using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedPeek.PluginContracts.Protocol;

public static class PluginJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
