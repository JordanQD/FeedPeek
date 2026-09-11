using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using FeedPeek.PluginContracts.Manifest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FeedPeek.Pages;

public sealed partial class PluginDetailPage : Page, IDisposable
{
    private readonly FeedPeekPanelSession _session = new();
    private readonly CookieStore _cookieStore = new();
    private readonly FeedPeekPluginRuntime _accountRuntime;
    private readonly PluginInstallationService _installations = new();
    private PluginSettingsRow? _plugin;
    private string? _pluginId;
    private WebSignInWindow? _signInWindow;
    private bool _disposed;

    public PluginDetailPage()
    {
        _accountRuntime = new FeedPeekPluginRuntime(_cookieStore);
        InitializeComponent();
        Loaded += PluginDetailPage_Loaded;
        Unloaded += PluginDetailPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pluginId = (e.Parameter as PluginSettingsRow)?.PluginId;
    }

    private async void PluginDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        _session.StatusRaised += Session_StatusRaised;
        await LoadAsync();
    }

    private void PluginDetailPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private async Task LoadAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_pluginId))
            {
                ShowStatus("缺少插件标识。", InfoBarSeverity.Warning);
                return;
            }

            await _session.InitializeAsync();
            await _accountRuntime.LoadAsync(default);
            var entries = await _session.GetPluginsAsync();
            var entry = entries.FirstOrDefault(item => item.PluginId == _pluginId);
            if (entry is null)
            {
                ShowStatus("插件不存在或已卸载。", InfoBarSeverity.Error);
                return;
            }

            _plugin = new PluginSettingsRow(entry);
            PluginCard.Header = _plugin.DisplayName;
            PluginCard.Description = $"版本 {_plugin.Version}";

            var hasLogin = _plugin.Manifest?.Auth?.LoginFlow is not null;
            var canConfigureSource = _plugin.Manifest?.Capabilities?.Contains("source.configuration", StringComparer.Ordinal) == true;

            LoginCard.Visibility = hasLogin ? Visibility.Visible : Visibility.Collapsed;
            LoginCard.IsEnabled = _plugin.IsEnabled;
            AddSourceCard.Visibility = canConfigureSource ? Visibility.Visible : Visibility.Collapsed;
            AddSourceCard.IsEnabled = _plugin.IsEnabled;

            if (hasLogin)
            {
                var profileId = GetSharedCredentialProfileId(_plugin.PluginId);
                var loggedIn = await _accountRuntime.HasCredentialsAsync(_plugin.PluginId, profileId);
                LoginCard.Description = loggedIn ? "已登录" : "未登录";
                LoginButton.Content = loggedIn ? "退出" : "登录";
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        var profileId = GetSharedCredentialProfileId(_plugin.PluginId);
        try
        {
            if (await _accountRuntime.HasCredentialsAsync(_plugin.PluginId, profileId))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"退出 {_plugin.DisplayName}",
                    Content = "退出后，依赖该登录状态的来源及其本地内容会被移除。",
                    PrimaryButtonText = "退出登录",
                    CloseButtonText = "取消",
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                await _session.ClearPluginCredentialsAsync(_plugin.PluginId, profileId);
                LoginCard.Description = "未登录";
                LoginButton.Content = "登录";
                ShowStatus("已退出登录并清除需要登录的来源内容。", InfoBarSeverity.Success);
                return;
            }

            _signInWindow = new WebSignInWindow(_cookieStore, _plugin.PluginId, profileId);
            _signInWindow.SignInSucceeded += SignInWindow_SignInSucceeded;
            _signInWindow.Closed += SignInWindow_Closed;
            _signInWindow.Activate();
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private async void AddSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin?.Manifest is null) return;
        var displayName = new TextBox { Header = "来源名称", Text = _plugin.DisplayName };
        var fields = new Dictionary<PluginSourceFieldManifest, TextBox>();
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(displayName);
        foreach (var field in _plugin.Manifest.SourceConfiguration?.Fields ?? [])
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
            Title = $"添加 {_plugin.DisplayName} 来源",
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
        if (_plugin.Manifest.Auth is not null)
        {
            var sharedProfileId = GetSharedCredentialProfileId(_plugin.PluginId);
            if (await _accountRuntime.HasCredentialsAsync(_plugin.PluginId, sharedProfileId))
                profileId = sharedProfileId;
            else if (_plugin.Manifest.Auth.Required)
            {
                ShowStatus("此网站来源需要先登录。", InfoBarSeverity.Warning);
                return;
            }
        }
        await RunBusyAsync(async () =>
        {
            await _session.AddPluginSourceAsync(
                _plugin.PluginId,
                string.IsNullOrWhiteSpace(displayName.Text) ? _plugin.DisplayName : displayName.Text.Trim(),
                options,
                profileId);
            ShowStatus("网站来源已添加。", InfoBarSeverity.Success);
        });
    }

    private async void DeletePluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除插件",
            Content = $"删除 {_plugin.DisplayName}？该插件的来源和本地缓存也会删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunBusyAsync(async () =>
        {
            await _session.ClearPluginCredentialsAsync(
                _plugin.PluginId,
                GetSharedCredentialProfileId(_plugin.PluginId));
            await _session.DeletePluginSourcesAsync(_plugin.PluginId);
            await _installations.UninstallAsync(_plugin.PluginId);
            ShowStatus("插件已删除。", InfoBarSeverity.Success);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Frame.CanGoBack) Frame.GoBack();
            });
        });
    }

    private void SignInWindow_SignInSucceeded(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            LoginCard.Description = "已登录";
            LoginButton.Content = "退出";
            ShowStatus("登录状态已保存，可以添加此网站的来源。", InfoBarSeverity.Success);
        });

    private void SignInWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_signInWindow is null) return;
        _signInWindow.SignInSucceeded -= SignInWindow_SignInSucceeded;
        _signInWindow.Closed -= SignInWindow_Closed;
        _signInWindow = null;
    }

    private static string GetSharedCredentialProfileId(string pluginId)
        => string.Equals(pluginId, LegacyVideoSiteCompatibility.PluginId, StringComparison.Ordinal)
            ? LegacyVideoSiteCompatibility.DefaultCredentialProfileId
            : "default";

    private void Session_StatusRaised(object? sender, FeedPeekStatus status)
        => ShowStatus(status.Message, status.Severity);

    private void ShowStatus(string message, InfoBarSeverity severity)
        => InfoBarAutoDismiss.Show(StatusInfoBar, message, severity);

    private async Task RunBusyAsync(Func<Task> operation)
    {
        SetBusy(true);
        try { await operation(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool isBusy)
    {
        LoginButton.IsEnabled = !isBusy;
        AddSourceButton.IsEnabled = !isBusy;
        DeletePluginButton.IsEnabled = !isBusy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= PluginDetailPage_Loaded;
        Unloaded -= PluginDetailPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        if (_signInWindow is not null)
        {
            _signInWindow.SignInSucceeded -= SignInWindow_SignInSucceeded;
            _signInWindow.Closed -= SignInWindow_Closed;
            _signInWindow = null;
        }
        _accountRuntime.Dispose();
        _session.Dispose();
    }
}
