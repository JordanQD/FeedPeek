namespace FeedPeek.PluginContracts.Protocol;

public sealed record PluginRequestEnvelope<TPayload>(
    string RequestId,
    int ApiVersion,
    string PluginId,
    string Method,
    TPayload Payload);
