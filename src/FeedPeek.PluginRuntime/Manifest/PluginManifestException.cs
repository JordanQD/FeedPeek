namespace FeedPeek.PluginRuntime.Manifest;

public sealed class PluginManifestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
