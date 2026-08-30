using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginRuntime.Execution;
using FeedPeek.PluginRuntime.Host;

namespace FeedPeek.PluginRuntime.Discovery;

public sealed class PluginRuntimeCatalog : IAsyncDisposable
{
    private readonly Dictionary<string, TransportEntry> _transports = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly PluginDiscoveryService _discovery;
    private readonly PluginHostServices _host;
    private readonly PluginRuntimeOptions _options;
    private readonly IPluginTransportFactory _transportFactory;
    private int _disposeState;

    public PluginRuntimeCatalog(
        PluginHostServices host,
        PluginRuntimeOptions? options = null,
        PluginDiscoveryService? discovery = null,
        IPluginTransportFactory? transportFactory = null)
    {
        _host = host;
        _options = options ?? new PluginRuntimeOptions();
        _discovery = discovery ?? new PluginDiscoveryService();
        _transportFactory = transportFactory ?? new JintPluginTransportFactory();
    }

    public IReadOnlyList<string> LoadedPluginIds
    {
        get
        {
            lock (_sync)
                return _transports.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
    }

    public async Task<IReadOnlyList<PluginDiscoveryResult>> LoadAsync(
        string pluginsRoot,
        IReadOnlySet<string>? disabledPluginIds = null,
        IReadOnlyDictionary<string, string>? expectedPackageHashes = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            await ClearCoreAsync();
            var discovered = await _discovery.DiscoverAsync(
                pluginsRoot,
                disabledPluginIds,
                expectedPackageHashes,
                cancellationToken);
            var results = new List<PluginDiscoveryResult>(discovered.Count);
            foreach (var result in discovered)
            {
                if (!result.IsAvailable)
                {
                    results.Add(result);
                    continue;
                }

                try
                {
                    var transport = await _transportFactory.CreateAsync(result.Package!, _host, _options, cancellationToken);
                    lock (_sync)
                        _transports.Add(result.Package!.Manifest.PluginId, new TransportEntry(transport));
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    results.Add(result with
                    {
                        ErrorCode = "PLUGIN_INITIALIZATION_FAILED",
                        ErrorMessage = ex.Message,
                    });
                }
            }
            return results;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<string> InvokeAsync(
        string pluginId,
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        TransportEntry? entry;
        lock (_sync)
        {
            if (!_transports.TryGetValue(pluginId, out entry) || !entry.TryAcquire())
                throw new KeyNotFoundException("The plugin is not loaded or is being disabled: " + pluginId);
        }
        try
        {
            return await entry.Transport.InvokeAsync(requestJson, cancellationToken);
        }
        finally
        {
            entry.Release();
        }
    }

    public async Task DisableAsync(string pluginId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _lifecycleGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            TransportEntry? entry;
            lock (_sync)
                _transports.Remove(pluginId, out entry);
            if (entry is not null)
            {
                await entry.StopAsync();
                await entry.Transport.DisposeAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        await _lifecycleGate.WaitAsync();
        try
        {
            await ClearCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ClearCoreAsync()
    {
        TransportEntry[] transports;
        lock (_sync)
        {
            transports = _transports.Values.ToArray();
            _transports.Clear();
        }
        foreach (var entry in transports)
        {
            await entry.StopAsync();
            await entry.Transport.DisposeAsync();
        }
    }

    private sealed class TransportEntry(IPluginTransport transport)
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;
        private bool _stopping;

        public IPluginTransport Transport { get; } = transport;

        public bool TryAcquire()
        {
            lock (_sync)
            {
                if (_stopping)
                    return false;
                _activeCalls++;
                return true;
            }
        }

        public void Release()
        {
            lock (_sync)
            {
                _activeCalls--;
                if (_stopping && _activeCalls == 0)
                    _drained.TrySetResult();
            }
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                _stopping = true;
                if (_activeCalls == 0)
                    _drained.TrySetResult();
                return _drained.Task;
            }
        }
    }
}
