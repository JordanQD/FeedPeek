using FeedPeek.Models;
using FeedPeek.Pages;
using FeedPeek.Services;
using FeedPeek.Helpers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Controls;

public sealed partial class MainPanelControl : UserControl, IDisposable
{
    private readonly HashSet<IDisposable> _initializedPages = [];
    private CancellationTokenSource? _flyoutCts;
    private double _hostHeight = double.NaN;
    private int _previousSelectedPageIndex = -1;
    private bool _isFlyoutOpen;
    private bool _isDisposed;
    private bool _isSettingDefaultPage;

    public FeedPeekPanelSession Session { get; }

    /// <summary>
    /// Set by the tray host. Lets a page keep the flyout open across a focus change, for
    /// example when a link is opened in the background and the browser takes focus.
    /// </summary>
    public Action? KeepFlyoutOpen { get; set; }

    public MainPanelControl()
    {
        Session = new FeedPeekPanelSession();
        InitializeComponent();
        Session.StatusRaised += Session_StatusRaised;
        Session.BackgroundOpenRequested += Session_BackgroundOpenRequested;
        Session.PropertyChanged += Session_PropertyChanged;
        PluginAccountEvents.Changed += PluginAccountEvents_Changed;
        ContentFrame.Navigated += ContentFrame_Navigated;
        Loaded += MainPanelControl_Loaded;
        ApplyBusyState();
        SelectDefaultPage();
    }

    private void MainPanelControl_Loaded(object sender, RoutedEventArgs e) => ApplyPanelHeight();

    public void SetHostHeight(double height)
    {
        _hostHeight = Math.Max(1d, height);
        ApplyPanelHeight();
    }

    private void ApplyPanelHeight()
    {
        var height = _hostHeight;
        if (double.IsNaN(height))
        {
            var scale = XamlRoot?.RasterizationScale ?? 1d;
            height = Math.Min(
                DisplayArea.Primary.WorkArea.Height / scale - 80,
                AppSettings.MainPanelHeight);
        }

        RootGrid.Height = height;
        Height = height;
    }

    private void SelectDefaultPage()
    {
        var selectedItem = AppSettings.DefaultOpenPage switch
        {
            DefaultOpenPage.History => HistorySelectorItem,
            DefaultOpenPage.ViewLater => ViewLaterSelectorItem,
            _ => FollowingSelectorItem,
        };

        _isSettingDefaultPage = true;
        ContentSelectorBar.SelectedItem = selectedItem;
        selectedItem.IsSelected = true;
        _isSettingDefaultPage = false;
        NavigateToSelectedPage(resetScrollPosition: false);
    }

    public void OnFlyoutOpened()
    {
        _isFlyoutOpen = true;
        _flyoutCts?.Cancel();
        _flyoutCts?.Dispose();
        _flyoutCts = new CancellationTokenSource();

        if (!AppSettings.SaveMainPanelPosition)
        {
            SelectDefaultPage();
        }

        _ = InitializeAndRefreshAsync(_flyoutCts.Token);
    }

    public void OnFlyoutClosed()
    {
        _isFlyoutOpen = false;
        _flyoutCts?.Cancel();
    }

    public async Task RefreshCurrentPageAsync()
    {
        if (!_isFlyoutOpen || _isDisposed) return;
        var cancellationToken = _flyoutCts?.Token ?? CancellationToken.None;
        try
        {
            await Session.RefreshAllAsync(cancellationToken);
            await ActivateCurrentPageAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task InitializeAndRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Session.InitializeAsync(cancellationToken);
            await Session.RefreshAllAsync(cancellationToken);
            await ActivateCurrentPageAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await ActivateCurrentPageAsync(cancellationToken);
        }
    }

