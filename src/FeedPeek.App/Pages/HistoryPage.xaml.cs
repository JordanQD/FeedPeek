using FeedPeek.Models;
using FeedPeek.Services;
using FeedPeek.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Pages;

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
        // Middle click is handled on the list itself, and with handledEventsToo, so a row that is
        // still being realised right after the panel opens cannot swallow the press.
        ItemsListView.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(ItemsListView_PointerPressed),
            handledEventsToo: true);
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
            await FeedItemMenuHelper.ShowItemMenuAsync(target, e, item, _session, MainPanelSection.History);
        }
    }

    private async void ItemsListView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || !e.GetCurrentPoint(ItemsListView).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (ResolveItemFromPointer(e.OriginalSource as DependencyObject) is not FeedItemRow item)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await _session.OpenItemInBackgroundAsync(item);
        }
        catch
        {
            // The session already reports the actionable error through its status notification.
        }
    }

    /// <summary>
    /// Resolves the pressed row through the list instead of through the element's DataContext.
    /// The list is rebuilt once the first refresh finishes, so right after the panel opens a row
    /// can still be settling; reading DataContext then would turn the press into a silent no-op.
    /// A press inside a button (the watch-later overlay) resolves to <see langword="null"/> so that
    /// button keeps its own behaviour.
    /// </summary>
    private FeedItemRow? ResolveItemFromPointer(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button)
            {
                return null;
            }

            if (node is ListViewItem container)
            {
                return ItemsListView.ItemFromContainer(container) as FeedItemRow;
            }
        }

        return null;
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
