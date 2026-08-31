using BiliRadar.Models;
using BiliRadar.Services;
using FeedPeek.PluginContracts.Manifest;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace BiliRadar.Pages;

public sealed partial class SourcesSettingsPage : Page, IDisposable
{
    private readonly ObservableCollection<FeedSourceRow> _sources = [];
    private readonly ObservableCollection<PluginSettingsRow> _plugins = [];
    private readonly FeedPeekPanelSession _session = new();
    private readonly CookieStore _cookieStore = new();
    private readonly FeedPeekPluginRuntime _accountRuntime;
    private readonly PluginInstallationService _installations = new();
    private WebSignInWindow? _signInWindow;
    private bool _pluginReloadRequired;
    private bool _disposed;

    public SourcesSettingsPage()
    {
        _accountRuntime = new FeedPeekPluginRuntime(_cookieStore);
        InitializeComponent();
        SourcesListView.ItemsSource = _sources;
        PluginsListView.ItemsSource = _plugins;
        Loaded += SourcesSettingsPage_Loaded;
        Unloaded += SourcesSettingsPage_Unloaded;
    }

    private async void SourcesSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _session.StatusRaised += Session_StatusRaised;
        await ReloadAsync();
    }

    private void SourcesSettingsPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private async Task ReloadAsync()
    {
        try
        {
            await _session.InitializeAsync();
            await _accountRuntime.LoadAsync(default);
            var entries = await _session.GetPluginsAsync();
            _plugins.Clear();
            foreach (var entry in entries.OrderBy(item => item.DisplayName))
                _plugins.Add(new PluginSettingsRow(entry));
            await ReloadSourcesAsync();
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddRssSourceAsync();

    private async void EndpointTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await AddRssSourceAsync();
    }

    private async Task AddRssSourceAsync()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowStatus("请输入网站或 RSS 地址。", InfoBarSeverity.Warning);
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _session.AddRssSubscriptionAsync(endpoint);
            EndpointTextBox.Text = string.Empty;
            await ReloadSourcesAsync();
            ShowStatus("RSS 来源已添加。", InfoBarSeverity.Success);
        });
    }

    private async void InstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await RunBusyAsync(async () =>
        {
            var installed = await _installations.InstallAsync(folder.Path);
            RequireSettingsReload($"已安装 {installed.DisplayName}。关闭并重新打开设置后可配置来源。");
        });
    }

    private async void TogglePluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginSettingsRow plugin }) return;
        await RunBusyAsync(async () =>
        {
            await _installations.SetEnabledAsync(plugin.PluginId, !plugin.IsEnabled);
            RequireSettingsReload($"插件已{(plugin.IsEnabled ? "停用" : "启用")}；关闭设置和托盘面板后重新打开即可生效。");
        });
    }

    private async void UninstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginSettingsRow plugin }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "卸载插件",
            Content = $"卸载 {plugin.DisplayName}？该插件的来源和本地缓存也会删除。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunBusyAsync(async () =>
        {
            await _session.ClearPluginCredentialsAsync(
                plugin.PluginId,
                GetSharedCredentialProfileId(plugin.PluginId));
            await _session.DeletePluginSourcesAsync(plugin.PluginId);
            await _installations.UninstallAsync(plugin.PluginId);
            await ReloadSourcesAsync();
            RequireSettingsReload("插件已卸载；关闭并重新打开设置后列表会更新。");
        });
    }

    private async void PluginLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginSettingsRow plugin }) return;
        var profileId = GetSharedCredentialProfileId(plugin.PluginId);
        try
        {
            if (await _accountRuntime.HasCredentialsAsync(plugin.PluginId, profileId))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"退出 {plugin.DisplayName}",
                    Content = "退出后，依赖该登录状态的来源及其本地内容会被移除。",
                    PrimaryButtonText = "退出登录",
                    CloseButtonText = "取消",
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                await _session.ClearPluginCredentialsAsync(plugin.PluginId, profileId);
                await ReloadSourcesAsync();
                ShowStatus("已退出登录并清除需要登录的来源内容。", InfoBarSeverity.Success);
                return;
            }
            _signInWindow = new WebSignInWindow(_cookieStore, plugin.PluginId, profileId);
            _signInWindow.SignInSucceeded += SignInWindow_SignInSucceeded;
            _signInWindow.Closed += SignInWindow_Closed;
            _signInWindow.Activate();
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private async void AddPluginSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginSettingsRow plugin } || plugin.Manifest is null) return;
        var displayName = new TextBox { Header = "来源名称", Text = plugin.DisplayName };
        var fields = new Dictionary<PluginSourceFieldManifest, TextBox>();
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(displayName);
        foreach (var field in plugin.Manifest.SourceConfiguration?.Fields ?? [])
        {
            var input = new TextBox
            {
                Header = field.Required ? $"{field.Label} *" : field.Label,
                PlaceholderText = field.Placeholder,
                Text = field.DefaultValue ?? string.Empty,
            };
            fields[field] = input;
            panel.Children.Add(input);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"添加 {plugin.DisplayName} 来源",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, input) in fields)
        {
            var value = input.Text.Trim();
            if (field.Required && string.IsNullOrWhiteSpace(value))
            {
                ShowStatus($"请填写“{field.Label}”。", InfoBarSeverity.Warning);
                return;
            }
            if (field.Kind == "url" && value.Length > 0
                && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowStatus($"“{field.Label}”需要填写 HTTPS 地址。", InfoBarSeverity.Warning);
                return;
            }
            if (value.Length > 0) options[field.Key] = value;
        }
        string? profileId = null;
        if (plugin.Manifest.Auth is not null)
        {
            var sharedProfileId = GetSharedCredentialProfileId(plugin.PluginId);
            if (await _accountRuntime.HasCredentialsAsync(plugin.PluginId, sharedProfileId))
                profileId = sharedProfileId;
            else if (plugin.Manifest.Auth.Required)
            {
                ShowStatus("此网站来源需要先登录。", InfoBarSeverity.Warning);
                return;
            }
        }
        await RunBusyAsync(async () =>
        {
            await _session.AddPluginSourceAsync(
                plugin.PluginId,
                string.IsNullOrWhiteSpace(displayName.Text) ? plugin.DisplayName : displayName.Text.Trim(),
                options,
                profileId);
            await ReloadSourcesAsync();
            ShowStatus("网站来源已添加。", InfoBarSeverity.Success);
        });
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedSourceRow source }) return;
        await RunBusyAsync(async () =>
        {
            await _session.DeleteSourceAsync(source);
            _sources.Remove(source);
            UpdateEmptyState();
            ShowStatus("来源已删除。", InfoBarSeverity.Success);
        });
    }

    private void SignInWindow_SignInSucceeded(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => ShowStatus("登录状态已保存，可以添加此网站的来源。", InfoBarSeverity.Success));

    private void SignInWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_signInWindow is null) return;
        _signInWindow.SignInSucceeded -= SignInWindow_SignInSucceeded;
        _signInWindow.Closed -= SignInWindow_Closed;
        _signInWindow = null;
    }

    private async Task ReloadSourcesAsync()
    {
        var sources = await _session.GetSourcesAsync();
        _sources.Clear();
        foreach (var source in sources) _sources.Add(source);
        UpdateEmptyState();
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        SetBusy(true);
        try { await operation(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private static string GetSharedCredentialProfileId(string pluginId)
        => string.Equals(pluginId, LegacyVideoSiteCompatibility.PluginId, StringComparison.Ordinal)
            ? LegacyVideoSiteCompatibility.DefaultCredentialProfileId
            : "default";

    private void Session_StatusRaised(object? sender, FeedPeekStatus status)
        => ShowStatus(status.Message, status.Severity);

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private void RequireSettingsReload(string message)
    {
        _pluginReloadRequired = true;
        PluginsListView.IsEnabled = false;
        InstallPluginButton.IsEnabled = false;
        ShowStatus(message, InfoBarSeverity.Success);
    }

    private void SetBusy(bool isBusy)
    {
        AddButton.IsEnabled = !isBusy;
        EndpointTextBox.IsEnabled = !isBusy;
        InstallPluginButton.IsEnabled = !isBusy && !_pluginReloadRequired;
    }

    private void UpdateEmptyState()
        => EmptyPanel.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= SourcesSettingsPage_Loaded;
        Unloaded -= SourcesSettingsPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        if (_signInWindow is not null)
        {
            _signInWindow.SignInSucceeded -= SignInWindow_SignInSucceeded;
            _signInWindow.Closed -= SignInWindow_Closed;
            _signInWindow = null;
        }
        SourcesListView.ItemsSource = null;
        PluginsListView.ItemsSource = null;
        _sources.Clear();
        _plugins.Clear();
        _accountRuntime.Dispose();
        _session.Dispose();
    }
}
