using FeedPeek.Controls;
using FeedPeek.Helpers;
using FeedPeek.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using AppActivationArguments = Microsoft.Windows.AppLifecycle.AppActivationArguments;
using AppInstance = Microsoft.Windows.AppLifecycle.AppInstance;
using ExtendedActivationKind = Microsoft.Windows.AppLifecycle.ExtendedActivationKind;

namespace FeedPeek;

public partial class App : Application
{
    private const string MainInstanceKey = "FeedPeek.Preview.Main";
    private SettingsWindow? _settingsWindow;
    private TrayFlyoutService? _trayFlyoutService;
    private BackgroundNotificationMonitor? _backgroundNotificationMonitor;
    private AppNotificationManager? _notificationManager;
    private NotificationService.NotificationActivationRequest? _pendingNotificationRequest;
    private readonly CookieStore _cookieStore = new();
    private readonly DispatcherQueue _dispatcherQueue;

    public App()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        AppDataMigrationService.Run();
        LocalizationHelper.SetLanguage(AppSettings.AppLanguage);
        InitializeComponent();
        InitializeNotifications();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (await RedirectToMainInstanceIfNeededAsync(activatedArgs))
        {
            Exit();
            return;
        }

        AppInstance.GetCurrent().Activated += AppInstance_Activated;

        InitializeTrayAndData();
        HandleActivation(activatedArgs, isRedirectedActivation: false);

    }

    private static async Task<bool> RedirectToMainInstanceIfNeededAsync(AppActivationArguments activatedArgs)
    {
        var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        if (mainInstance.IsCurrent)
        {
            return false;
        }

        await mainInstance.RedirectActivationToAsync(activatedArgs);
        return true;
    }

    private void AppInstance_Activated(object? sender, AppActivationArguments args)
    {
        _dispatcherQueue.TryEnqueue(() => HandleActivation(args, isRedirectedActivation: true));
    }

    private void InitializeTrayAndData()
    {
        if (_backgroundNotificationMonitor is null)
        {
            _backgroundNotificationMonitor = new BackgroundNotificationMonitor(_cookieStore);
            _ = _backgroundNotificationMonitor.StartAsync();
            HandlePendingNotificationRequest();
        }

        if (_trayFlyoutService is null)
        {
            _trayFlyoutService = new TrayFlyoutService(
                _dispatcherQueue,
                ShowSettingsWindow,
                ExitApplication);
        }

    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += SettingsWindow_Closed;
        }

        _settingsWindow.ShowWindow();
    }

    private async void HandleRunningLaunchAction()
    {
        if (AppSettings.RunningLaunchAction == RunningLaunchAction.OpenCustomWebPage)
        {
            if (Uri.TryCreate(AppSettings.CustomLaunchWebPageUrl, UriKind.Absolute, out var uri))
            {
                await NotificationService.LaunchUriAsync(uri);
            }

            return;
        }

        ShowSettingsWindow();
    }

    private void ExitApplication()
    {
        _trayFlyoutService?.Dispose();
        _trayFlyoutService = null;
        _backgroundNotificationMonitor?.Dispose();
        _backgroundNotificationMonitor = null;
        _notificationManager?.Unregister();
        _notificationManager = null;
        _settingsWindow?.Close();
        _settingsWindow = null;
        Exit();
    }

    private void InitializeNotifications()
    {
        try
        {
            _notificationManager = AppNotificationManager.Default;
            _notificationManager.NotificationInvoked += AppNotificationManager_NotificationInvoked;
            if (IsPackaged())
            {
                _notificationManager.Register();
                NotificationService.TraceNotification("Packaged app notification manager registered");
            }
            else
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-24_altform-unplated.png");
                _notificationManager.Register("FeedPeek", new Uri(iconPath));
                NotificationService.TraceNotification("Unpackaged app notification manager registered");
            }
        }
        catch (Exception exception)
        {
            NotificationService.TraceNotification(
                $"App notification registration failed: {exception.GetType().Name}: {exception.Message}");
            _notificationManager = null;
        }
    }

    private void AppNotificationManager_NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        HandleNotificationActivation(args);
    }

    private void HandleActivation(AppActivationArguments activatedArgs, bool isRedirectedActivation)
    {
        try
        {
            if (activatedArgs.Kind == ExtendedActivationKind.AppNotification
                && activatedArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
            {
                HandleNotificationActivation(notificationArgs);
                return;
            }

            if (isRedirectedActivation)
            {
                HandleRunningLaunchAction();
            }
        }
        catch
        {
        }
    }

    private void HandleNotificationActivation(AppNotificationActivatedEventArgs args)
    {
        if (!NotificationService.TryGetActivationRequest(args, out var request))
        {
            return;
        }

        var monitor = _backgroundNotificationMonitor;
        if (monitor is null)
        {
            _pendingNotificationRequest = request;
            return;
        }

        _dispatcherQueue.TryEnqueue(async () => await monitor.HandleActivationAsync(request));
    }

    private void HandlePendingNotificationRequest()
    {
        if (_pendingNotificationRequest is null || _backgroundNotificationMonitor is null)
        {
            return;
        }

        var request = _pendingNotificationRequest;
        _pendingNotificationRequest = null;
        _dispatcherQueue.TryEnqueue(async () => await _backgroundNotificationMonitor.HandleActivationAsync(request));
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current.Id.Name;
            return true;
        }
        catch
        {
            return false;
        }
    }


    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= SettingsWindow_Closed;
            _settingsWindow = null;
        }
    }
}
