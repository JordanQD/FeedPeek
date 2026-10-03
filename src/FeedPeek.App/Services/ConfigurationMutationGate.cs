using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Services;

// A restore must not race the observation/delivery of a partially restored notification selection.
internal static class ConfigurationMutationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> EnterAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Gate.Release();
        }
    }
}
