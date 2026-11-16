using FeedPeek.Controls;
using FeedPeek.Helpers;
using FeedPeek.Models;
using DesktopFlyouts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.ViewManagement;

namespace FeedPeek.Services;

internal sealed class TrayFlyoutService : IDisposable
{
    private const double MainPanelWidth = 420d;
    private const double MinimumPanelHeight = 320d;
    private const double FlyoutWorkAreaInset = 24d;
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;
    private const int VirtualKeyMiddleButton = 0x04;
    private static readonly Guid TrayIconGuid = new("9CB5A423-C114-4FF5-8AA6-271935539A0C");
#if DEBUG
    private const long TraceFileMaxBytes = 256 * 1024;
    private static readonly object TraceFileGate = new();
    private static string? _traceFilePath;
    private static bool _traceFileResolved;
#endif
    private static readonly TimeSpan TrayLightDismissReopenGuard = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BackgroundOpenWatchInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BackgroundOpenTargetWindow = TimeSpan.FromSeconds(3);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DesktopFlyout _mainFlyout;
    private readonly DesktopFlyoutIsland _mainFlyoutIsland;
    private readonly DesktopMenuFlyout _contextMenu;
    private readonly DispatcherQueueTimer _mainFlyoutOpenDelayTimer;
    private readonly DispatcherQueueTimer _keepFlyoutOpenTimer;
    private readonly SystemTrayIcon _trayIcon;
    private UISettings? _uiSettings;
    private DateTime _lastMainFlyoutClosedAt = DateTime.MinValue;
    private Point? _pendingContextMenuPoint;
    private Point? _pendingMainFlyoutPoint;
    private Point _mainFlyoutAnchorPoint;
    private bool _isDisposed;
    private nint _flyoutWindow;
    private nint _backgroundOpenTargetWindow;
    private DateTime _backgroundOpenStartedAt;
    private bool _isWatchingBackgroundOpen;
    private bool _mouseButtonWasDown;
    private bool _backgroundOpenLostForeground;
    private bool _backgroundOpenPanelUsed;

    public TrayFlyoutService(
        DispatcherQueue dispatcherQueue,
        Action settingsAction,
        Action exitAction)
    {
        _dispatcherQueue = dispatcherQueue;

        _uiSettings = new UISettings();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;

        _mainFlyoutIsland = new DesktopFlyoutIsland
        {
            IslandHeight = new GridLength(AppSettings.MainPanelHeight),
        };

        _mainFlyout = new DesktopFlyout
        {
            FlyoutWidth = new GridLength(MainPanelWidth),
            FlyoutHeight = new GridLength(AppSettings.MainPanelHeight),
            ActivationMode = DesktopFlyoutActivationMode.Activate,
            HideOnLostFocus = true,
            IsBackdropEnabled = true,
            BackdropKind = DesktopFlyoutBackdropKind.DesktopAcrylic,
            IsTransitionAnimationEnabled = _uiSettings.AnimationsEnabled,
            PressedScale = 1d,
            IsSwipeToDismissEnabled = false,
        };
        _mainFlyout.Islands.Add(_mainFlyoutIsland);
        _mainFlyout.TransitionStateChanged += OnMainFlyoutTransitionStateChanged;

        _contextMenu = CreateContextMenu(settingsAction, exitAction);
        _mainFlyoutOpenDelayTimer = _dispatcherQueue.CreateTimer();
        _mainFlyoutOpenDelayTimer.IsRepeating = false;
        _mainFlyoutOpenDelayTimer.Tick += MainFlyoutOpenDelayTimer_Tick;

        _keepFlyoutOpenTimer = _dispatcherQueue.CreateTimer();
        _keepFlyoutOpenTimer.IsRepeating = true;
        _keepFlyoutOpenTimer.Interval = BackgroundOpenWatchInterval;
        _keepFlyoutOpenTimer.Tick += KeepFlyoutOpenTimer_Tick;

        _trayIcon = new SystemTrayIcon(GetIconPath(), "FeedPeek", TrayIconGuid);
        _trayIcon.LeftClicked += OnTrayIconLeftClicked;
        _trayIcon.RightClicked += OnTrayIconRightClicked;
        _trayIcon.Show();

        TraceFlyout($"DesktopFlyouts initialized. AnimationsEnabled={_uiSettings.AnimationsEnabled}");
    }

