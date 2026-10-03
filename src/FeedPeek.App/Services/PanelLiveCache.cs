using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Services;

/// <summary>Display-only live snapshots. Contains DTOs and URLs, never UI objects or notification state.</summary>
internal sealed class PanelLiveCache(string directory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string ContextKey(FeedSubscription subscription, string? pluginVersion, string credentialFingerprint)
        => Hash(JsonSerializer.Serialize(new
        {
            subscription.SubscriptionId, subscription.ProviderId, subscription.SourceId, subscription.FeedId,
            subscription.Endpoint, subscription.CredentialProfileId, pluginVersion, credentialFingerprint,
            Options = subscription.Options?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        }, JsonOptions));

    public async Task<IReadOnlyList<PluginLiveItem>?> ReadAsync(
        FeedSubscription subscription, string contextKey, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = await ReadFileAsync(PathFor(subscription), token).ConfigureAwait(false);
            return snapshot is { Version: 1, Items: not null }
                && snapshot.ContextKey == contextKey
                && snapshot.ProviderId == subscription.ProviderId && snapshot.SourceId == subscription.SourceId
                && snapshot.SubscriptionId == subscription.SubscriptionId
                ? snapshot.Items : null;
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<PluginLiveItem>> RecordAsync(FeedSubscription subscription, string contextKey,
        PluginLivePage page, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        IReadOnlyList<PluginLiveItem> items = page.Items;
        try
        {
            if (!page.IsComplete)
            {
                var previous = await ReadFileAsync(PathFor(subscription), token).ConfigureAwait(false);
                var hasPrevious = previous is { Version: 1, Items: not null } && previous.ContextKey == contextKey;
                // An unsuccessful first observation must not become a cached "all offline" result.
                if (!hasPrevious && page.Items.Count == 0) return [];
                var keys = page.Items.Select(item => item.CreatorKey).ToHashSet(StringComparer.Ordinal);
                items = page.Items.Concat(hasPrevious
                    ? previous!.Items!.Where(item => !keys.Contains(item.CreatorKey)) : []).ToArray();
            }
            Directory.CreateDirectory(directory);
            var path = PathFor(subscription);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var snapshot = new Snapshot(1, subscription.ProviderId, subscription.SourceId,
                subscription.SubscriptionId, contextKey, items.ToArray());
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { /* Cache writes must not prevent fresh live data from displaying. */ }
        catch (UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Gate.Release();
        }
        return items;
    }

    public async Task RemoveSourceAsync(string providerId, string sourceId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var snapshot = await ReadFileAsync(path, token).ConfigureAwait(false);
                if (snapshot?.ProviderId == providerId && snapshot.SourceId == sourceId)
                    File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Gate.Release(); }
    }

    private async Task<Snapshot?> ReadFileAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var snapshot = JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions);
            return snapshot?.Items is not null && snapshot.Items.All(item => item is not null
                && !string.IsNullOrWhiteSpace(item.CreatorKey) && !string.IsNullOrWhiteSpace(item.DisplayName)
                && Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                ? snapshot : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private string PathFor(FeedSubscription subscription)
        => Path.Combine(directory, Hash(JsonSerializer.Serialize(new[]
            { subscription.ProviderId, subscription.SourceId, subscription.FeedId, subscription.SubscriptionId })) + ".json");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Snapshot(int Version, string ProviderId, string SourceId, string SubscriptionId,
        string ContextKey, IReadOnlyList<PluginLiveItem> Items);
}
