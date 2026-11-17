using FeedPeek.Helpers;
using FeedPeek.Services;
using FeedPeek.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace FeedPeek;

public sealed partial class OobeWindow : Window
{
    private readonly StartupSettingsService _startup = new();
    private readonly HyperlinkButton _laterLink = new()
    {
        Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private bool _updatingPreferences;

    public OobeWindow()
    {
        InitializeComponent();
        _laterLink.Content = LocalizationHelper.GetString("OobeLaterLink.Content", "稍后再说");
        _laterLink.Click += LaterLink_Click;
        AutomationProperties.SetAutomationId(_laterLink, "OobeLaterLink");
        Title = LocalizationHelper.GetString("OobeWindowTitle", "欢迎使用 FeedPeek");
        var hwnd = WindowNative.GetWindowHandle(this);
        WindowIconHelper.ApplyTo(AppWindow, hwnd);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        AppWindow.ResizeClient(new SizeInt32((int)(720 * scale), (int)(680 * scale)));
        InfoBarAutoDismiss.CollapseWhenClosed(StartupInfoBar);
        Closed += (_, _) => ClearSources();

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    private void Wizard_Completed(object? sender, EventArgs e)
    {
        AppSettings.HasCompletedOobe = true;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        Close();
        ((App)Application.Current).ShowMainPanel(new PointInt32(area.X + area.Width - 1, area.Y + area.Height - 1));
    }

    private async void Wizard_StepChanged(object? sender, int index)
    {
        _laterLink.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 1)
        {
            SourcesHost.Content ??= new OobeSourcesControl();
        }
        else
        {
            ClearSources();
        }

        if (index != 2) return;
        _updatingPreferences = true;
        try
        {
            MainPanelHeightBox.SelectedIndex = AppSettings.MainPanelHeight switch { 600 => 0, 1000 => 2, _ => 1 };
            LiveSectionDisplayModeBox.SelectedIndex = (int)AppSettings.LiveSectionDisplayMode;
            AutoStartSwitch.IsEnabled = false;
            ApplyStartupStatus(await _startup.LoadAsync());
        }
        finally { _updatingPreferences = false; }
    }

    private void Wizard_Loaded(object sender, RoutedEventArgs e)
    {
        var back = FindElement(Wizard, "PART_BackButton")
            ?? throw new InvalidOperationException("MorphingWizard footer was not found.");
        var footer = (Grid)back.Parent;
        if (_laterLink.Parent is null) footer.Children.Add(_laterLink);
        _laterLink.Visibility = Wizard.CurrentIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement? FindElement(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindElement(child, name) is { } found) return found;
        }
        return null;
    }

    private void ClearSources()
    {
        (SourcesHost.Content as IDisposable)?.Dispose();
        SourcesHost.Content = null;
    }

    private async void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingPreferences) return;
        _updatingPreferences = true;
        AutoStartSwitch.IsEnabled = false;
        try
        {
            var result = await _startup.SetEnabledAsync(AutoStartSwitch.IsOn);
            ApplyStartupStatus(result.Status);
            if (!result.Succeeded)
                InfoBarAutoDismiss.Show(StartupInfoBar, LocalizationHelper.GetString("ConfigStartupRestoreFailed"), InfoBarSeverity.Warning);
        }
        finally { _updatingPreferences = false; }
    }

    private void ApplyStartupStatus(StartupSettingsStatus status)
    {
        AutoStartSwitch.IsOn = status.IsEnabled;
        AutoStartSwitch.IsEnabled = status.CanChange;
        StartupInfoBar.Message = status.Message;
        StartupInfoBar.IsOpen = !string.IsNullOrWhiteSpace(status.Message);
    }

    private void MainPanelHeightBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingPreferences && MainPanelHeightBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, out var height)) AppSettings.MainPanelHeight = height;
    }

    private void LaterLink_Click(object sender, RoutedEventArgs e) => Close();

    private void LiveSectionDisplayModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPreferences) return;
        AppSettings.LiveSectionDisplayMode = LiveSectionDisplayModeBox.SelectedIndex switch
        {
            1 => LiveSectionDisplayMode.Collapsed,
            2 => LiveSectionDisplayMode.Hidden,
            _ => LiveSectionDisplayMode.Expanded,
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