    public Task RefreshCurrentPanelPageAsync()
    {
        return _mainFlyoutIsland.Content is MainPanelControl panel
            ? panel.RefreshCurrentPageAsync()
            : Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _pendingContextMenuPoint = null;
        CancelDelayedMainFlyoutShow();
        _mainFlyoutOpenDelayTimer.Tick -= MainFlyoutOpenDelayTimer_Tick;
        _keepFlyoutOpenTimer.Stop();
        _keepFlyoutOpenTimer.Tick -= KeepFlyoutOpenTimer_Tick;
        _isWatchingBackgroundOpen = false;
        _backgroundOpenTargetWindow = nint.Zero;

        if (_uiSettings is not null)
        {
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
            _uiSettings = null;
        }

        _trayIcon.LeftClicked -= OnTrayIconLeftClicked;
        _trayIcon.RightClicked -= OnTrayIconRightClicked;
        _trayIcon.Dispose();

        _mainFlyout.TransitionStateChanged -= OnMainFlyoutTransitionStateChanged;

        ReleaseFlyoutContent();
        _mainFlyout.Dispose();
        _contextMenu.Dispose();
    }

    private DesktopMenuFlyout CreateContextMenu(Action settingsAction, Action exitAction)
    {
        var menu = new DesktopMenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TraySettings"),
            Icon = new FontIcon { Glyph = "\uE713" },
            Command = new DelegateCommand(settingsAction),
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TrayExit"),
            // 19 keeps this glyph's ink at 25x25. At the default 20 the close cross reads larger
            // than the settings gear beside it (27x27 against 25x27).
            Icon = new FontIcon { Glyph = "\uE8BB", FontSize = 19 },
            Command = new DelegateCommand(() => RequestExit(exitAction)),
        });
        return menu;
    }

    private void OnTrayIconLeftClicked(object? sender, MouseEventReceivedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        TraceFlyout($"Tray icon left clicked. State={_mainFlyout.TransitionState}");

        // A left click is the latest user intent. Cancel a context menu that was
        // waiting for the main flyout transition to finish.
        _pendingContextMenuPoint = null;

        if (_contextMenu.IsOpen)
        {
            _contextMenu.Hide();
        }

        if (_pendingMainFlyoutPoint.HasValue)
        {
            CancelDelayedMainFlyoutShow();
            return;
        }

        if (_mainFlyout.TransitionState is DesktopFlyoutTransitionState.Opening
            or DesktopFlyoutTransitionState.Open
            or DesktopFlyoutTransitionState.Closing)
        {
            // Clicking the tray can light-dismiss the panel before this callback.
            // Treat Closing as a close intent too, rather than queueing a reopen.
            _mainFlyout.Hide();
            return;
        }

        RequestMainFlyoutShow(args.Point);
    }

    private void OnTrayIconRightClicked(object? sender, MouseEventReceivedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_contextMenu.IsOpen)
        {
            _contextMenu.Hide();
        }

        var menuPoint = GetContextMenuPoint(args.Point);
        CancelDelayedMainFlyoutShow();

        if (_mainFlyout.TransitionState is not DesktopFlyoutTransitionState.Closed)
        {
            // The library handles Hide during transitions. Only the menu needs
            // to wait for Closed because it owns a separate host window.
            _pendingContextMenuPoint = menuPoint;
            _mainFlyout.Hide();
            return;
        }

        _pendingContextMenuPoint = null;
        _contextMenu.Show(menuPoint);
    }

    private void OnMainFlyoutTransitionStateChanged(object? sender, EventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        switch (_mainFlyout.TransitionState)
        {
            case DesktopFlyoutTransitionState.Opening:
                // Also runs when the library replays a Show queued during closing.
                var panelHeight = GetMainPanelHeight(_mainFlyoutAnchorPoint);
                EnsureFlyoutContent(panelHeight);
                ConfigureMainFlyout(panelHeight);
                break;

            case DesktopFlyoutTransitionState.Open:
                TraceFlyout("DesktopFlyout opened");
                if (_mainFlyoutIsland.Content is MainPanelControl panel)
                {
                    panel.OnFlyoutOpened();
                }
                break;

            case DesktopFlyoutTransitionState.Closed:
                EndBackgroundOpenWatch();
                _lastMainFlyoutClosedAt = DateTime.UtcNow;
                TraceFlyout("DesktopFlyout closed");
                ReleaseFlyoutContent();
                QueuePendingContextMenuShow();
                break;
        }
    }

    /// <summary>
    /// Called just before a link is handed to the shell for a background open.
    /// </summary>
    /// <remarks>
    /// The browser takes the foreground, which normally light-dismisses the panel, so
    /// <see cref="DesktopFlyout.HideOnLostFocus"/> is cleared while the link opens. That alone is
    /// not enough: <c>HideOnLostFocus</c> is evaluated when the host window is deactivated, and a
    /// window that is already inactive never raises another deactivation, which would leave the
    /// panel open but impossible to dismiss by clicking away. The watch started here therefore owns
    /// the dismissal: it closes the panel on a click outside the panel, and when the foreground
    /// moves to a window other than the browser that was just opened. Dismissal is handed back to
    /// the library only while the panel holds the foreground, because restoring the flag on an
    /// inactive panel closes it immediately.
    /// </remarks>
    private void KeepMainFlyoutOpen()
    {
        if (_isDisposed || !_mainFlyout.IsOpen)
        {
            return;
        }

        RememberFlyoutWindow();
        _backgroundOpenTargetWindow = nint.Zero;
        _backgroundOpenStartedAt = DateTime.UtcNow;
        _backgroundOpenLostForeground = false;
        _backgroundOpenPanelUsed = false;
        _isWatchingBackgroundOpen = true;
        _mouseButtonWasDown = IsAnyMouseButtonPressedOrDown();
        _mainFlyout.HideOnLostFocus = false;
        _keepFlyoutOpenTimer.Stop();
        _keepFlyoutOpenTimer.Start();
        TraceFlyout($"Background open: watching. flyout={_flyoutWindow}");
    }

    private void KeepFlyoutOpenTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed || !_isWatchingBackgroundOpen || !_mainFlyout.IsOpen)
        {
            EndBackgroundOpenWatch();
            return;
        }

        var foreground = GetForegroundWindow();
        var panelIsForeground = IsOwnWindow(foreground);

        // Dismissal is detected here rather than by the library: while this watch runs light dismiss
        // is suppressed, and once the panel is inactive no deactivation reaches the library at all.
        var mouseButtonDown = IsAnyMouseButtonPressedOrDown();
        var pressedThisTick = mouseButtonDown && !_mouseButtonWasDown;
        _mouseButtonWasDown = mouseButtonDown;

        if (pressedThisTick)
        {
            if (IsPressOutsideFlyout())
            {
                DismissFlyoutForBackgroundOpen("click outside the panel");
                return;
            }

            // The press landed on the panel. Dismissal is handed back on a later tick, once the panel
            // is confirmed active: restoring the flag while it is still inactive closes the panel on
            // the spot, which is visible as the panel flinching.
            _backgroundOpenPanelUsed = true;
        }

        if (panelIsForeground)
        {
            _flyoutWindow = foreground;
            if (_backgroundOpenLostForeground || _backgroundOpenPanelUsed)
            {
                // Handing dismissal back is only safe with the panel active: the flag then protects
                // nothing, and the next deactivation closes the panel as usual. This is what keeps an
                // ordinary left click on an item able to close the panel again.
                TraceFlyout("Background open: handing dismissal back to the library");
                EndBackgroundOpenWatch();
                return;
            }

            if (DateTime.UtcNow - _backgroundOpenStartedAt > BackgroundOpenTargetWindow)
            {
                // No browser took over at all, so nothing is left to protect the panel from.
                TraceFlyout("Background open: no browser took over; restoring normal dismissal");
                EndBackgroundOpenWatch();
            }

            return;
        }

        _backgroundOpenLostForeground = true;

        if (_backgroundOpenTargetWindow == nint.Zero)
        {
            if (DateTime.UtcNow - _backgroundOpenStartedAt <= BackgroundOpenTargetWindow)
            {
                // The first foreign foreground window after the launch is the browser being opened.
                _backgroundOpenTargetWindow = foreground;
                TraceFlyout($"Background open: browser window={foreground}");
            }
            else
            {
                // No browser showed up, so the user moved on by themselves.
                DismissFlyoutForBackgroundOpen($"no browser took over (foreground={foreground})");
            }

            return;
        }

        if (foreground != _backgroundOpenTargetWindow)
        {
            // The user moved to a different window, which is what light dismiss would have done.
            DismissFlyoutForBackgroundOpen($"user moved to {foreground}");
        }
    }

    private void DismissFlyoutForBackgroundOpen(string reason)
    {
        TraceFlyout($"Background open: dismissing the panel - {reason}");
        _keepFlyoutOpenTimer.Stop();
        _mainFlyout.Hide();
    }

    private void EndBackgroundOpenWatch()
    {
        _keepFlyoutOpenTimer.Stop();
        _isWatchingBackgroundOpen = false;
        _backgroundOpenLostForeground = false;
        _backgroundOpenPanelUsed = false;
        _backgroundOpenTargetWindow = nint.Zero;
        if (_mainFlyout.HideOnLostFocus)
        {
            return;
        }

        _mainFlyout.HideOnLostFocus = true;
    }

    private void RememberFlyoutWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsOwnWindow(foreground))
        {
            _flyoutWindow = foreground;
        }
    }

    private static bool IsOwnWindow(nint window)
    {
        if (window == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Reports a held button or one pressed since the previous call: the 0x0001 bit is what makes
    /// clicks shorter than the polling interval observable.
    /// </summary>
    private static bool IsAnyMouseButtonPressedOrDown()
        => IsKeyPressedOrDown(VirtualKeyLeftButton)
            || IsKeyPressedOrDown(VirtualKeyRightButton)
            || IsKeyPressedOrDown(VirtualKeyMiddleButton);

    private static bool IsKeyPressedOrDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8001) != 0;

    /// <summary>
    /// Returns <see langword="true"/> only when the press is known to be outside the panel. When the
    /// panel rectangle cannot be determined the press is treated as inside, because a wrong "outside"
    /// would close the panel under the user's cursor.
    /// </summary>
    private bool IsPressOutsideFlyout()
    {
        if (!IsOwnWindow(_flyoutWindow) || !GetWindowRect(_flyoutWindow, out var rect))
        {
            return false;
        }

        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var inside = point.X >= rect.Left
            && point.X < rect.Right
            && point.Y >= rect.Top
            && point.Y < rect.Bottom;
        return !inside;
    }

    private void QueuePendingContextMenuShow()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed
                || _mainFlyout.TransitionState is not DesktopFlyoutTransitionState.Closed
                || _pendingMainFlyoutPoint.HasValue
                || _pendingContextMenuPoint is not Point menuPoint)
            {
                return;
            }

            _pendingContextMenuPoint = null;
            TraceFlyout("Showing deferred context menu");
            _contextMenu.Show(menuPoint);
        });
    }

    private void RequestMainFlyoutShow(Point anchorPoint)
    {
        var elapsedSinceClose = DateTime.UtcNow - _lastMainFlyoutClosedAt;
        if (_mainFlyout.TransitionState is DesktopFlyoutTransitionState.Closed
            && elapsedSinceClose < TrayLightDismissReopenGuard)
        {
            // Preserve the existing light-dismiss guard, with one delayed Show
            // rather than repeatedly resending requests during transitions.
            _pendingMainFlyoutPoint = anchorPoint;
            _mainFlyoutOpenDelayTimer.Interval =
                TrayLightDismissReopenGuard - elapsedSinceClose + TimeSpan.FromMilliseconds(20);
            _mainFlyoutOpenDelayTimer.Start();
            return;
        }

        ShowMainFlyout(anchorPoint);
    }

    private void ShowMainFlyout(Point anchorPoint)
    {
        try
        {
            _mainFlyoutAnchorPoint = anchorPoint;
            _mainFlyout.IsTransitionAnimationEnabled = _uiSettings?.AnimationsEnabled == true;
            TraceFlyout($"Requesting DesktopFlyout open. State={_mainFlyout.TransitionState}");
            var anchorBounds = _trayIcon.TryGetBounds(out var bounds)
                ? bounds
                : new Rectangle(anchorPoint, new Size(1, 1));
            _mainFlyout.Show(DesktopFlyoutPosition.ForTaskbarAnchor(anchorBounds));
        }
        catch (Exception exception)
        {
            TraceFlyout($"DesktopFlyout Show failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void MainFlyoutOpenDelayTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var point = _pendingMainFlyoutPoint;
        _pendingMainFlyoutPoint = null;
        if (!_isDisposed && point is Point anchorPoint)
        {
            ShowMainFlyout(anchorPoint);
        }
    }

    private void CancelDelayedMainFlyoutShow()
    {
        _mainFlyoutOpenDelayTimer.Stop();
        _pendingMainFlyoutPoint = null;
    }

    private void ReleaseFlyoutContent()
    {
        if (_mainFlyoutIsland.Content is not MainPanelControl panel)
        {
            return;
        }

        panel.OnFlyoutClosed();
        _mainFlyoutIsland.Content = null;
        // Disposal may await in-flight requests. Keep it off the UI thread so
        // closing and any subsequent opening can finish without blocking.
        _ = DisposePanelAsync(panel);
    }

    private static async Task DisposePanelAsync(MainPanelControl panel)
    {
        try
        {
            await panel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TraceFlyout($"Main panel disposal failed: {exception}");
        }
    }

    private void EnsureFlyoutContent(double panelHeight)
    {
        if (_mainFlyoutIsland.Content is MainPanelControl existingPanel)
        {
            existingPanel.SetHostHeight(panelHeight);
            return;
        }

        var panel = new MainPanelControl();
        panel.SetHostHeight(panelHeight);
        panel.KeepFlyoutOpen = KeepMainFlyoutOpen;
        _mainFlyoutIsland.Content = panel;
    }

    private void ConfigureMainFlyout(double panelHeight)
    {
        var height = new GridLength(panelHeight);
        _mainFlyout.FlyoutWidth = new GridLength(MainPanelWidth);
        _mainFlyout.FlyoutHeight = height;
        _mainFlyoutIsland.IslandHeight = height;
    }

    private void RequestExit(Action exitAction)
    {
        TraceFlyout("Exit requested from context menu");
        _contextMenu.Hide();
        _dispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => exitAction());
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                _trayIcon.SetIcon(GetIconPath());
            }
        });
    }

    private static double GetMainPanelHeight(Point anchorPoint)
    {
        var monitor = MonitorFromPoint(
            new NativePoint(anchorPoint.X, anchorPoint.Y),
            MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };

        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return AppSettings.MainPanelHeight;
        }

        var dpiY = 96u;
        _ = GetDpiForMonitor(monitor, MonitorDpiType.Effective, out _, out dpiY);

        var workAreaHeightInPixels = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        var workAreaHeightInDips = workAreaHeightInPixels * 96d / Math.Max(96u, dpiY);
        return Math.Max(
            MinimumPanelHeight,
            Math.Min(workAreaHeightInDips - FlyoutWorkAreaInset, AppSettings.MainPanelHeight));
    }

    private static Point GetContextMenuPoint(Point anchorPoint)
    {
        var monitor = MonitorFromPoint(
            new NativePoint(anchorPoint.X, anchorPoint.Y),
            MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };

        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new Point(anchorPoint.X, anchorPoint.Y - 32);
        }

        var workAreaMiddle = monitorInfo.WorkArea.Top
            + ((monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / 2);
        var verticalOffset = anchorPoint.Y >= workAreaMiddle ? -32 : 16;
        return new Point(anchorPoint.X, anchorPoint.Y + verticalOffset);
    }

    private static string GetIconPath()
    {
        var assetName = IsSystemUsingLightTheme()
            ? "Assets/TrayIconDark.ico"
            : "Assets/TrayIconLight.ico";

        try
        {
            return Path.Combine(Package.Current.InstalledLocation.Path, assetName);
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, assetName);
        }
    }

    private static bool IsSystemUsingLightTheme()
    {
        const string personalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(personalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is int value ? value != 0 : true;
    }

    /// <summary>
    /// Writes a tray diagnostic. The whole call, including the message built by the caller, is
    /// compiled out of release builds, and so is the file log behind it.
    /// </summary>
    [Conditional("DEBUG")]
    private static void TraceFlyout(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TrayFlyout] {message}");
#if DEBUG
        AppendTraceLine(message);
#endif
    }

#if DEBUG
    /// <summary>
    /// Mirrors the diagnostics to a bounded log file next to the database, because
    /// <see cref="Debug.WriteLine"/> is only visible to an attached debugger.
    /// </summary>
    private static void AppendTraceLine(string message)
    {
        try
        {
            if (!_traceFileResolved)
            {
                _traceFileResolved = true;
                _traceFilePath = ResolveTraceFilePath();
            }

            if (_traceFilePath is null)
            {
                return;
            }

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [TrayFlyout] {message}";
            lock (TraceFileGate)
            {
                var file = new FileInfo(_traceFilePath);
                if (file.Exists && file.Length > TraceFileMaxBytes)
                {
                    File.Delete(_traceFilePath);
                }

                File.AppendAllText(_traceFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never take the tray down.
        }
    }

    private static string? ResolveTraceFilePath()
    {
        try
        {
            var directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "FeedPeek");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "tray-flyout.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:HH:mm:ss.fff}] [TrayFlyout] Log file: {path}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null;
        }
    }
#endif

    private const uint MonitorDefaultToNearest = 2;

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    private sealed class DelegateCommand : ICommand
    {
        private readonly Action _execute;

        public DelegateCommand(Action execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
