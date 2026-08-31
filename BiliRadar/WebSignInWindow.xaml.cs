using BiliRadar.Helpers;
using BiliRadar.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace BiliRadar;

public sealed partial class WebSignInWindow : Window
{
    private const double StandardTitleBarHeight = 32;

    private readonly FeedPeekPluginRuntime _pluginRuntime;
    private readonly AppWindow _appWindow;
    private readonly string _pluginId;
    private readonly string _credentialProfileId;
    private PluginLoginDescriptor? _login;
    private bool _isCookieSaved;

    internal WebSignInWindow(
        CookieStore cookieStore,
        string pluginId,
        string credentialProfileId)
    {
        _pluginId = pluginId;
        _credentialProfileId = credentialProfileId;
        _pluginRuntime = new FeedPeekPluginRuntime(cookieStore);
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Title = LocalizationHelper.GetString("WebSignInWindow.Title", "FeedPeek Preview 登录");
        WindowIconHelper.ApplyTo(_appWindow, hwnd);
        _appWindow.Resize(new SizeInt32(1200, 720));
        ConfigureTitleBar(hwnd);

        mainView.Loaded += MainView_Loaded;
        Closed += WebSignInWindow_Closed;
    }

    public event EventHandler? SignInSucceeded;

    private async void MainView_Loaded(object sender, RoutedEventArgs e)
    {
        mainView.Loaded -= MainView_Loaded;
        try
        {
            await mainView.EnsureCoreWebView2Async();
            var load = await _pluginRuntime.LoadAsync(default);
            if (!load.IsAvailable)
            {
                throw new InvalidOperationException(load.ErrorMessage);
            }

            _login = _pluginRuntime.GetLoginDescriptor(_pluginId, _credentialProfileId);
            await _pluginRuntime.BeginAuthenticationAsync(_pluginId, _credentialProfileId);
            _appWindow.Title = $"{_login.DisplayName} 登录";
            titleBar.Title = _appWindow.Title;
            foreach (var domain in _login.Auth.CookieDomains)
            {
                var cookies = await mainView.CoreWebView2.CookieManager.GetCookiesAsync($"https://{domain}/");
                foreach (var cookie in cookies)
                {
                    mainView.CoreWebView2.CookieManager.DeleteCookie(cookie);
                }
            }
            mainView.CoreWebView2.Navigate(_login.Auth.LoginFlow!.LoginUrl);
        }
        catch (Exception ex)
        {
            ShowStatus(LocalizationHelper.Format("LoginWindowInitFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void MainView_NavigationCompleted(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess || sender.CoreWebView2 is null)
        {
            return;
        }

        var source = sender.CoreWebView2.Source ?? string.Empty;
        var successPrefixes = _login?.Auth.LoginFlow?.SuccessUrlPrefixes;
        if (successPrefixes is null
            || !successPrefixes.Any(prefix => source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            await TrySaveCookiesAsync(sender);
        }
        catch (Exception ex)
        {
            ShowStatus(LocalizationHelper.Format("LoginWindowInitFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task TrySaveCookiesAsync(WebView2 sender)
    {
        if (_isCookieSaved || sender.CoreWebView2 is null || _login?.Auth.LoginFlow is null)
        {
            return;
        }

        var cookiePairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in _login.Auth.CookieDomains)
        {
            var cookies = await sender.CoreWebView2.CookieManager.GetCookiesAsync($"https://{domain}/");
            foreach (var cookie in cookies.Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name)))
            {
                cookiePairs[cookie.Name] = cookie.Value ?? string.Empty;
            }
        }

        if (_login.Auth.LoginFlow.SuccessCookieNames.Any(name => !cookiePairs.ContainsKey(name)))
        {
            return;
        }

        ShowStatus(LocalizationHelper.GetString("LoginSuccessExchanging"), InfoBarSeverity.Informational);
        await _pluginRuntime.SaveWebCookiesAsync(_login, cookiePairs);
        _isCookieSaved = true;
        ShowStatus(LocalizationHelper.GetString("LoginSuccessSaved"), InfoBarSeverity.Success);
        SignInSucceeded?.Invoke(this, EventArgs.Empty);
        Close();
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

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        statusInfoBar.Message = message;
        statusInfoBar.Severity = severity;
        statusInfoBar.IsOpen = true;
    }

    private void WebSignInWindow_Closed(object sender, WindowEventArgs args)
    {
        Closed -= WebSignInWindow_Closed;
        _pluginRuntime.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
