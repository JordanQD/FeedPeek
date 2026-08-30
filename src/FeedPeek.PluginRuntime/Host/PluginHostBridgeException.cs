namespace FeedPeek.PluginRuntime.Host;

public sealed class PluginHostBridgeException(
    string code,
    string message,
    bool retryable = false,
    IReadOnlyDictionary<string, string>? context = null) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public IReadOnlyDictionary<string, string>? Context { get; } = context;
}
