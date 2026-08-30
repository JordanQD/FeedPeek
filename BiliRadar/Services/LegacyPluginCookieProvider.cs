using FeedPeek.PluginRuntime.Host;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Services;

/// <summary>
/// Phase 4.3 bridge from the existing cookie store to the plugin host.
/// Phase 4.5 replaces this with manifest-driven credential profiles.
/// </summary>
internal sealed class LegacyPluginCookieProvider(CookieStore cookieStore) : IPluginCookieProvider
{
    private readonly CookieStore _cookieStore = cookieStore
        ?? throw new ArgumentNullException(nameof(cookieStore));

    public Task<string?> GetCookieHeaderAsync(
        string pluginId,
        string credentialProfileId,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(pluginId, BuiltInVideoSitePlugin.PluginId, StringComparison.Ordinal)
            || !string.Equals(
                credentialProfileId,
                BuiltInVideoSitePlugin.LegacyCredentialProfileId,
                StringComparison.Ordinal))
        {
            return Task.FromResult<string?>(null);
        }

        var cookie = _cookieStore.GetCookieString();
        return Task.FromResult(string.IsNullOrWhiteSpace(cookie) ? null : cookie);
    }
}
