using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FeedPeek.Pages;

public sealed partial class PluginDetailPage : Page, IDisposable
{
    private readonly FeedPeekPanelSession _session = new();
    private readonly CookieStore _cookieStore = new();
    private readonly FeedPeekPluginRuntime _accountRuntime;
    private readonly PluginInstallationService _installations = new();
    private PluginSettingsRow? _plugin;
    private string? _pluginId;
    private string? _defaultIconUri;
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
        AppSettings.WebsiteSettingsChanged += OnWebsiteSettingsChanged;
        await LoadAsync();
    }

    private void PluginDetailPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private void SaveWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pluginId))
        {
            return;
        }

        AppSettings.SetPluginWebsite(_pluginId, WebsiteBox.Text);
        WebsiteBox.Text = AppSettings.GetPluginWebsite(_pluginId) ?? string.Empty;
        ShowStatus("已保存打开的网页。", InfoBarSeverity.Success);
    }

    private void OnWebsiteSettingsChanged(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pluginId) || _disposed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            WebsiteBox.Text = AppSettings.GetPluginWebsite(_pluginId) ?? string.Empty;
        });
    }

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
            _defaultIconUri = _plugin.DefaultIconUri;
            HeroName.Text = _plugin.DisplayName;
            HeroVersion.Text = string.IsNullOrWhiteSpace(_plugin.Version) ? string.Empty : $"版本 {_plugin.Version}";
            WebsiteBox.Text = AppSettings.GetPluginWebsite(_pluginId) ?? string.Empty;
            PluginCard.Header = "插件标识";
            PluginCard.Description = _plugin.PluginId;
            IconPicture.ProfilePicture = CreateImageSource(_plugin.IconUri);

            var hasLogin = _plugin.Manifest?.Auth?.LoginFlow is not null;

            LoginCard.Visibility = hasLogin ? Visibility.Visible : Visibility.Collapsed;
            LoginCard.IsEnabled = _plugin.IsEnabled;

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

    private async void PickIconFile_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        var picker = new FileOpenPicker();
        picker.ViewMode = PickerViewMode.Thumbnail;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".ico", ".bmp", ".gif", ".webp" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        var hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await RunBusyAsync(async () =>
        {
            var iconUri = await StoreIconFileAsync(file);
            AppSettings.SetPluginIcon(_plugin.PluginId, iconUri);
            IconPicture.ProfilePicture = CreateImageSource(iconUri);
            ShowStatus("图标已更新。", InfoBarSeverity.Success);
        });
    }

    private async void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null) return;
        await RunBusyAsync(() =>
        {
            AppSettings.SetPluginIcon(_plugin.PluginId, null);
            IconPicture.ProfilePicture = CreateImageSource(_defaultIconUri);
            ShowStatus("已恢复默认图标。", InfoBarSeverity.Success);
            return Task.CompletedTask;
        });
    }

    private async Task<string> StoreIconFileAsync(StorageFile file)
    {
        var folder = await ApplicationData.Current.LocalFolder
            .CreateFolderAsync("plugin-icons", CreationCollisionOption.OpenIfExists);
        var ext = Path.GetExtension(file.Name);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".png";
        }

        // 用唯一文件名，避免覆盖被 BitmapImage 占用的旧文件导致“文件被占用”错误。
        var fileName = $"{SanitizeFileName(_plugin!.PluginId)}_{Guid.NewGuid():N}{ext}";
        await file.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
        return $"ms-appdata:///local/plugin-icons/{fileName}";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private void IconGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        => EditOverlay.Visibility = Visibility.Visible;

    private void IconGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        => EditOverlay.Visibility = Visibility.Collapsed;

    private static ImageSource? CreateImageSource(string? imageUrl)
        => ImageSourceCache.GetSettingsAvatar(imageUrl);

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
        DeletePluginButton.IsEnabled = !isBusy;
        IconButton.IsEnabled = !isBusy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= PluginDetailPage_Loaded;
        Unloaded -= PluginDetailPage_Unloaded;
        _session.StatusRaised -= Session_StatusRaised;
        AppSettings.WebsiteSettingsChanged -= OnWebsiteSettingsChanged;
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