using BiliRadar.Models;
using BiliRadar.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace BiliRadar.Pages;

public sealed partial class SourcesSettingsPage : Page, IDisposable
{
    private readonly ObservableCollection<FeedSourceRow> _sources = [];
    private readonly FeedPeekPanelSession _session = new();
    private readonly CookieStore _cookieStore = new();
    private readonly BuiltInPluginAccountService _accountService;
    private WebSignInWindow? _signInWindow;
    private bool _isAccountAuthenticated;
    private bool _disposed;

    public SourcesSettingsPage()
    {
        _accountService = new BuiltInPluginAccountService(_cookieStore);
        InitializeComponent();
        SourcesListView.ItemsSource = _sources;
        Loaded += SourcesSettingsPage_Loaded;
        Unloaded += SourcesSettingsPage_Unloaded;
    }

    private async void SourcesSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _session.StatusRaised += Session_StatusRaised;
        PluginAccountEvents.Changed += PluginAccountEvents_Changed;
        await ReloadSourcesAsync();
        await RefreshPluginAccountStatusAsync();
    }

    private void SourcesSettingsPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddSourceAsync();

    private async void EndpointTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await AddSourceAsync();
    }

    private async Task AddSourceAsync()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowStatus("请输入网站或 RSS 地址。", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            await _session.AddRssSubscriptionAsync(endpoint);
            EndpointTextBox.Text = string.Empty;
            await ReloadSourcesAsync();
            ShowStatus("RSS 来源已添加。", InfoBarSeverity.Success);
        }
        catch
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FeedSourceRow source }) return;
        SetBusy(true);
        try
        {
            await _session.DeleteSourceAsync(source);
            _sources.Remove(source);
            UpdateEmptyState();
            ShowStatus("来源已删除。", InfoBarSeverity.Success);
        }
        catch
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SiteLoginActionButton_Click(object sender, RoutedEventArgs e)
    {
        SiteLoginActionButton.IsEnabled = false;
        try
        {
            if (_isAccountAuthenticated)
            {
                await _accountService.SignOutAsync();
                await _session.ReconcileBuiltInPluginSourceAsync();
                await ReloadSourcesAsync();
                await RefreshPluginAccountStatusAsync();
                ShowStatus("网站插件已退出登录，本地缓存已清除。", InfoBarSeverity.Success);
                return;
            }

            if (await _accountService.HasCredentialsAsync())
            {
                await _accountService.SignOutAsync();
            }

            if (_signInWindow is null)
            {
                _signInWindow = new WebSignInWindow(_cookieStore);
                _signInWindow.SignInSucceeded += SignInWindow_SignInSucceeded;
                _signInWindow.Closed += SignInWindow_Closed;
            }

            _signInWindow.Activate();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SiteLoginActionButton.IsEnabled = true;
        }
    }

    private void SignInWindow_SignInSucceeded(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(async () =>
        {
            await _session.ReconcileBuiltInPluginSourceAsync();
            await ReloadSourcesAsync();
            await RefreshPluginAccountStatusAsync();
        });

    private void SignInWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_signInWindow is null) return;
        _signInWindow.SignInSucceeded -= SignInWindow_SignInSucceeded;
        _signInWindow.Closed -= SignInWindow_Closed;
        _signInWindow = null;
    }

    private void PluginAccountEvents_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed) return;
            await RefreshPluginAccountStatusAsync();
        });
    }

    private async Task RefreshPluginAccountStatusAsync()
    {
        if (!await _accountService.HasCredentialsAsync())
        {
            _isAccountAuthenticated = false;
            SiteAccountNameText.Text = "未登录";
            SiteAccountDetailText.Text = "登录状态由此网站下需要账户权限的来源共享。";
            SiteAccountAvatar.ProfilePicture = null;
            SiteAccountAvatar.Initials = string.Empty;
            SiteLoginActionIcon.Glyph = "\uE77B";
            SiteLoginActionButton.SetValue(ToolTipService.ToolTipProperty, "网页登录");
            return;
        }

        try
        {
            var profile = await _accountService.GetCurrentProfileAsync();
            if (profile is null)
            {
                _isAccountAuthenticated = false;
                SiteAccountNameText.Text = "登录已失效";
                SiteAccountDetailText.Text = "请重新登录此网站插件。";
                SiteAccountAvatar.ProfilePicture = null;
                SiteAccountAvatar.Initials = string.Empty;
                SiteLoginActionIcon.Glyph = "\uE77B";
                SiteLoginActionButton.SetValue(ToolTipService.ToolTipProperty, "重新登录");
                return;
            }

            _isAccountAuthenticated = true;
            SiteAccountNameText.Text = profile.DisplayName;
            SiteAccountDetailText.Text = "此账户由该网站下需要账户权限的来源共享。";
            SiteAccountAvatar.Initials = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? string.Empty
                : profile.DisplayName[..1];
            SiteAccountAvatar.ProfilePicture = string.IsNullOrWhiteSpace(profile.AvatarUrl)
                ? null
                : new BitmapImage(new Uri(profile.AvatarUrl));
            SiteLoginActionIcon.Glyph = "\uE8D7";
            SiteLoginActionButton.SetValue(ToolTipService.ToolTipProperty, "退出登录");
        }
        catch
        {
            _isAccountAuthenticated = false;
            SiteAccountNameText.Text = "状态未知";
            SiteAccountDetailText.Text = "暂时无法验证登录状态。";
            SiteLoginActionIcon.Glyph = "\uE77B";
            SiteLoginActionButton.SetValue(ToolTipService.ToolTipProperty, "重新登录");
        }
    }

    private async Task ReloadSourcesAsync()
    {
        try
        {
            await _session.InitializeAsync();
            var sources = await _session.GetSourcesAsync();
            _sources.Clear();
            foreach (var source in sources.Where(source =>
                string.Equals(source.ProviderId, FeedPeek.Rss.RssProviderInfo.ProviderId, StringComparison.Ordinal)))
            {
                _sources.Add(source);
            }
            UpdateEmptyState();
        }
        catch
        {
        }
    }

    private void Session_StatusRaised(object? sender, FeedPeekStatus status)
        => ShowStatus(status.Message, status.Severity);

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private void SetBusy(bool isBusy)
    {
        AddButton.IsEnabled = !isBusy;
        EndpointTextBox.IsEnabled = !isBusy;
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
        PluginAccountEvents.Changed -= PluginAccountEvents_Changed;
        _accountService.Dispose();
        if (_signInWindow is not null)
        {
            _signInWindow.SignInSucceeded -= SignInWindow_SignInSucceeded;
            _signInWindow.Closed -= SignInWindow_Closed;
            _signInWindow = null;
        }
        SourcesListView.ItemsSource = null;
        _sources.Clear();
        _session.Dispose();
    }
}
