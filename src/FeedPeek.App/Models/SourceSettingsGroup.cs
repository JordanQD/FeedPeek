using FeedPeek.Helpers;
using FeedPeek.PluginContracts.Manifest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Linq;

namespace FeedPeek.Models;

/// <summary>One direct source card or a plugin and its subscriptions, including unavailable plugins.</summary>
public sealed class SourceSettingsGroup
{
    public SourceSettingsGroup(string providerId, PluginSettingsRow? plugin,
        SourceSettingsRow? source, IReadOnlyList<SourceSettingsRow> sources)
    {
        ProviderId = providerId;
        Plugin = plugin;
        Source = source;
        Detail = SourceType(plugin, source, sources);
        // Auto-created website entries already open the same plugin details.
        // Represent them through the footer instead of repeating the plugin card.
        Subscriptions = sources.Count == 1 && sources[0].OpensPluginDetails ? [] : sources;
    }

    public string ProviderId { get; }
    public PluginSettingsRow? Plugin { get; }
    public SourceSettingsRow? Source { get; }
    public IReadOnlyList<SourceSettingsRow> Subscriptions { get; }
    public string DisplayName => Plugin?.DisplayName ?? Source?.DisplayName ?? ProviderId;
    public string Detail { get; }
    public ImageSource? IconSource => Plugin?.IconSource ?? Source?.IconSource ?? Subscriptions.FirstOrDefault()?.IconSource;
    public Visibility SourceVisibility => Source is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PluginVisibility => Source is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginSettingsVisibility => Plugin is null ? Visibility.Collapsed : Visibility.Visible;

    private static string SourceType(PluginSettingsRow? plugin, SourceSettingsRow? source,
        IReadOnlyList<SourceSettingsRow> sources)
    {
        if (source is not null) return source.Source.Category;
        var manifest = plugin?.Manifest;
        var isRoom = sources.Count > 0 ? sources.All(row => row.SortOrder == 1)
            : manifest is not null && (PluginManifestRules.ResolveRuntime(manifest) == PluginManifestRules.LiveParseRuntime
                || (manifest.Views.Count > 0 && manifest.Views.All(view => view.Kind == "live")));
        return LocalizationHelper.GetString(isRoom ? "NotificationRoomKind"
            : sources.Count > 0 || manifest is not null ? "NotificationWebsiteKind" : "SourcesPluginKind");
    }
}
