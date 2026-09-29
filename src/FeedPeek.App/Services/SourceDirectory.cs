using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedPeek.Services;

/// <summary>One filterable source row for the panel menus and open-site action.</summary>
public sealed record SourceCatalogEntry(
    string ProviderId,
    string SourceId,
    string DisplayName,
    string? IconUrl,
    string? WebsiteUrl,
    string? SuggestedSiteRoot,
    bool IsFilterable);

/// <summary>
/// Process-wide source directory so the flyout can fill filter/open menus synchronously without
/// waiting for <see cref="FeedPeekPanelSession"/> initialization. Safe to read from the UI thread;
/// loads and refreshes run off it.
/// </summary>
public static class SourceDirectory
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private static IReadOnlyList<SourceCatalogEntry> _current = [];
    private static bool _loaded;

    public static event EventHandler? Updated;

    public static IReadOnlyList<SourceCatalogEntry> Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    public static bool IsLoaded
    {
        get
        {
            lock (Gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>Reload from SQLite + plugin install state + LocalSettings, then raise <see cref="Updated"/>.</summary>
    public static async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            lock (Gate)
            {
                _current = entries;
                _loaded = true;
            }

            Updated?.Invoke(null, EventArgs.Empty);
        }
        finally
        {
            LoadGate.Release();
        }
    }

    /// <summary>
    /// Fire-and-forget refresh after a source/plugin/icon/website change. Safe to call from UI handlers.
    /// </summary>
    public static void Refresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadAsync().ConfigureAwait(false);
            }
            catch
            {
                // Keep the previous snapshot if a refresh fails.
            }
        });
    }

    public static void NotifyUpdated()
        => Updated?.Invoke(null, EventArgs.Empty);

    private static async Task<IReadOnlyList<SourceCatalogEntry>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "FeedPeek",
            "feedpeek.db");

        var entries = new List<SourceCatalogEntry>();
        var pluginWebsites = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pluginIcons = new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            var installations = new PluginInstallationService();
            foreach (var plugin in await installations.GetEntriesAsync(cancellationToken).ConfigureAwait(false))
            {
                pluginIcons[plugin.PluginId] = AppSettings.GetPluginIcon(plugin.PluginId) ?? plugin.IconUri;
                if (!string.IsNullOrWhiteSpace(plugin.Manifest?.Website))
                {
                    pluginWebsites[plugin.PluginId] = AppSettings.GetPluginWebsite(plugin.PluginId)
                        ?? plugin.Manifest.Website;
                }
            }
        }
        catch
        {
            // Plugin listing is optional — RSS sources can still be shown.
        }

        if (!File.Exists(databasePath))
        {
            return FilterAndSort(entries);
        }

        await using var store = new SqliteFeedStore(databasePath);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var summaries = await store.GetSourceSummariesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var summary in summaries)
        {
            var source = summary.Source;
            var isRss = string.Equals(source.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal);
            var iconUrl = ReadOption(source, "source.icon") ?? ReadOption(source, "rss.icon");
            if (string.IsNullOrWhiteSpace(iconUrl) && !isRss
                && pluginIcons.TryGetValue(source.ProviderId, out var pluginIcon))
            {
                iconUrl = pluginIcon;
            }

            var website = ReadOption(source, "source.website")
                ?? ReadOption(source, "website")
                ?? ReadOption(source, "homepage")
                ?? ReadOption(source, "home");
            if (string.IsNullOrWhiteSpace(website) && !isRss
                && pluginWebsites.TryGetValue(source.ProviderId, out var pluginWebsite))
            {
                website = pluginWebsite;
            }

            entries.Add(new SourceCatalogEntry(
                source.ProviderId,
                source.SourceId,
                source.DisplayName,
                NormalizeHttpOrNull(iconUrl),
                NormalizeHttpOrNull(website),
                InferSiteRoot(source),
                IsFilterable(source)));
        }

        return FilterAndSort(entries);
    }

    private static IReadOnlyList<SourceCatalogEntry> FilterAndSort(List<SourceCatalogEntry> entries)
        => entries
            .Where(item => item.IsFilterable)
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool IsFilterable(SourceDefinition source)
        => string.Equals(source.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal)
            || source.Feeds.Any(feed => !string.Equals(feed.FeedId, PluginFeedIds.Live, StringComparison.Ordinal));

    private static string? ReadOption(SourceDefinition source, string key)
        => source.Options is not null && source.Options.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

    private static string? NormalizeHttpOrNull(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "ms-appdata" or "file"
                ? uri.AbsoluteUri
                : null;

    private static string? InferSiteRoot(SourceDefinition source)
    {
        if (!string.Equals(source.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal))
        {
            return null;
        }

        var endpoint = ReadOption(source, "rss.endpoint");
        if (endpoint is null
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
        {
            Path = "/",
        }.Uri.AbsoluteUri;
    }
}
