using FeedPeek.Helpers;
using FeedPeek.Rss;
using FeedPeek.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;

namespace FeedPeek.Models;

/// <summary>A source as it appears in settings, with its provider's name and icon.</summary>
public sealed class SourceSettingsRow
{
    private readonly string? _sourceIconUrl;
    private readonly string? _pluginIconUrl;

    public SourceSettingsRow(FeedSourceRow source, PluginSettingsRow? plugin)
    {
        Source = source;
        Plugin = plugin;
        // FeedSourceRow.IconUrl guesses /favicon.ico when the feed has no icon.
        // A missing favicon still creates a BitmapImage, leaving SettingsCard blank.
        // Only use icons actually supplied by the source or its plugin here.
        _sourceIconUrl = ReadSourceOption(source, "source.icon")
            ?? ReadSourceOption(source, "rss.icon");
        _pluginIconUrl = plugin?.IconUri;

        if (string.Equals(source.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal))
        {
            SortOrder = 2;
            Detail = source.Category;
        }
        else if (source.Source.Options?.ContainsKey("roomIds") == true
            && source.Feeds.Any(feed => string.Equals(feed.Kind, "live", StringComparison.Ordinal)))
        {
            SortOrder = 1;
            Detail = plugin is null ? "直播间" : $"直播间 · {plugin.DisplayName}";
        }
        else
        {
            SortOrder = 0;
            Detail = plugin is null ? "网站订阅" : $"网站订阅 · {plugin.DisplayName}";
        }

        OpensPluginDetails = SortOrder == 0 && plugin is not null
            && AppSettings.HasAutoCreatedPluginSource(plugin.PluginId);
    }

    public FeedSourceRow Source { get; }
    public PluginSettingsRow? Plugin { get; }
    public bool OpensPluginDetails { get; }
    public string DisplayName => Source.DisplayName;
    public string Detail { get; }
    public int SortOrder { get; }

    public ImageSource? IconSource => ImageSourceCache.GetIcon(_sourceIconUrl)
        ?? ImageSourceCache.GetIcon(_pluginIconUrl);

    public string Initials => IconSource is null
        ? new string(DisplayName.Trim().Take(2).Select(char.ToUpperInvariant).ToArray())
        : string.Empty;

    private static string? ReadSourceOption(FeedSourceRow source, string key)
        => source.Source.Options is { } options
            && options.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
}
