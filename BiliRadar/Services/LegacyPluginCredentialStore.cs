using FeedPeek.PluginRuntime.Host;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Services;

/// <summary>
/// Phase 4.4 bridge that exposes only the CSRF token required by legacy item actions.
/// Phase 4.5 replaces it with the persistent credential profile store.
/// </summary>
internal sealed class LegacyPluginCredentialStore(CookieStore cookieStore) : IPluginCredentialStore
{
    public const string CsrfSecretName = "csrf";

    private readonly CookieStore _cookieStore = cookieStore
        ?? throw new ArgumentNullException(nameof(cookieStore));
    private readonly InMemoryPluginCredentialStore _transient = new();

    public Task<bool> ExistsAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsLegacyProfile(pluginId, credentialProfileId)
            && _cookieStore.HasCookie);
    }

    public Task<string?> GetOpaqueSecretAsync(
        string pluginId,
        string credentialProfileId,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsLegacyProfile(pluginId, credentialProfileId)
            && string.Equals(name, CsrfSecretName, StringComparison.Ordinal))
        {
            return Task.FromResult(GetCookieValue(_cookieStore.GetCookieString(), "bili_jct"));
        }

        return _transient.GetOpaqueSecretAsync(
            pluginId,
            credentialProfileId,
            name,
            cancellationToken);
    }

    public Task SetOpaqueSecretAsync(
        string pluginId,
        string credentialProfileId,
        string name,
        string value,
        CancellationToken cancellationToken)
        => _transient.SetOpaqueSecretAsync(
            pluginId,
            credentialProfileId,
            name,
            value,
            cancellationToken);

    public Task DeleteOpaqueSecretAsync(
        string pluginId,
        string credentialProfileId,
        string name,
        CancellationToken cancellationToken)
        => _transient.DeleteOpaqueSecretAsync(
            pluginId,
            credentialProfileId,
            name,
            cancellationToken);

    private static bool IsLegacyProfile(string pluginId, string credentialProfileId)
        => string.Equals(pluginId, BuiltInVideoSitePlugin.PluginId, StringComparison.Ordinal)
            && string.Equals(
                credentialProfileId,
                BuiltInVideoSitePlugin.LegacyCredentialProfileId,
                StringComparison.Ordinal);

    private static string? GetCookieValue(string cookie, string name)
    {
        foreach (var segment in cookie.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0
                || !string.Equals(segment[..separator].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(separator + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
