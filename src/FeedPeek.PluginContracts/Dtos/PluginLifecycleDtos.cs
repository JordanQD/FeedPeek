namespace FeedPeek.PluginContracts.Dtos;

public sealed record PluginHandshakeResult(
    string ProviderId,
    int ApiVersion,
    IReadOnlyList<string> Capabilities);

public sealed record PluginStatusResult(
    string State,
    string? Message,
    IReadOnlyDictionary<string, string>? Details);

public sealed record PluginContextPayload(PluginInvocationContext Context);

public sealed record PluginActionResult(
    bool Completed,
    string? Message,
    IReadOnlyDictionary<string, string>? Data);
