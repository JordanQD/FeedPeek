using CommunityToolkit.WinUI.Controls;
using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace FeedPeek.Controls;

public sealed partial class OobeSourcesControl : UserControl, IDisposable
{
    private readonly FeedPeekPanelSession _session = new();
    private readonly PluginInstallationService _installations = new();
    private readonly CookieStore _cookieStore = new();
    private readonly FeedPeekPluginRuntime _accounts;
    private WebSignInWindow? _signInWindow;
    private bool _disposed;
    private bool _busy;

    public OobeSourcesControl()
    {
        _accounts = new FeedPeekPluginRuntime(_cookieStore);
        InitializeComponent();
        AddressCard.Header = Text("OobeSourcesAddressHeader", "订阅地址");
        AutomationProperties.SetName(AddressBox, Text("OobeSourcesAddressHeader", "订阅地址"));
        AutomationProperties.SetName(AddAddressButton, Text("OobeSourcesAdd", "添加"));
        ToolTipService.SetToolTip(AddAddressButton, Text("OobeSourcesAdd", "添加"));
        InstallCard.Header = Text("OobeSourcesPluginHeader", "网站插件");
        InstallButton.Content = Text("OobeSourcesInstall", "选择文件夹");
        SourceCountCard.Header = Text("OobeSourcesCountHeader", "我的来源");
        InfoBarAutoDismiss.CollapseWhenClosed(StatusInfoBar);
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
    }

    private async void Control_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Control_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    public Task ReloadAsync() => RunBusyAsync(ReloadCoreAsync);

    private async Task ReloadCoreAsync()
    {
        await _session.InitializeAsync();
        var sources = await _session.GetSourcesAsync();
        var plugins = await _session.GetPluginsAsync();
        await _accounts.LoadAsync(default);
        var cards = new List<SettingsCard>();
        foreach (var entry in plugins.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture))
        {
            var plugin = new PluginSettingsRow(entry);
            var hasLogin = plugin.Manifest?.Auth?.LoginFlow is not null;
            var loggedIn = plugin.Manifest?.Auth is not null
                && await _accounts.HasCredentialsAsync(plugin.PluginId, ProfileId(plugin.PluginId));
            if (_disposed) return;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var card = new SettingsCard
            {
                Header = plugin.DisplayName,
                Description = string.Join(" · ", new[]
                {
                    string.Format(Text("OobeSourcesPluginCount", "{0} 个来源"), sources.Count(source => source.ProviderId == plugin.PluginId)),
                    string.Join("、", sources.Where(source => source.ProviderId == plugin.PluginId
                        && source.DisplayName != plugin.DisplayName).Select(source => source.DisplayName)),
                    hasLogin ? Text(loggedIn ? "OobeSourcesLoggedIn" : "OobeSourcesNotLoggedIn", loggedIn ? "已登录" : "未登录") : string.Empty,
                    plugin.ErrorMessage ?? string.Empty,
                }.Where(value => !string.IsNullOrEmpty(value))),
                Content = actions,
                IsEnabled = plugin.IsEnabled && plugin.ErrorMessage is null,
            };
            AutomationProperties.SetName(card, plugin.DisplayName);
            if (hasLogin)
            {
                var login = new Button
                {
                    Content = Text(loggedIn ? "OobeSourcesLoggedIn" : "OobeSourcesLogin", loggedIn ? "已登录" : "登录"),
                    IsEnabled = !loggedIn,
                };
                AutomationProperties.SetAutomationId(login, "OobePluginLogin_" + plugin.PluginId);
                login.Click += (_, _) => OpenLogin(plugin);
                actions.Children.Add(login);
            }
            cards.Add(card);
        }

        if (_disposed) return;
        SourceCountText.Text = sources.Count.ToString();
        SourceCards.Children.Clear();
        foreach (var source in sources.Where(source => !plugins.Any(plugin => plugin.PluginId == source.ProviderId))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture))
        {
            var card = new SettingsCard
            {
                Header = source.DisplayName,
                Description = source.Category,
            };
            AutomationProperties.SetName(card, source.DisplayName);
            AutomationProperties.SetAutomationId(card, "OobeSource_" + source.ProviderId + "_" + source.SourceId);
            SourceCards.Children.Add(card);
        }
        PluginCards.Children.Clear();
        foreach (var card in cards) PluginCards.Children.Add(card);
    }

    private async void AddAddressButton_Click(object sender, RoutedEventArgs e) => await AddAddressAsync();

    private async void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await AddAddressAsync();
    }

    private async Task AddAddressAsync()
    {
        var address = AddressBox.Text.Trim();
        if (string.IsNullOrEmpty(address)) return;
        await RunBusyAsync(async () =>
        {
            await _session.AddSourceFromAddressAsync(address);
            if (_disposed) return;
            AddressBox.Text = string.Empty;
            await ReloadCoreAsync();
            ShowStatus(Text("OobeSourcesAdded", "已添加来源。"), InfoBarSeverity.Success);
        });
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || _disposed) return;
            await _installations.InstallAsync(folder.Path);
            await ReloadCoreAsync();
            ShowStatus(Text("OobeSourcesInstalled", "已安装插件。"), InfoBarSeverity.Success);
        });
    }

    private void OpenLogin(PluginSettingsRow plugin)
    {
        if (_signInWindow is not null)
        {
            _signInWindow.Activate();
            return;
        }
        try
        {
            _signInWindow = new WebSignInWindow(_cookieStore, plugin.PluginId, ProfileId(plugin.PluginId));
            _signInWindow.SignInSucceeded += SignIn_Succeeded;
            _signInWindow.Closed += SignIn_Closed;
            _signInWindow.Activate();
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private async void SignIn_Succeeded(object? sender, EventArgs e) => await ReloadAsync();

    private void SignIn_Closed(object sender, WindowEventArgs args)
    {
        if (_signInWindow is null) return;
        _signInWindow.SignInSucceeded -= SignIn_Succeeded;
        _signInWindow.Closed -= SignIn_Closed;
        _signInWindow = null;
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_busy || _disposed) return;
        _busy = true;
        SetBusy(true);
        try { await operation(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            if (!_disposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        AddressCard.IsEnabled = !busy;
        InstallCard.IsEnabled = !busy;
        PluginArea.IsEnabled = !busy;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (!_disposed) InfoBarAutoDismiss.Show(StatusInfoBar, message, severity);
    }

    private static string Text(string key, string fallback) => LocalizationHelper.GetString(key, fallback);

    private static string ProfileId(string pluginId)
        => string.Equals(pluginId, LegacyVideoSiteCompatibility.PluginId, StringComparison.Ordinal)
            ? LegacyVideoSiteCompatibility.DefaultCredentialProfileId : "default";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= Control_Loaded;
        Unloaded -= Control_Unloaded;
        if (_signInWindow is not null)
        {
            _signInWindow.SignInSucceeded -= SignIn_Succeeded;
            _signInWindow.Closed -= SignIn_Closed;
            _signInWindow = null;
        }
        _accounts.Dispose();
        _session.Dispose();
    }
}
