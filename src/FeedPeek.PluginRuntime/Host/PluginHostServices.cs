namespace FeedPeek.PluginRuntime.Host;

public sealed record PluginHostServices(
    IPluginHttpBridge Http,
    IPluginCredentialStore Credentials,
    IPluginLogSink Log,
    IPluginInvocationAuthorizer InvocationAuthorizer);
