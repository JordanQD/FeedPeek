using BiliRadar.Models;
using BiliRadar.Services;
using BiliRadar.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliRadar.Pages;

public sealed partial class HistoryPage : Page, IMainPanelPage, IDisposable
{
    private readonly IncrementalFeedItemCollection _items;
    private FeedPeekPanelSession? _session;
    private bool _isDisposed;

    public HistoryPage()
    {
        _items = new IncrementalFeedItemCollection(LoadMoreAsync);
        InitializeComponent();
        ItemsListView.ItemsSource = _items;
    }

    public void Initialize(FeedPeekPanelSession session) => _session = session;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || _isDisposed) return;
        var result = await _session.GetHistoryPageAsync(cancellationToken);
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
            return await session.LoadMoreHistoryItemsAsync(cancellationToken);
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FeedItemRow item } && _session is not null)
        {
            try
            {
                await _session.SetSavedAsync(item, true);
            }
            catch
            {
                // The session already reports the actionable error through its status notification.
            }
        }
    }

    private async void ItemRoot_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FeedItemRow item } target && _session is not null)
        {
            await FeedItemMenuHelper.ShowCreatorMenuAsync(target, e, item, _session);
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
