using FeedPeek.PluginContracts.Dtos;

namespace FeedPeek.PluginRuntime.Host;

public interface IPluginInvocationAuthorizer
{
    Task<PluginHostContext> AuthorizeAsync(
        string pluginId,
        string requestId,
        string method,
        PluginInvocationContext? requestedContext,
        CancellationToken cancellationToken);
}
