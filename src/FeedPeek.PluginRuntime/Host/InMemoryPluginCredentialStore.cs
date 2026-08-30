using System.Collections.Concurrent;

namespace FeedPeek.PluginRuntime.Host;

public sealed class InMemoryPluginCredentialStore : IPluginCredentialStore
{
    private readonly ConcurrentDictionary<(string PluginId, string ProfileId, string Name), string> _values = new();

    public Task<bool> ExistsAsync(string pluginId, string credentialProfileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = _values.Keys.Any(key =>
            key.PluginId == pluginId && key.ProfileId == credentialProfileId);
        return Task.FromResult(exists);
    }

    public Task<string?> GetOpaqueSecretAsync(string pluginId, string credentialProfileId, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.TryGetValue((pluginId, credentialProfileId, name), out var value);
        return Task.FromResult(value);
    }

    public Task SetOpaqueSecretAsync(string pluginId, string credentialProfileId, string name, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[(pluginId, credentialProfileId, name)] = value;
        return Task.CompletedTask;
    }

    public Task DeleteOpaqueSecretAsync(string pluginId, string credentialProfileId, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.TryRemove((pluginId, credentialProfileId, name), out _);
        return Task.CompletedTask;
    }
}
