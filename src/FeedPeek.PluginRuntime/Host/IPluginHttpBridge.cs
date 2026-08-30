using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;

namespace FeedPeek.PluginRuntime.Host;

public interface IPluginHttpBridge
{
    Task<HostHttpResponse> SendAsync(
        PluginManifest manifest,
        PluginHostContext context,
        HostHttpRequest request,
        CancellationToken cancellationToken);
}
