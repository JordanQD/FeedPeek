namespace FeedPeek.PluginRuntime.Host;

public sealed record PluginHostContext(
    string PluginId,
    string RequestId,
    string Method,
    string? CredentialProfileId);
