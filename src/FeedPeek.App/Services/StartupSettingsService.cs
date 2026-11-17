using FeedPeek.Helpers;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FeedPeek.Services;

public sealed record StartupSettingsStatus(bool IsEnabled, bool CanChange, string Message);

public sealed record StartupSettingsResult(bool Succeeded, StartupSettingsStatus Status);

public sealed class StartupSettingsService
{
    private const string StartupTaskId = "FeedPeekPreviewStartupTask";
    private const string StartupRegistryName = "FeedPeek";
    private StartupTask? _startupTask;
    private bool _useRegistryStartup;

    public StartupSettingsStatus CurrentStatus { get; private set; } = new(false, false, string.Empty);

    public async Task<StartupSettingsStatus> LoadAsync()
    {
        try
        {
            _startupTask = await StartupTask.GetAsync(StartupTaskId);
            _useRegistryStartup = false;
            return CurrentStatus = GetPackagedStatus();
        }
        catch
        {
        }

        _startupTask = null;
        _useRegistryStartup = true;
        return CurrentStatus = GetRegistryStatus(IsRegistryStartupEnabled());
    }

    public async Task<StartupSettingsResult> SetEnabledAsync(bool isEnabled)
    {
        if (_useRegistryStartup)
        {
            try
            {
                SetRegistryStartupEnabled(isEnabled);
                CurrentStatus = GetRegistryStatus(IsRegistryStartupEnabled());
                return new(CurrentStatus.IsEnabled == isEnabled, CurrentStatus);
            }
            catch
            {
                bool actual;
                try { actual = IsRegistryStartupEnabled(); }
                catch { actual = false; }
                CurrentStatus = GetRegistryStatus(actual);
                return new(false, CurrentStatus);
            }
        }

        if (_startupTask is null)
        {
            CurrentStatus = new(false, false, string.Empty);
            return new(!isEnabled, CurrentStatus);
        }

        try
        {
            if (isEnabled)
            {
                var state = await _startupTask.RequestEnableAsync();
                CurrentStatus = GetPackagedStatus();
                if (state is not StartupTaskState.Enabled)
                    return new(false, CurrentStatus);
            }
            else if (_startupTask.State is StartupTaskState.Enabled)
            {
                _startupTask.Disable();
            }

            CurrentStatus = GetPackagedStatus();
            return new(CurrentStatus.IsEnabled == isEnabled, CurrentStatus);
        }
        catch
        {
            CurrentStatus = GetPackagedStatus();
            return new(false, CurrentStatus);
        }
    }

    private StartupSettingsStatus GetPackagedStatus()
    {
        var state = _startupTask!.State;
        return new(
            state is StartupTaskState.Enabled,
            state is StartupTaskState.Disabled or StartupTaskState.Enabled,
            state switch
            {
                StartupTaskState.DisabledByUser => LocalizationHelper.GetString("StartupDisabledByUser"),
                StartupTaskState.DisabledByPolicy => LocalizationHelper.GetString("StartupDisabledByPolicy"),
                _ => string.Empty,
            });
    }

    private static StartupSettingsStatus GetRegistryStatus(bool isEnabled)
        => new(isEnabled, true, LocalizationHelper.GetString("StartupTaskFallback"));

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
            key?.SetValue(StartupRegistryName, GetRegistryStartupCommand());
        else
            key?.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
    }

    private static string GetRegistryStartupCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        return $"\"{path}\"";
    }
}
