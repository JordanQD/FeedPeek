using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;
using FeedPeek.PluginRuntime.Manifest;

namespace FeedPeek.PluginRuntime.Discovery;

public interface IPluginTransportFactory
{
    Task<IPluginTransport> CreateAsync(
        PluginPackage package,
        PluginHostServices host,
        PluginRuntimeOptions options,
        CancellationToken cancellationToken);
}

public sealed class JintPluginTransportFactory : IPluginTransportFactory
{
    public async Task<IPluginTransport> CreateAsync(
        PluginPackage package,
        PluginHostServices host,
        PluginRuntimeOptions options,
        CancellationToken cancellationToken)
        => await JintPluginTransport.CreateAsync(package, host, options, cancellationToken);
}
