namespace FeedPeek.PluginRuntime.Host;

public sealed class NullPluginLogSink : IPluginLogSink
{
    public static NullPluginLogSink Instance { get; } = new();

    private NullPluginLogSink()
    {
    }

    public void Write(string pluginId, string level, string message, IReadOnlyDictionary<string, string>? context)
    {
    }
}
