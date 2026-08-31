using BiliRadar.Helpers;
using BiliRadar.Models;
using BiliRadar.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.ApplicationModel;

namespace BiliRadar.Pages;

public sealed partial class GeneralSettingsPage : Page
{
    private const string StartupTaskId = "FeedPeekPreviewStartupTask";
    private const string StartupRegistryName = "FeedPeek";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private bool _isLoadingSettings;
    private bool _useRegistryStartup;
    private StartupTask? _startupTask;

    public GeneralSettingsPage()
    {
        InitializeComponent();
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
        UpdateCustomLaunchWebPageUrlBoxState();
        await LoadStartupStateAsync();
        _isLoadingSettings = false;

        SettingsScrollView.ScrollTo(0, 0);
        InitLanguageSelector();
    }

    private void GeneralSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= GeneralSettingsPage_Loaded;
        Unloaded -= GeneralSettingsPage_Unloaded;
    }

    private async System.Threading.Tasks.Task LoadStartupStateAsync()
    {
        try
        {
            _startupTask = await StartupTask.GetAsync(StartupTaskId);
            _useRegistryStartup = false;
            AutoStartSwitch.IsOn = _startupTask.State is StartupTaskState.Enabled;
            AutoStartSwitch.IsEnabled = _startupTask.State is StartupTaskState.Disabled or StartupTaskState.Enabled;
            StartupInfoBar.IsOpen = _startupTask.State is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
            StartupInfoBar.Severity = InfoBarSeverity.Informational;
            StartupInfoBar.Message = _startupTask.State switch
            {
                StartupTaskState.DisabledByUser => LocalizationHelper.GetString("StartupDisabledByUser"),
                StartupTaskState.DisabledByPolicy => LocalizationHelper.GetString("StartupDisabledByPolicy"),
                _ => string.Empty,
            };
            return;
        }
        catch
        {
        }

        _startupTask = null;
        _useRegistryStartup = true;
        AutoStartSwitch.IsEnabled = true;
        AutoStartSwitch.IsOn = IsRegistryStartupEnabled();
        StartupInfoBar.IsOpen = true;
        StartupInfoBar.Severity = InfoBarSeverity.Informational;
        StartupInfoBar.Message = LocalizationHelper.GetString("StartupTaskFallback");
    }

    private async void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        await SetAutoStartEnabledAsync(AutoStartSwitch.IsOn);
    }

    private async System.Threading.Tasks.Task SetAutoStartEnabledAsync(bool isEnabled)
    {
        if (_useRegistryStartup)
        {
            SetRegistryStartupEnabled(isEnabled);
            return;
        }

        if (_startupTask is null)
        {
            _isLoadingSettings = true;
            AutoStartSwitch.IsOn = false;
            _isLoadingSettings = false;
            return;
        }

        try
        {
            if (isEnabled)
            {
                var state = await _startupTask.RequestEnableAsync();
                if (state is not StartupTaskState.Enabled)
                {
                    _isLoadingSettings = true;
                    AutoStartSwitch.IsOn = false;
                    _isLoadingSettings = false;
                }
            }
            else if (_startupTask.State is StartupTaskState.Enabled)
            {
                _startupTask.Disable();
            }
        }
        catch
        {
            _isLoadingSettings = true;
            AutoStartSwitch.IsOn = _startupTask.State is StartupTaskState.Enabled;
            _isLoadingSettings = false;
        }
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
        var picker = new FileSavePicker(((FrameworkElement)sender).XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"FeedPeek-config-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add(LocalizationHelper.GetString("FilePickerJsonLabel"), [".json"]);

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var export = await CreateSettingsExportAsync();
            var json = JsonSerializer.Serialize(export, JsonOptions);
            await File.WriteAllTextAsync(result.Path, json);
            SetConfigStatus(LocalizationHelper.Format("ExportedTo", result.Path));
        }
        catch (Exception ex)
        {
            SetConfigStatus(LocalizationHelper.Format("ExportFailed", ex.Message));
        }
    }

    private async void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(((FrameworkElement)sender).XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".json");

        var result = await picker.PickSingleFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(result.Path);
            var import = JsonSerializer.Deserialize<SettingsExport>(json, JsonOptions)
                ?? throw new InvalidOperationException(LocalizationHelper.GetString("InvalidConfigFile"));

            await ApplySettingsExportAsync(import);
            SetConfigStatus(LocalizationHelper.Format("ImportedFrom", result.Path));
        }
        catch (Exception ex)
        {
            SetConfigStatus(LocalizationHelper.Format("ImportFailed", ex.Message));
        }
    }

    private System.Threading.Tasks.Task<SettingsExport> CreateSettingsExportAsync()
    {
        return System.Threading.Tasks.Task.FromResult(new SettingsExport
        {
            ExportedAt = DateTimeOffset.Now,
            AutoStartEnabled = AutoStartSwitch.IsOn,
            RunningLaunchActionValue = AppSettings.RunningLaunchAction.ToString(),
            CustomLaunchWebPageUrl = AppSettings.CustomLaunchWebPageUrl,
            DefaultOpenPageValue = AppSettings.DefaultOpenPage.ToString(),
            SaveMainPanelPosition = AppSettings.SaveMainPanelPosition,
            MainPanelHeight = AppSettings.MainPanelHeight,
            NotificationCheckIntervalMinutes = AppSettings.NotificationCheckIntervalMinutes,
            NotificationTargetMode = AppSettings.NotificationTargetMode.ToString(),
            LiveSectionDisplayMode = AppSettings.LiveSectionDisplayMode.ToString(),
            VideoNotificationsEnabled = AppSettings.VideoNotificationsEnabled,
            LiveNotificationsEnabled = AppSettings.LiveNotificationsEnabled,
            CustomNotificationCreators = AppSettings.CustomNotificationCreators.ToList(),
        });
    }

    private async System.Threading.Tasks.Task ApplySettingsExportAsync(SettingsExport import)
    {
        AppSettings.RunningLaunchAction = ParseRunningLaunchAction(import.RunningLaunchActionValue);
        AppSettings.CustomLaunchWebPageUrl = import.CustomLaunchWebPageUrl;
        AppSettings.DefaultOpenPage = ParseEnum(import.DefaultOpenPageValue, DefaultOpenPage.Following);
        AppSettings.SaveMainPanelPosition = import.SaveMainPanelPosition;
        AppSettings.MainPanelHeight = NormalizeMainPanelHeight(import.MainPanelHeight);
        AppSettings.NotificationCheckIntervalMinutes = Math.Max(1, import.NotificationCheckIntervalMinutes);
        AppSettings.NotificationTargetMode = ParseEnum(import.NotificationTargetMode, NotificationTargetMode.AllFollowing);
        AppSettings.VideoNotificationsEnabled = import.VideoNotificationsEnabled;
        AppSettings.LiveNotificationsEnabled = import.LiveNotificationsEnabled;
        AppSettings.LiveSectionDisplayMode = ParseEnum(import.LiveSectionDisplayMode, LiveSectionDisplayMode.Expanded);
        AppSettings.CustomNotificationCreators = import.CustomNotificationCreators ?? [];

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
        UpdateCustomLaunchWebPageUrlBoxState();
        AutoStartSwitch.IsOn = import.AutoStartEnabled;
        _isLoadingSettings = false;

        await SetAutoStartEnabledAsync(import.AutoStartEnabled);
    }

    private static bool IsRegistryStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue(StartupRegistryName) is string value
            && string.Equals(value, GetRegistryStartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRegistryStartupEnabled(bool isEnabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (isEnabled)
        {
            key?.SetValue(StartupRegistryName, GetRegistryStartupCommand());
        }
        else
        {
            key?.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
        }
    }

    private static string GetRegistryStartupCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }

        return $"\"{path}\"";
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static RunningLaunchAction ParseRunningLaunchAction(string? value)
    {
        if (string.Equals(value, "OpenBilibiliWebPage", StringComparison.OrdinalIgnoreCase))
        {
            return RunningLaunchAction.OpenCustomWebPage;
        }

        return ParseEnum(value, RunningLaunchAction.OpenSettings);
    }

    private static int NormalizeMainPanelHeight(int value)
    {
        return value switch
        {
            600 or 800 or 1000 => value,
            _ => 800,
        };
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
        }));
    }

    private void SetConfigStatus(string message)
    {
        ConfigStatusText.Text = message;
        ConfigStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class SettingsExport
    {
        public DateTimeOffset ExportedAt { get; set; }

        public bool AutoStartEnabled { get; set; }

        [JsonPropertyName("RunningLaunchAction")]
        public string RunningLaunchActionValue { get; set; } = nameof(Services.RunningLaunchAction.OpenSettings);

        public string CustomLaunchWebPageUrl { get; set; } = "https://github.com/JordanQD/FeedPeek";

        [JsonPropertyName("DefaultOpenPage")]
        public string DefaultOpenPageValue { get; set; } = nameof(Services.DefaultOpenPage.Following);

        public bool SaveMainPanelPosition { get; set; }

        public int MainPanelHeight { get; set; } = 800;

        public int NotificationCheckIntervalMinutes { get; set; } = 15;

        public string NotificationTargetMode { get; set; } = nameof(Services.NotificationTargetMode.AllFollowing);

        public string LiveSectionDisplayMode { get; set; } = nameof(Services.LiveSectionDisplayMode.Expanded);

        public bool VideoNotificationsEnabled { get; set; } = true;

        public bool LiveNotificationsEnabled { get; set; } = true;

        public List<NotificationCreatorSubscription> CustomNotificationCreators { get; set; } = [];
    }
}
