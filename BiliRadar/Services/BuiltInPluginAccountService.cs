using FeedPeek.PluginContracts.Dtos;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Services;

internal sealed class BuiltInPluginAccountService : IDisposable
{
    private readonly FeedPeekPluginRuntime _runtime;

    public BuiltInPluginAccountService(CookieStore cookieStore)
    {
        _runtime = new FeedPeekPluginRuntime(cookieStore);
    }

    public Task<bool> HasCredentialsAsync(CancellationToken cancellationToken = default)
        => _runtime.HasCredentialsAsync(cancellationToken);

    public Task<PluginAccountProfile?> GetCurrentProfileAsync(CancellationToken cancellationToken = default)
        => _runtime.GetAccountProfileAsync(cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default)
        => _runtime.ClearCredentialsAsync(cancellationToken);

    public Task ImportCookieHeaderAsync(
        string cookieHeader,
        CancellationToken cancellationToken = default)
        => _runtime.ImportCookieHeaderAsync(cookieHeader, cancellationToken);

    public Task<string?> ExportCookieHeaderAsync(CancellationToken cancellationToken = default)
        => _runtime.ExportCookieHeaderAsync(cancellationToken);

    public void Dispose() => _runtime.Dispose();
}
