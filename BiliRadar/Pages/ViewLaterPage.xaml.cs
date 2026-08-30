using BiliRadar.Models;
using BiliRadar.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Pages;

public sealed partial class ViewLaterPage : Page, IMainPanelPage, IDisposable
{
    private readonly IncrementalFeedItemCollection _items;
    private FeedPeekPanelSession? _session;
    private bool _isDisposed;

    public ViewLaterPage()
    {
        _items = new IncrementalFeedItemCollection(LoadMoreAsync);
        InitializeComponent();
        ItemsListView.ItemsSource = _items;
    }

    public void Initialize(FeedPeekPanelSession session) => _session = session;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || _isDisposed) return;
        var result = await _session.GetViewLaterPageAsync(cancellationToken);
        if (_isDisposed) return;
        _items.Replace(result.Items, result.HasMore);
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<FeedItemLoadResult> LoadMoreAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || _isDisposed)
        {
            return new FeedItemLoadResult([], false);
        }

        try
        {
            return await session.LoadMoreViewLaterItemsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new FeedItemLoadResult([], false);
        }
        catch
        {
            return new FeedItemLoadResult([.. _items], false);
        }
    }

    private async void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FeedItemRow item && _session is not null)
            await _session.MarkReadAndLaunchAsync(item);
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedItemRow item } || _session is null) return;
        try
        {
            await _session.SetSavedAsync(item, false);
            _items.Remove(item);
            EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            // The session already reports the actionable error through its status notification.
        }
    }

    public void ResetScrollPosition()
    {
        if (_items.Count > 0) ItemsListView.ScrollIntoView(_items[0]);
    }

    public void Deactivate()
    {
        _items.StopLoading();
        _session = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _items.StopLoading();
        _session = null;
        ItemsListView.ItemsSource = null;
        _items.Clear();
    }
}
