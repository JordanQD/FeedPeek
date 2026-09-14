using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace FeedPeek.Models;

/// <summary>
/// One row of the combined "已添加来源" list: either an installed plugin (opens the plugin page) or an
/// added source such as an RSS feed or a pinned live room (opens the source page).
/// </summary>
public sealed class AddedSourceEntry
{
    private const string PluginGlyph = "\uE74C";
    private const string FeedGlyph = "\uE774";

    private AddedSourceEntry(string displayName, string detail, string? iconUrl, string glyph, object tag)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "未命名来源" : displayName;
        Detail = string.IsNullOrWhiteSpace(detail) ? "来源" : detail;
        IconUrl = iconUrl;
        Glyph = glyph;
        Tag = tag;
    }

    public string DisplayName { get; }

    public string Detail { get; }

    public string? IconUrl { get; }

    public string Glyph { get; }

    public object Tag { get; }

    /// <summary>The source's own icon when it has one, otherwise a glyph for its kind.</summary>
    /// <remarks>
    /// Built on every access, like <see cref="FeedSourceRow.SourceIcon"/>, instead of being cached: a
    /// <c>SettingsExpander</c> hosts its items in an <c>ItemsRepeater</c> that re-evaluates the item
    /// template, and handing the same element to a second host throws an ArgumentException from WinRT
    /// ("element already has a parent").
    /// </remarks>
    public IconElement Icon => CreateIcon();

    public static AddedSourceEntry ForPlugin(PluginSettingsRow plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        // The row shows what kind of entry it is instead of version/enabled state.
        return new AddedSourceEntry(plugin.DisplayName, "插件", plugin.IconUri, PluginGlyph, plugin);
    }

    public static AddedSourceEntry ForSource(FeedSourceRow source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AddedSourceEntry(source.DisplayName, source.Category, source.IconUrl, FeedGlyph, source);
    }

    private IconElement CreateIcon()
    {
        if (Uri.TryCreate(IconUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "ms-appdata" or "file")
        {
            return new ImageIcon { Source = new BitmapImage(uri) };
        }

        return new FontIcon { Glyph = Glyph, FontSize = 16 };
    }
}
