using FeedPeek.Helpers;
using FeedPeek.Core.Configuration;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.IO;

namespace FeedPeek.Pages;

public sealed partial class GeneralSettingsPage : Page
{
    private Window? _onboardingWindow;

    private void OnboardingCard_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingWindow is not null) _onboardingWindow.Closed -= OnboardingWindow_Closed;
        _onboardingWindow = ((App)Application.Current).ShowOobeWindow();
        _onboardingWindow.Closed += OnboardingWindow_Closed;
    }

    private async void OnboardingWindow_Closed(object sender, WindowEventArgs args)
    {
        _onboardingWindow!.Closed -= OnboardingWindow_Closed;
        _onboardingWindow = null;
        PopulateSettingsControls();
        await LoadStartupStateAsync();
    }

    private readonly StartupSettingsService _startupSettings = new();

    private bool _isLoadingSettings;
    private bool _isInitializingKeepSourceFilter;
    private bool _configurationBusy;
    private bool _startupReady;

    public GeneralSettingsPage()
    {
        InitializeComponent();
        // 关闭状态的 InfoBar 自身仍是 Visible 并保留 Margin，会占位；统一让它跟随 IsOpen 折叠。
        InfoBarAutoDismiss.CollapseWhenClosed(StartupInfoBar);
        InfoBarAutoDismiss.CollapseWhenClosed(ConfigInfoBar);
        ExportConfigButton.IsEnabled = false;
        ImportConfigButton.IsEnabled = false;
        Loaded += GeneralSettingsPage_Loaded;
        Unloaded += GeneralSettingsPage_Unloaded;
    }

    private async void GeneralSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingSettings = true;
        RunningLaunchActionBox.SelectedIndex = AppSettings.RunningLaunchAction == RunningLaunchAction.OpenCustomWebPage ? 1 : 0;
        CustomLaunchWebPageUrlBox.Text = AppSettings.CustomLaunchWebPageUrl;
        DefaultOpenPageBox.SelectedIndex = (int)AppSettings.DefaultOpenPage;
        SaveMainPanelPositionSwitch.IsOn = AppSettings.SaveMainPanelPosition;
        MainPanelHeightBox.SelectedIndex = AppSettings.MainPanelHeight switch
        {
            600 => 0,
            1000 => 2,
            _ => 1,
        };
        LiveSectionDisplayModeBox.SelectedIndex = (int)AppSettings.LiveSectionDisplayMode;
        _isInitializingKeepSourceFilter = true;
        KeepSourceFilterSwitch.IsOn = AppSettings.KeepPanelSourceFilter;
        _isInitializingKeepSourceFilter = false;
        UpdateCustomLaunchWebPageUrlBoxState();
        await LoadStartupStateAsync();
        _isLoadingSettings = false;
        _startupReady = true;
        ExportConfigButton.IsEnabled = true;
        ImportConfigButton.IsEnabled = true;

        SettingsScrollView.ScrollTo(0, 0);
        InitLanguageSelector();
    }

    private void GeneralSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_onboardingWindow is not null) _onboardingWindow.Closed -= OnboardingWindow_Closed;
        _onboardingWindow = null;
        Loaded -= GeneralSettingsPage_Loaded;
        Unloaded -= GeneralSettingsPage_Unloaded;
    }

    private async System.Threading.Tasks.Task LoadStartupStateAsync()
    {
        ApplyStartupStatus(await _startupSettings.LoadAsync());
    }

    private void ApplyStartupStatus(StartupSettingsStatus status)
    {
        SetAutoStartSwitchState(status.IsEnabled);
        AutoStartSwitch.IsEnabled = !_configurationBusy && status.CanChange;
        SetStartupInfoBar(!string.IsNullOrEmpty(status.Message), status.Message);
    }

    /// <summary>
    /// InfoBar 关闭时只折叠模板内容，元素自身仍是 Visible 且保留 Margin，
    /// 会让它与下方卡片的间距比其它卡片多出 4px。这里显式同步 Visibility：
    /// 关闭时完全不参与布局，打开时仍保留 4px 间距。
    /// </summary>
    private void SetStartupInfoBar(bool isOpen, string message)
    {
        StartupInfoBar.IsOpen = isOpen;
        StartupInfoBar.Severity = InfoBarSeverity.Informational;
        StartupInfoBar.Message = message;
        StartupInfoBar.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (!await SetAutoStartEnabledAsync(AutoStartSwitch.IsOn))
            SetConfigStatus(LocalizationHelper.GetString("ConfigStartupRestoreFailed"), InfoBarSeverity.Warning);
    }

    private async System.Threading.Tasks.Task<bool> SetAutoStartEnabledAsync(bool isEnabled)
    {
        var result = await _startupSettings.SetEnabledAsync(isEnabled);
        ApplyStartupStatus(result.Status);
        return result.Succeeded;
    }

    private void SetAutoStartSwitchState(bool enabled)
    {
        var wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        try { AutoStartSwitch.IsOn = enabled; }
        finally { _isLoadingSettings = wasLoading; }
    }

    private void RunningLaunchActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.RunningLaunchAction = RunningLaunchActionBox.SelectedIndex == 1
            ? RunningLaunchAction.OpenCustomWebPage
            : RunningLaunchAction.OpenSettings;
        UpdateCustomLaunchWebPageUrlBoxState();
    }

    private void CustomLaunchWebPageUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.CustomLaunchWebPageUrl = CustomLaunchWebPageUrlBox.Text;
    }

    private void CustomLaunchWebPageUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isLoadingSettings = true;
        CustomLaunchWebPageUrlBox.Text = AppSettings.CustomLaunchWebPageUrl;
        _isLoadingSettings = false;
    }

    private void DefaultOpenPageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.DefaultOpenPage = DefaultOpenPageBox.SelectedIndex switch
        {
            1 => DefaultOpenPage.History,
            2 => DefaultOpenPage.ViewLater,
            _ => DefaultOpenPage.Following,
        };
    }

    private void KeepSourceFilterSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // Do not gate on _isLoadingSettings: programmatic IsOn assignment during load can
        // interleave with user clicks (LoadStartupStateAsync is async). A dedicated one-shot
        // guard covers the initial assignment only.
        if (_isInitializingKeepSourceFilter)
        {
            return;
        }

        AppSettings.KeepPanelSourceFilter = KeepSourceFilterSwitch.IsOn;
    }

    private void SaveMainPanelPositionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.SaveMainPanelPosition = SaveMainPanelPositionSwitch.IsOn;
    }

    private void UpdateCustomLaunchWebPageUrlBoxState()
    {
        CustomLaunchWebPageUrlBox.IsEnabled = RunningLaunchActionBox.SelectedIndex == 1;
    }

    private void MainPanelHeightBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (MainPanelHeightBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var height))
        {
            AppSettings.MainPanelHeight = height;
        }
    }

    private void LiveSectionDisplayModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.LiveSectionDisplayMode = LiveSectionDisplayModeBox.SelectedIndex switch
        {
            1 => LiveSectionDisplayMode.Collapsed,
            2 => LiveSectionDisplayMode.Hidden,
            _ => LiveSectionDisplayMode.Expanded,
        };
    }

    private async void ExportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_startupReady || _configurationBusy) return;
        SetConfigurationBusy(true);
        try
        {
            var picker = new FileSavePicker(((FrameworkElement)sender).XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"FeedPeek-config-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add(LocalizationHelper.GetString("FilePickerJsonLabel"), [".json"]);
            var result = await picker.PickSaveFileAsync();
            if (result is null) return;
            var export = await ConfigurationTransferService.ExportAsync(AutoStartSwitch.IsOn);
            var temporaryPath = result.Path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, export.ToJson());
                File.Move(temporaryPath, result.Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            SetConfigStatus(LocalizationHelper.Format("ExportedTo", result.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetConfigStatus(LocalizationHelper.Format("ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
        finally { SetConfigurationBusy(false); }
    }

    private async void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_startupReady || _configurationBusy) return;
        SetConfigurationBusy(true);
        try
        {
            var picker = new FileOpenPicker(((FrameworkElement)sender).XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            if (new FileInfo(result.Path).Length > 16 * 1024 * 1024)
                throw new InvalidOperationException(LocalizationHelper.GetString("InvalidConfigFile"));
            var document = ConfigurationDocument.Parse(await File.ReadAllTextAsync(result.Path));
            var previousLanguage = AppSettings.AppLanguage;
            await ConfigurationTransferService.ImportAsync(document);
            var startupRestored = document.Settings.AutoStartEnabled is not { } startup
                || await SetAutoStartEnabledAsync(startup);
            PopulateSettingsControls();
            if (!string.Equals(previousLanguage, AppSettings.AppLanguage, StringComparison.Ordinal))
                LocalizationHelper.SetLanguage(AppSettings.AppLanguage);
            var message = LocalizationHelper.Format("ImportedFrom", result.Path);
            if (document.IsLegacy) message += " " + LocalizationHelper.GetString("ConfigLegacyImported");
            if (!string.Equals(previousLanguage, AppSettings.AppLanguage, StringComparison.Ordinal))
                message += " " + LocalizationHelper.GetString("ConfigLanguageRestart");
            if (!startupRestored) message += " " + LocalizationHelper.GetString("ConfigStartupRestoreFailed");
            SetConfigStatus(message, startupRestored ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetConfigStatus(LocalizationHelper.Format("ImportFailed", ex.Message), InfoBarSeverity.Error);
        }
        finally { SetConfigurationBusy(false); }
    }

    private void SetConfigurationBusy(bool busy)
    {
        _configurationBusy = busy;
        foreach (var control in new Control[]
        {
            RunningLaunchActionBox, CustomLaunchWebPageUrlBox, DefaultOpenPageBox,
            SaveMainPanelPositionSwitch, MainPanelHeightBox, LiveSectionDisplayModeBox,
            KeepSourceFilterSwitch, LanguageSelectorBox,
        }) control.IsEnabled = !busy;
        AutoStartSwitch.IsEnabled = !busy && _startupSettings.CurrentStatus.CanChange;
        if (!busy) UpdateCustomLaunchWebPageUrlBoxState();
        ExportConfigButton.IsEnabled = !busy && _startupReady;
        ImportConfigButton.IsEnabled = !busy && _startupReady;
    }

    private void PopulateSettingsControls()
    {
        _isLoadingSettings = true;
        _isInitializingKeepSourceFilter = true;
        try
        {
            RunningLaunchActionBox.SelectedIndex = AppSettings.RunningLaunchAction == RunningLaunchAction.OpenCustomWebPage ? 1 : 0;
            CustomLaunchWebPageUrlBox.Text = AppSettings.CustomLaunchWebPageUrl;
            DefaultOpenPageBox.SelectedIndex = (int)AppSettings.DefaultOpenPage;
            SaveMainPanelPositionSwitch.IsOn = AppSettings.SaveMainPanelPosition;
            MainPanelHeightBox.SelectedIndex = AppSettings.MainPanelHeight switch { 600 => 0, 1000 => 2, _ => 1 };
            LiveSectionDisplayModeBox.SelectedIndex = (int)AppSettings.LiveSectionDisplayMode;
            KeepSourceFilterSwitch.IsOn = AppSettings.KeepPanelSourceFilter;
            LanguageSelectorBox.SelectedIndex = AppSettings.AppLanguage switch { "zh-CN" => 1, "zh-HK" => 2, _ => 0 };
            UpdateCustomLaunchWebPageUrlBoxState();
        }
        finally
        {
            _isInitializingKeepSourceFilter = false;
            _isLoadingSettings = false;
        }
    }

    private void InitLanguageSelector()
    {
        _isLoadingSettings = true;
        var current = AppSettings.AppLanguage;
        LanguageSelectorBox.SelectedIndex = current switch
        {
            "zh-CN" => 1,
            "zh-HK" => 2,
            _ => 0,
        };
        _isLoadingSettings = false;
    }

    private void LanguageSelectorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || LanguageSelectorBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (string.Equals(AppSettings.AppLanguage, tag, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppSettings.AppLanguage = tag;
        LocalizationHelper.SetLanguage(tag);

        SetConfigStatus(LocalizationHelper.Format("LanguageRestartMessage", tag switch
        {
            AppSettings.SystemLanguage => LocalizationHelper.GetString("GeneralLanguageSystemItem.Content", "跟随系统"),
            "zh-CN" => "简体中文",
            "zh-HK" => "繁體中文（香港）",
            _ => "简体中文",
        }), InfoBarSeverity.Informational);
    }

    private void SetConfigStatus(string message, InfoBarSeverity severity)
    {
        if (!string.IsNullOrWhiteSpace(message)) InfoBarAutoDismiss.Show(ConfigInfoBar, message, severity);
    }

}
