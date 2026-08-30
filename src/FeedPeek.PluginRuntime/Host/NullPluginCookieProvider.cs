namespace FeedPeek.PluginRuntime.Host;

public sealed class NullPluginCookieProvider : IPluginCookieProvider
{
    public static NullPluginCookieProvider Instance { get; } = new();

    private NullPluginCookieProvider()
    {
    }

    public Task<string?> GetCookieHeaderAsync(string pluginId, string credentialProfileId, Uri requestUri, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
