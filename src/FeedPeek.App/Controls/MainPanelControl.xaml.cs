using FeedPeek.Models;
using FeedPeek.Pages;
using FeedPeek.Services;
using FeedPeek.Helpers;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.Rss;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Controls;

public sealed partial class MainPanelControl : UserControl, IDisposable
{
    private readonly HashSet<IDisposable> _initializedPages = [];
    private readonly List<(string Key, string Text, string? IconUrl)> _filterEntries = [];
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
        ApplyPersistedSourceFilter();
        Session.StatusRaised += Session_StatusRaised;
        Session.BackgroundOpenRequested += Session_BackgroundOpenRequested;
        Session.PropertyChanged += Session_PropertyChanged;
        PluginAccountEvents.Changed += PluginAccountEvents_Changed;
        ContentFrame.Navigated += ContentFrame_Navigated;
        Loaded += MainPanelControl_Loaded;
        ApplyBusyState();
        SelectDefaultPage();
    }

    private void ApplyPersistedSourceFilter()
    {
        Session.SourceFilter = AppSettings.PanelSourceFilter;
        UpdateFilterButtonVisual();
    }

    private static bool IsFilterableSource(FeedSourceRow row)
        => string.Equals(row.ProviderId, RssProviderInfo.ProviderId, StringComparison.Ordinal)
            || row.Feeds.Any(feed => !string.Equals(feed.FeedId, PluginFeedIds.Live, StringComparison.Ordinal));

    private async void FilterMenuFlyout_Opening(object sender, object e)
    {
        RebuildFilterMenuFromCache();
        if (_filterEntries.Count == 0)
        {
            try
            {
                await RefreshFilterEntriesAsync();
            }
            catch
            {
                // Keep the default "all" entry if source loading fails.
            }
        }
    }

    private async Task RefreshFilterEntriesAsync()
    {
        if (_isDisposed) return;

        var sources = await Session.GetSourcesAsync(_flyoutCts?.Token ?? CancellationToken.None);
        if (_isDisposed) return;

        // Plugin package / user plugin icons live on PluginManagementEntry, not Source.Options.
        // Settings > Sources shows those via PluginSettingsRow; the filter must use the same path.
        var pluginIcons = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (var plugin in await Session.GetPluginsAsync(_flyoutCts?.Token ?? CancellationToken.None))
            {
                pluginIcons[plugin.PluginId] = AppSettings.GetPluginIcon(plugin.PluginId) ?? plugin.IconUri;
            }
        }
        catch
        {
            // Plugin listing is optional for the filter list itself.
        }

        if (_isDisposed) return;

        _filterEntries.Clear();
        foreach (var source in sources.Where(IsFilterableSource)
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var iconUrl = source.IconUrl;
            if (string.IsNullOrWhiteSpace(iconUrl)
                && pluginIcons.TryGetValue(source.ProviderId, out var pluginIcon))
            {
                iconUrl = pluginIcon;
            }

            _filterEntries.Add(($"{source.ProviderId}|{source.SourceId}", source.DisplayName, iconUrl));
        }

        RebuildFilterMenuFromCache();
    }

    private void RebuildFilterMenuFromCache()
    {
        while (FilterMenuFlyout.Items.Count > 2)
        {
            FilterMenuFlyout.Items.RemoveAt(FilterMenuFlyout.Items.Count - 1);
        }

        var current = Session.SourceFilter ?? AppSettings.PanelSourceFilter;
        var stillExists = _filterEntries.Any(entry =>
            string.Equals(entry.Key, current, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(current) && _filterEntries.Count > 0 && !stillExists)
        {
            AppSettings.PanelSourceFilter = null;
            Session.SourceFilter = null;
            current = null;
        }

        FilterAllItem.IsChecked = string.IsNullOrWhiteSpace(current);

        foreach (var (key, text, _) in _filterEntries)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = "PanelSourceFilter",
                Text = text,
                Tag = key,
                IsChecked = string.Equals(current, key, StringComparison.Ordinal),
            };
            item.Click += FilterMenuItem_Click;
            FilterMenuFlyout.Items.Add(item);
        }

        UpdateFilterButtonVisual();
    }

    private async void FilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item)
        {
            return;
        }

        var key = item == FilterAllItem || item.Tag is not string tag
            ? null
            : tag;

        AppSettings.PanelSourceFilter = AppSettings.KeepPanelSourceFilter ? key : null;
        Session.SourceFilter = key;
        UpdateFilterButtonVisual();
        await RefreshCurrentPageAsync();
    }

    private void UpdateFilterButtonVisual()
    {
        var current = Session.SourceFilter;
        var filtered = !string.IsNullOrWhiteSpace(current);
        ToolTipService.SetToolTip(
            FilterButton,
            filtered ? "筛选来源（已筛选）" : "筛选来源");

        string? iconUrl = null;
        if (filtered)
        {
            foreach (var (key, _, url) in _filterEntries)
            {
                if (string.Equals(key, current, StringComparison.Ordinal))
                {
                    iconUrl = url;
                    break;
                }
            }
        }

        var bitmap = iconUrl is null ? null : ImageSourceCache.GetIcon(iconUrl);
        FilterButton.Content = bitmap is not null
            ? new ImageIcon
            {
                Source = bitmap,
                Width = 16,
                Height = 16,
            }
            : new FontIcon
            {
                FontSize = 15,
                Glyph = "",
            };
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
            await RefreshFilterEntriesAsync();
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
