namespace FeedPeek.PluginContracts.Protocol;

/// <summary>
/// A serialized plugin boundary that can be implemented in-process or by a worker transport.
/// </summary>
public interface IPluginTransport : IAsyncDisposable
{
    Task<string> InvokeAsync(string requestJson, CancellationToken cancellationToken = default);
}
