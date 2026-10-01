using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace FeedPeek;

public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth = 1360;
    private const int WindowHeight = 900;
    private const double StandardTitleBarHeight = 32;

    private readonly AppWindow _appWindow;
    private bool _isSynchronizingNavigationSelection;
    private SourceDetailPage? _sourceDetailPage;

    public SettingsWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Title = LocalizationHelper.GetString("SettingsWindow.Title", "FeedPeek 设置");
        WindowIconHelper.ApplyTo(_appWindow, hwnd);
        ResizeForCurrentDpi(hwnd);
        ConfigureTitleBar(hwnd);

        ApplySystemThemeToCaptionButtons();
        RootGrid.ActualThemeChanged += (_, _) => ApplySystemThemeToCaptionButtons();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        navFrame.Navigated += NavFrame_Navigated;
        GeneralNavItem.IsSelected = true;
        navFrame.Navigate(typeof(GeneralSettingsPage));
        RootGrid.SizeChanged += OnRootGridSizeChanged;
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        navView.PaneDisplayMode = e.NewSize.Width switch
        {
            < 640 => NavigationViewPaneDisplayMode.LeftMinimal,
            < 1008 => NavigationViewPaneDisplayMode.LeftCompact,
            _ => NavigationViewPaneDisplayMode.Left,
        };
    }

    public void ShowWindow()
    {
        Activate();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        // Detail pages change the selected home item to reflect their owning settings
        // section. That change must not be treated as a user navigation request.
        if (_isSynchronizingNavigationSelection)
        {
            return;
        }

        var pageType = tag switch
        {
            "general" => typeof(GeneralSettingsPage),
            "sources" => typeof(SourcesSettingsPage),
            "notification" => typeof(NotificationSettingsPage),
            "about" => typeof(AboutSettingsPage),
            _ => typeof(GeneralSettingsPage),
        };

        // Home-page navigation starts a new settings path. Detail pages are the only
        // pages that should remain in the back stack when the user changes sections.
        navFrame.BackStack.Clear();
        if (navFrame.CurrentSourcePageType != pageType)
        {
            navFrame.Navigate(pageType);
            navFrame.BackStack.Clear();
        }

        if (navView.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftMinimal)
        {
            navView.IsPaneOpen = false;
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        navView.IsPaneOpen = !navView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (navFrame.CanGoBack) navFrame.GoBack();
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (_sourceDetailPage is not null) _sourceDetailPage.SourceChanged -= SourceDetailPage_SourceChanged;
        _sourceDetailPage = navFrame.Content as SourceDetailPage;
        if (_sourceDetailPage is not null) _sourceDetailPage.SourceChanged += SourceDetailPage_SourceChanged;
        var pageType = e.SourcePageType;
        titleBar.IsBackButtonVisible = IsDetailPage(pageType);

        NavigationBreadcrumb.ItemsSource = pageType switch
        {
            var t when t == typeof(GeneralSettingsPage) => new[] { LocalizationHelper.GetString("GeneralPageTitle.Text", "通用") },
            var t when t == typeof(SourcesSettingsPage) => new[] { "来源" },
            var t when t == typeof(NotificationSettingsPage) => new[] { LocalizationHelper.GetString("NotificationPageTitle.Text", "通知") },
            var t when t == typeof(AboutSettingsPage) => new[] { LocalizationHelper.GetString("AboutPageTitle.Text", "关于") },
            var t when t == typeof(PluginDetailPage) && e.Parameter is PluginSettingsRow plugin => new[] { "来源", plugin.DisplayName },
            var t when t == typeof(SourceDetailPage) && e.Parameter is FeedSourceRow source => new[] { "来源", source.DisplayName },
            var t when t == typeof(NotificationDetailPage) && e.Parameter is NotificationRuleRow rule => new[]
            {
                LocalizationHelper.GetString("NotificationPageTitle.Text", "通知"),
                rule.DisplayName,
            },
            _ => Array.Empty<string>(),
        };

        SynchronizeNavigationSelection(GetNavigationRootTag(pageType));
    }

    private void SourceDetailPage_SourceChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, navFrame.Content) && sender is SourceDetailPage { SourceDisplayName: { } name })
            NavigationBreadcrumb.ItemsSource = new[] { "来源", name };
    }

    private void NavigationBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index != 0)
        {
            return;
        }

        var rootPageType = GetNavigationRootPageType(navFrame.CurrentSourcePageType);
        if (rootPageType is null || navFrame.CurrentSourcePageType == rootPageType)
        {
            return;
        }

        NavigateToExistingRoot(rootPageType);
    }

    private static bool IsDetailPage(Type? pageType)
        => pageType == typeof(PluginDetailPage)
            || pageType == typeof(SourceDetailPage)
            || pageType == typeof(NotificationDetailPage);

    private static string? GetNavigationRootTag(Type? pageType)
        => pageType switch
        {
            var t when t == typeof(SourcesSettingsPage)
                || t == typeof(SourceDetailPage)
                || t == typeof(PluginDetailPage) => "sources",
            var t when t == typeof(NotificationSettingsPage)
                || t == typeof(NotificationDetailPage) => "notification",
            var t when t == typeof(GeneralSettingsPage) => "general",
            var t when t == typeof(AboutSettingsPage) => "about",
            _ => null,
        };

    private static Type? GetNavigationRootPageType(Type? pageType)
        => GetNavigationRootTag(pageType) switch
        {
            "sources" => typeof(SourcesSettingsPage),
            "notification" => typeof(NotificationSettingsPage),
            "general" => typeof(GeneralSettingsPage),
            "about" => typeof(AboutSettingsPage),
            _ => null,
        };

    private void SynchronizeNavigationSelection(string? rootTag)
    {
        if (rootTag is null)
        {
            return;
        }

        NavigationViewItem? target = null;
        foreach (var item in navView.MenuItems)
        {
            if (item is NavigationViewItem navigationItem
                && string.Equals(navigationItem.Tag as string, rootTag, StringComparison.Ordinal))
            {
                target = navigationItem;
                break;
            }
        }

        if (target is null || ReferenceEquals(navView.SelectedItem, target))
        {
            return;
        }

        _isSynchronizingNavigationSelection = true;
        try
        {
            navView.SelectedItem = target;
        }
        finally
        {
            _isSynchronizingNavigationSelection = false;
        }
    }

    private void NavigateToExistingRoot(Type rootPageType)
    {
        var rootIndex = -1;
        for (var index = 0; index < navFrame.BackStack.Count; index++)
        {
            if (navFrame.BackStack[index].SourcePageType == rootPageType)
            {
                rootIndex = index;
            }
        }

        if (rootIndex >= 0)
        {
            // BackStack excludes the current page. Pop the detail chain until the
            // existing root page becomes current instead of appending another root.
            while (navFrame.CanGoBack && navFrame.BackStack.Count > rootIndex)
            {
                navFrame.GoBack();
            }

            return;
        }

        navFrame.BackStack.Clear();
        navFrame.Navigate(rootPageType);
        navFrame.BackStack.Clear();
    }

    private void ConfigureTitleBar(IntPtr hwnd)
    {
        this.ExtendsContentIntoTitleBar = true;

        var appTitleBar = _appWindow.TitleBar;
        appTitleBar.ExtendsContentIntoTitleBar = true;
        appTitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var scale = GetDpiForWindow(hwnd) / 96.0;
        var titleBarHeight = appTitleBar.Height > 0
            ? appTitleBar.Height / scale
            : StandardTitleBarHeight;

        titleBar.Height = titleBarHeight;
        titleBar.MinHeight = titleBarHeight;
        this.SetTitleBar(titleBar);
    }

    private void ApplySystemThemeToCaptionButtons()
    {
        var foregroundColor = RootGrid.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        _appWindow.TitleBar.ButtonForegroundColor = foregroundColor;
        _appWindow.TitleBar.ButtonHoverForegroundColor = foregroundColor;
        _appWindow.TitleBar.ButtonHoverBackgroundColor = RootGrid.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(24, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0);
    }

    private void ResizeForCurrentDpi(IntPtr hwnd)
    {
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(WindowWidth * scale),
            (int)Math.Round(WindowHeight * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
