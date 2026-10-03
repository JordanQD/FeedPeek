using FeedPeek.PluginRuntime.Host;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;

namespace FeedPeek.Services;

internal sealed class PluginCredentialProfileStore : IPluginCredentialStore, IPluginCookieProvider
{
    private const string ResourceName = "FeedPeek.PluginCredentialProfile.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PasswordVault _vault = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<string> GetCacheFingerprintAsync(string pluginId, string profileId, CancellationToken token)
    {
        var profile = await ReadAsync(pluginId, profileId, token).ConfigureAwait(false);
        if (profile is null) return "anonymous";
        var canonical = JsonSerializer.Serialize(new
        {
            profile.CookieHeader,
            Secrets = profile.Secrets.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public async Task<bool> ExistsAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken)
    {
        var profile = await ReadAsync(pluginId, credentialProfileId, cancellationToken)
            .ConfigureAwait(false);
        return profile is not null
            && (!string.IsNullOrWhiteSpace(profile.CookieHeader) || profile.Secrets.Count > 0);
    }

    public async Task<string?> GetCookieHeaderAsync(
        string pluginId,
        string credentialProfileId,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        return (await ReadAsync(pluginId, credentialProfileId, cancellationToken)
            .ConfigureAwait(false))?.CookieHeader;
    }

    public async Task<string?> GetOpaqueSecretAsync(
        string pluginId,
        string credentialProfileId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var profile = await ReadAsync(pluginId, credentialProfileId, cancellationToken)
            .ConfigureAwait(false);
        return profile?.Secrets.TryGetValue(name, out var value) == true ? value : null;
    }

    public async Task SetOpaqueSecretAsync(
        string pluginId,
        string credentialProfileId,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        await UpdateAsync(
            pluginId,
            credentialProfileId,
            profile => profile.Secrets[name] = value,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteOpaqueSecretAsync(
        string pluginId,
        string credentialProfileId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await UpdateAsync(
            pluginId,
            credentialProfileId,
            profile => profile.Secrets.Remove(name),
            cancellationToken).ConfigureAwait(false);
    }

    public Task SaveCookiesAsync(
        string pluginId,
        string credentialProfileId,
        IReadOnlyDictionary<string, string> cookies,
        IReadOnlySet<string>? exposedCookieNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var normalized = cookies
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        var cookieHeader = CookieNormalizer.Normalize(string.Join(
            "; ",
            normalized.Select(item => $"{item.Key}={item.Value}")));
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new InvalidOperationException("登录没有返回可保存的 Cookie。");
        }

        return UpdateAsync(
            pluginId,
            credentialProfileId,
            profile =>
            {
                profile.CookieHeader = cookieHeader;
                profile.Secrets.Clear();
                foreach (var (name, value) in normalized)
                {
                    if (exposedCookieNames?.Contains(name) == true)
                    {
                        profile.Secrets[name] = value;
                    }
                }
            },
            cancellationToken);
    }

    public async Task ImportCookieHeaderAsync(
        string pluginId,
        string credentialProfileId,
        string cookieHeader,
        IReadOnlySet<string>? exposedCookieNames,
        CancellationToken cancellationToken = default)
    {
        var cookies = CookieNormalizer.Normalize(cookieHeader)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => (Segment: segment, Separator: segment.IndexOf('=')))
            .Where(item => item.Separator > 0)
            .ToDictionary(
                item => item.Segment[..item.Separator].Trim(),
                item => item.Segment[(item.Separator + 1)..].Trim(),
                StringComparer.OrdinalIgnoreCase);
        await SaveCookiesAsync(
            pluginId,
            credentialProfileId,
            cookies,
            exposedCookieNames,
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> ExportCookieHeaderAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
        => (await ReadAsync(pluginId, credentialProfileId, cancellationToken)
            .ConfigureAwait(false))?.CookieHeader;

    public async Task DeleteProfileAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(pluginId, credentialProfileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveCredential(GetUserName(pluginId, credentialProfileId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CredentialPayload?> ReadAsync(
        string pluginId,
        string credentialProfileId,
        CancellationToken cancellationToken)
    {
        ValidateKey(pluginId, credentialProfileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadCore(GetUserName(pluginId, credentialProfileId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateAsync(
        string pluginId,
        string credentialProfileId,
        Action<CredentialPayload> update,
        CancellationToken cancellationToken)
    {
        ValidateKey(pluginId, credentialProfileId);
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var userName = GetUserName(pluginId, credentialProfileId);
            var profile = ReadCore(userName) ?? new CredentialPayload();
            update(profile);
            RemoveCredential(userName);
            _vault.Add(new PasswordCredential(
                ResourceName,
                userName,
                JsonSerializer.Serialize(profile, JsonOptions)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private CredentialPayload? ReadCore(string userName)
    {
        var credential = FindCredential(userName);
        if (credential is null)
        {
            return null;
        }

        try
        {
            credential.RetrievePassword();
            return JsonSerializer.Deserialize<CredentialPayload>(credential.Password, JsonOptions);
        }
        catch (JsonException)
        {
            RemoveCredential(userName);
            return null;
        }
        catch (Exception exception) when (IsVaultMiss(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a stored credential by name. This deliberately does not use
    /// <see cref="PasswordVault.Retrieve(string, string)"/>, which throws "无法从保管库获取凭据" for a
    /// missing entry: a plugin that has never been signed in is a normal state, and the throw showed up
    /// as first-chance exception noise under the debugger. <c>FindAllByResource</c> just returns nothing.
    /// </summary>
    private PasswordCredential? FindCredential(string userName)
    {
        try
        {
            foreach (var candidate in _vault.FindAllByResource(ResourceName))
            {
                if (string.Equals(candidate.UserName, userName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception) when (IsVaultMiss(exception))
        {
        }

        return null;
    }

    private void RemoveCredential(string userName)
    {
        var credential = FindCredential(userName);
        if (credential is null)
        {
            return;
        }

        try
        {
            _vault.Remove(credential);
        }
        catch (Exception exception) when (IsVaultMiss(exception))
        {
        }
    }

    private static bool IsVaultMiss(Exception exception)
        => exception is COMException
            or KeyNotFoundException
            or ArgumentException
            or InvalidOperationException;

    private static void ValidateKey(string pluginId, string credentialProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialProfileId);
    }

    private static string GetUserName(string pluginId, string credentialProfileId)
        => $"{Uri.EscapeDataString(pluginId)}|{Uri.EscapeDataString(credentialProfileId)}";

    private sealed class CredentialPayload
    {
        public string? CookieHeader { get; set; }

        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
