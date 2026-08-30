namespace FeedPeek.PluginContracts.Protocol;

public sealed record PluginResponseEnvelope<TResult>(
    string RequestId,
    bool Success,
    TResult? Result,
    PluginError? Error)
{
    public static PluginResponseEnvelope<TResult> Succeeded(string requestId, TResult result)
        => new(requestId, true, result, null);

    public static PluginResponseEnvelope<TResult> Failed(string requestId, PluginError error)
        => new(requestId, false, default, error);
}

public sealed record PluginError(
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, string>? Context);
