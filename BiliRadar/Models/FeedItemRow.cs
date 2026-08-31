using FeedPeek.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BiliRadar.Models;

public sealed class FeedItemRow : INotifyPropertyChanged
{
    private bool _isRead;
    private bool _isSaved;

    public FeedItemRow(
        StoredFeedItem storedItem,
        string? sourceName = null,
        string? sourceIconUrl = null,
        bool preferAuthorAttribution = false)
    {
        ArgumentNullException.ThrowIfNull(storedItem);
        Item = storedItem.Item;
        SubscriptionId = storedItem.SubscriptionId;
        FirstSeenAt = storedItem.FirstSeenAt;
        LastSeenAt = storedItem.LastSeenAt;
        _isRead = storedItem.IsRead;
        _isSaved = storedItem.IsSaved;
        SourceName = string.IsNullOrWhiteSpace(sourceName) ? Item.SourceId : sourceName;
        ImageSource = CreateImageSource(Item.ImageUrl);
        SourceIconSource = CreateImageSource(sourceIconUrl);
        AttributionName = preferAuthorAttribution ? Item.AuthorName : SourceName;
        AttributionIconSource = preferAuthorAttribution
            ? CreateImageSource(Item.AuthorImageUrl)
            : SourceIconSource;
    }

    public FeedItem Item { get; }

    public string SubscriptionId { get; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    public FeedItemKey Key => Item.Key;

    public string ProviderId => Item.ProviderId;

    public string SourceId => Item.SourceId;

    public string SourceName { get; }

    public string FeedId => Item.FeedId;

    public string ItemId => Item.ItemId;

    public string Title => Item.Title;

    public string AuthorName => Item.AuthorName;

    public string AuthorUrl => Item.AuthorUrl ?? string.Empty;

    public string Url => Item.Url;

    public DateTimeOffset PublishedAt => Item.PublishedAt;

    public string Summary => Item.Summary ?? string.Empty;

    public string ImageUrl => Item.ImageUrl ?? string.Empty;

    public ImageSource? ImageSource { get; }

    public ImageSource? SourceIconSource { get; }

    public string AttributionName { get; }

    public ImageSource? AttributionIconSource { get; }

    public string AuthorImageUrl => Item.AuthorImageUrl ?? string.Empty;

    public string DurationText => Item.DurationText ?? string.Empty;

    public Visibility SummaryVisibility => string.IsNullOrWhiteSpace(Summary)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility DurationVisibility => string.IsNullOrWhiteSpace(DurationText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool HasImage => !string.IsNullOrWhiteSpace(Item.ImageUrl);

    public bool IsRead
    {
        get => _isRead;
        private set => SetProperty(ref _isRead, value);
    }

    public bool IsUnread => !IsRead;

    public bool IsSaved
    {
        get => _isSaved;
        private set => SetProperty(ref _isSaved, value);
    }

    public double UnreadOpacity => IsUnread ? 1d : 0d;

    public string ReadStateText => IsRead ? string.Empty : "未读";

    public string PublishedText => FormatPublishedTime(PublishedAt);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetRead(bool isRead)
    {
        IsRead = isRead;
    }

    public void SetSaved(bool isSaved)
    {
        IsSaved = isSaved;
    }

    public void RefreshPublishedText()
    {
        OnPropertyChanged(nameof(PublishedText));
    }

    private static string FormatPublishedTime(DateTimeOffset publishedAt)
    {
        var localTime = publishedAt.ToLocalTime();
        var delta = DateTimeOffset.Now - localTime;
        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
        }

        if (delta.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalHours)} 小时前";
        }

        return localTime.Year == DateTimeOffset.Now.Year
            ? localTime.ToString("M-d HH:mm")
            : localTime.ToString("yyyy-M-d HH:mm");
    }

    private static ImageSource? CreateImageSource(string? imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new BitmapImage(uri);
    }

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsRead))
        {
            OnPropertyChanged(nameof(IsUnread));
            OnPropertyChanged(nameof(UnreadOpacity));
            OnPropertyChanged(nameof(ReadStateText));
        }
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
