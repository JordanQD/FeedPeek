using FeedPeek.PluginContracts.Dtos;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace FeedPeek.Models;

public sealed class FeedLiveItemRow
{
    public FeedLiveItemRow(PluginLiveItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        CreatorKey = item.CreatorKey;
        DisplayName = item.DisplayName;
        Url = item.Url;
        Title = item.Title ?? string.Empty;
        AvatarSource = CreateImageSource(item.AvatarUrl);
    }

    public string CreatorKey { get; }

    public string DisplayName { get; }

    public string Url { get; }

    public string Title { get; }

    public ImageSource? AvatarSource { get; }

    public string ToolTipText => string.IsNullOrWhiteSpace(Title) ? DisplayName : Title;

    private static ImageSource? CreateImageSource(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
                ? new BitmapImage(uri)
                : null;
}
