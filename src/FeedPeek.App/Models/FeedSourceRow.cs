using FeedPeek.Core.Models;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FeedPeek.Models;

public sealed class FeedSourceRow : INotifyPropertyChanged
{
    private int _itemCount;
    private int _unreadCount;

    public FeedSourceRow(FeedSourceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Source = summary.Source;
        _itemCount = summary.ItemCount;
        _unreadCount = summary.UnreadCount;
    }

    public SourceDefinition Source { get; }

    public string ProviderId => Source.ProviderId;

    public string SourceId => Source.SourceId;

    public string DisplayName => Source.DisplayName;

    public string Endpoint => Source.Options is not null
        && Source.Options.TryGetValue("rss.endpoint", out var endpoint)
            ? endpoint
            : ProviderId;

    public IReadOnlyList<FeedDefinition> Feeds => Source.Feeds;

    public int ItemCount
    {
        get => _itemCount;
        private set => SetProperty(ref _itemCount, value);
    }

    public int UnreadCount
    {
        get => _unreadCount;
        private set => SetProperty(ref _unreadCount, value);
    }

    public string ItemCountText => $"{ItemCount} 条";

    public string UnreadCountText => UnreadCount > 0
        ? $"{UnreadCount} 条未读"
        : "无未读";

    public string SourcesDetail => $"{Endpoint} · {ItemCountText} · {UnreadCountText}";

    public string Category => string.Equals(ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal)
        ? "RSS / Atom"
        : "插件";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(FeedSourceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Source.ProviderId != ProviderId || summary.Source.SourceId != SourceId)
        {
            throw new InvalidOperationException("The source summary does not match this row.");
        }

        ItemCount = summary.ItemCount;
        UnreadCount = summary.UnreadCount;
    }

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(ItemCount))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourcesDetail)));
        }
        else if (propertyName == nameof(UnreadCount))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourcesDetail)));
        }
    }
}
