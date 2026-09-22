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
using Windows.System;

namespace FeedPeek.Pages;

public sealed partial class FollowingPage : Page, IMainPanelPage, IDisposable
{
    private readonly IncrementalFeedItemCollection _items;
    private readonly System.Collections.ObjectModel.ObservableCollection<FeedLiveItemRow> _liveItems = [];
    private FeedPeekPanelSession? _session;
    private bool _isLiveSectionExpanded;
    private bool _isDisposed;

    public FollowingPage()
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
        LiveItemsRepeater.ItemsSource = _liveItems;
        _isLiveSectionExpanded = AppSettings.LiveSectionDisplayMode != LiveSectionDisplayMode.Collapsed;
    }

    public void Initialize(FeedPeekPanelSession session) => _session = session;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null || _isDisposed) return;
        var result = await session.GetFollowingPageAsync(cancellationToken);
        if (_isDisposed) return;
        _items.Reset(result.Items, result.HasMore);
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var extras = await session.GetFollowingExtrasAsync(cancellationToken);
        if (_isDisposed) return;
        _liveItems.Clear();
        // BitmapImage is a WinUI object and must be created on the UI thread.
        foreach (var item in extras.LiveItems) _liveItems.Add(new FeedLiveItemRow(item));
        ApplyLiveSectionState();
    }

    private void LiveSectionToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isLiveSectionExpanded = !_isLiveSectionExpanded;
        ApplyLiveSectionState();
    }

    private async void LiveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedLiveItemRow item }
            || !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        await Launcher.LaunchUriAsync(uri);
    }

    private async void ItemRoot_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FeedItemRow item } target && _session is not null)
        {
            await FeedItemMenuHelper.ShowItemMenuAsync(target, e, item, _session, MainPanelSection.Following);
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

    private void ApplyLiveSectionState()
    {
        var isHidden = AppSettings.LiveSectionDisplayMode == LiveSectionDisplayMode.Hidden;
        LiveCreatorsSection.Visibility = !isHidden && _liveItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LiveItemsScrollViewer.Visibility = _isLiveSectionExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        LiveSectionChevron.Glyph = _isLiveSectionExpanded ? "\uE70D" : "\uE76C";
        EmptyPanel.Visibility = _items.Count == 0 && (isHidden || _liveItems.Count == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task<FeedItemLoadResult> LoadMoreAsync(int offset, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || _isDisposed)
        {
            return new FeedItemLoadResult([], false);
        }

        try
        {
            return await session.LoadMoreFollowingItemsAsync(offset, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new FeedItemLoadResult([], false);
        }
        catch
        {
            return new FeedItemLoadResult([], false);
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
        LiveItemsRepeater.ItemsSource = null;
        _items.Clear();
        _liveItems.Clear();
    }
}
