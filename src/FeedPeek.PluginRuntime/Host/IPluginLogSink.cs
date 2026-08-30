namespace FeedPeek.PluginRuntime.Host;

public interface IPluginLogSink
{
    void Write(string pluginId, string level, string message, IReadOnlyDictionary<string, string>? context);
}