    private void ContentSelectorBar_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (_isSettingDefaultPage || _isDisposed) return;
        NavigateToSelectedPage(resetScrollPosition: true);
    }

    private void NavigateToSelectedPage(bool resetScrollPosition)
    {
        var currentIndex = ContentSelectorBar.Items.IndexOf(ContentSelectorBar.SelectedItem);
        if (currentIndex < 0) currentIndex = 0;

        var pageType = currentIndex switch
        {
            0 => typeof(FollowingPage),
            1 => typeof(HistoryPage),
            _ => typeof(ViewLaterPage),
        };

        if (ContentFrame.Content?.GetType() == pageType)
        {
            if (resetScrollPosition) ResetPageScrollPosition();
            if (_isFlyoutOpen) _ = ActivateCurrentPageAsync(_flyoutCts?.Token ?? CancellationToken.None);
            _previousSelectedPageIndex = currentIndex;
            return;
        }

        DisposeCurrentPage();
        var transition = currentIndex > _previousSelectedPageIndex
            ? SlideNavigationTransitionEffect.FromRight
            : SlideNavigationTransitionEffect.FromLeft;
        ContentFrame.Navigate(
            pageType,
            null,
            new SlideNavigationTransitionInfo { Effect = transition });
        _previousSelectedPageIndex = currentIndex;

        if (resetScrollPosition) ResetPageScrollPosition();
        if (_isFlyoutOpen) _ = ActivateCurrentPageAsync(_flyoutCts?.Token ?? CancellationToken.None);
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is not IMainPanelPage page) return;
        page.Initialize(Session);
        if (page is IDisposable disposable) _initializedPages.Add(disposable);
    }

    private Task ActivateCurrentPageAsync(CancellationToken cancellationToken)
        => ContentFrame.Content is IMainPanelPage page
            ? page.ActivateAsync(cancellationToken)
            : Task.CompletedTask;

    private void DisposeCurrentPage()
    {
        if (ContentFrame.Content is IDisposable disposable)
        {
            disposable.Dispose();
            _initializedPages.Remove(disposable);
        }
        else if (ContentFrame.Content is IMainPanelPage page)
        {
            page.Deactivate();
        }

        ContentFrame.Content = null;
    }

    private void ResetPageScrollPosition()
    {
        (ContentFrame.Content as FollowingPage)?.ResetScrollPosition();
        (ContentFrame.Content as HistoryPage)?.ResetScrollPosition();
        (ContentFrame.Content as ViewLaterPage)?.ResetScrollPosition();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshCurrentPageAsync();

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeedPeekPanelSession.IsBusy))
        {
            ApplyBusyState();
        }
    }

    /// <summary>
    /// Drives the flush top progress bar. The session raises <see cref="FeedPeekPanelSession.IsBusy"/>
    /// on the UI thread, so the bar can be toggled directly.
    /// </summary>
    private void ApplyBusyState()
        => RefreshProgressBar.Visibility = Session.IsBusy ? Visibility.Visible : Visibility.Collapsed;

    private void Session_BackgroundOpenRequested(object? sender, EventArgs e)
        => KeepFlyoutOpen?.Invoke();

    private void Session_StatusRaised(object? sender, FeedPeekStatus status)
    {
        if (_isDisposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            StatusNotificationLayer.Visibility = Visibility.Visible;
            StatusInfoBarText.Text = status.Message;
            StatusInfoBar.Severity = status.Severity;
            InfoBarAutoDismiss.Show(
                StatusInfoBar,
                () => StatusNotificationLayer.Visibility = Visibility.Collapsed);
        });
    }

    private void PluginAccountEvents_Changed(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_isDisposed) return;
            try
            {
                if (_isFlyoutOpen)
                {
                    await RefreshCurrentPageAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        });
    }

    private void StatusNotificationInfoBar_CloseButtonClick(InfoBar sender, object args)
        => InfoBarAutoDismiss.Dismiss(StatusInfoBar);

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isFlyoutOpen = false;
        _flyoutCts?.Cancel();
        _flyoutCts?.Dispose();
        _flyoutCts = null;
        Loaded -= MainPanelControl_Loaded;
        PluginAccountEvents.Changed -= PluginAccountEvents_Changed;
        Session.StatusRaised -= Session_StatusRaised;
        Session.BackgroundOpenRequested -= Session_BackgroundOpenRequested;
        Session.PropertyChanged -= Session_PropertyChanged;
        ContentFrame.Navigated -= ContentFrame_Navigated;
        DisposeCurrentPage();
        foreach (var page in _initializedPages) page.Dispose();
        _initializedPages.Clear();
        Session.Dispose();
        ImageSourceCache.Clear();
        WorkingSetTrimmer.RequestTrim();
    }
}
