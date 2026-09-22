using FeedPeek.Helpers;
using FeedPeek.PluginContracts.Dtos;
using Microsoft.UI.Xaml.Media;
using System;

namespace FeedPeek.Models;

public sealed class FeedLiveItemRow
{
    private readonly string? _avatarUrl;
    private ImageSource? _avatarSource;

    public FeedLiveItemRow(PluginLiveItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        CreatorKey = item.CreatorKey;
        DisplayName = item.DisplayName;
        Url = item.Url;
        Title = item.Title ?? string.Empty;
        _avatarUrl = item.AvatarUrl;
    }

    public string CreatorKey { get; }

    public string DisplayName { get; }

    public string Url { get; }

    public string Title { get; }

    public ImageSource? AvatarSource
    {
        get
        {
            // BitmapImage must be created on the UI thread (lazy bind).
            _avatarSource ??= ImageSourceCache.GetAvatar(_avatarUrl);
            return _avatarSource;
        }
    }

    public string ToolTipText => string.IsNullOrWhiteSpace(Title) ? DisplayName : Title;

}
