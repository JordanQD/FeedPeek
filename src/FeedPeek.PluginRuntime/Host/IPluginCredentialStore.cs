namespace FeedPeek.PluginRuntime.Host;

public interface IPluginCredentialStore
{
    Task<bool> ExistsAsync(string pluginId, string credentialProfileId, CancellationToken cancellationToken);
    Task<string?> GetOpaqueSecretAsync(string pluginId, string credentialProfileId, string name, CancellationToken cancellationToken);
    Task SetOpaqueSecretAsync(string pluginId, string credentialProfileId, string name, string value, CancellationToken cancellationToken);
    Task DeleteOpaqueSecretAsync(string pluginId, string credentialProfileId, string name, CancellationToken cancellationToken);
}

public interface IPluginCookieProvider
{
    Task<string?> GetCookieHeaderAsync(
        string pluginId,
        string credentialProfileId,
        Uri requestUri,
        CancellationToken cancellationToken);
}
