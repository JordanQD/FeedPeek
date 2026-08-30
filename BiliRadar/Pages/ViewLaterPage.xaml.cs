using BiliRadar.Models;
using BiliRadar.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Pages;

public sealed partial class ViewLaterPage : Page, IMainPanelPage, IDisposable
{
    private readonly ObservableCollection<FeedItemRow> _items = [];
    private FeedPeekPanelSession? _session;
    private bool _isDisposed;

    public ViewLaterPage()
    {
        InitializeComponent();
        ItemsListView.ItemsSource = _items;
    }

    public void Initialize(FeedPeekPanelSession session) => _session = session;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || _isDisposed) return;
        var items = await _session.GetViewLaterItemsAsync(cancellationToken);
        if (_isDisposed) return;
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FeedItemRow item && _session is not null)
            await _session.MarkReadAndLaunchAsync(item);
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedItemRow item } || _session is null) return;
        await _session.SetSavedAsync(item, false);
        _items.Remove(item);
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ResetScrollPosition()
    {
        if (_items.Count > 0) ItemsListView.ScrollIntoView(_items[0]);
    }

    public void Deactivate() => _session = null;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _session = null;
        ItemsListView.ItemsSource = null;
        _items.Clear();
    }
}
