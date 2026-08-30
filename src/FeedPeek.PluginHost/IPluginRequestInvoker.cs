using FeedPeek.PluginRuntime.Discovery;

namespace FeedPeek.PluginHost;

public interface IPluginRequestInvoker
{
    Task<string> InvokeAsync(
        string pluginId,
        string requestJson,
        CancellationToken cancellationToken = default);
}

public sealed class PluginRuntimeCatalogInvoker(PluginRuntimeCatalog catalog) : IPluginRequestInvoker
{
    private readonly PluginRuntimeCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public Task<string> InvokeAsync(
        string pluginId,
        string requestJson,
        CancellationToken cancellationToken = default)
        => _catalog.InvokeAsync(pluginId, requestJson, cancellationToken);
}
