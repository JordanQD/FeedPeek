using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Services;
using FeedPeek.Core.Models;
using FeedPeek.PluginHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using CommunityToolkit.WinUI.Controls;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Pages;

public sealed partial class NotificationSettingsPage : Page
{

    private FeedPeekNotificationSession? _notificationSession;
    private bool _isLoadingSettings;
    private CancellationTokenSource? _sourceSynchronizationCancellation;
    private int _modeChangeVersion;
    private readonly object _defaultNotificationTargetDescription;

    private FeedPeekNotificationSession NotificationSession
        => _notificationSession ??= new FeedPeekNotificationSession(new CookieStore());

    public ObservableCollection<NotificationSettingsGroup> NotificationGroups { get; } = [];
    private readonly Dictionary<string, bool> _expandedPlugins = new(StringComparer.Ordinal);
    private bool _addingNotification;

    public NotificationSettingsPage()
    {
        InitializeComponent();
        _defaultNotificationTargetDescription = NotificationTargetCard.Description ?? string.Empty;
        NotificationGroupsList.ItemsSource = NotificationGroups;
        Loaded += NotificationSettingsPage_Loaded;
        Unloaded += NotificationSettingsPage_Unloaded;
    }

    private async void NotificationSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _sourceSynchronizationCancellation?.Cancel();
        _sourceSynchronizationCancellation?.Dispose();
        _sourceSynchronizationCancellation = new CancellationTokenSource();
        var loadCancellation = _sourceSynchronizationCancellation;
        _isLoadingSettings = true;
        NotificationCheckIntervalBox.Value = AppSettings.NotificationCheckIntervalMinutes;
        NotificationTargetModeBox.SelectedIndex = (int)AppSettings.NotificationTargetMode;
        VideoNotificationSwitch.IsOn = AppSettings.VideoNotificationsEnabled;
        LiveNotificationSwitch.IsOn = AppSettings.LiveNotificationsEnabled;

        UpdateNotificationModeVisibility();
        _isLoadingSettings = false;
        SettingsScrollView.ScrollTo(0, 0);
        var loadedVersion = _modeChangeVersion;
        var synchronized = await SynchronizeSourceNotificationsAsync(AppSettings.NotificationTargetMode);
        if (!IsLoaded || !ReferenceEquals(loadCancellation, _sourceSynchronizationCancellation))
            return;
        if (loadedVersion == _modeChangeVersion)
            NotificationTargetCard.Description = synchronized ? _defaultNotificationTargetDescription
                : LocalizationHelper.GetString("SourceNotificationSyncFailed");
        await ReloadNotificationRulesAsync();

    }

    private void NotificationSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _modeChangeVersion++;
        _sourceSynchronizationCancellation?.Cancel();
        _sourceSynchronizationCancellation?.Dispose();
        _sourceSynchronizationCancellation = null;
        _notificationSession?.Dispose();
        _notificationSession = null;
    }

    private void VideoNotificationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.VideoNotificationsEnabled = VideoNotificationSwitch.IsOn;
    }

    private void LiveNotificationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettings.LiveNotificationsEnabled = LiveNotificationSwitch.IsOn;
    }

    private void NotificationCheckIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoadingSettings || double.IsNaN(args.NewValue))
        {
            return;
        }

        AppSettings.NotificationCheckIntervalMinutes = Math.Max(1, (int)Math.Round(args.NewValue));
    }

    private async void NotificationTargetModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var mode = (NotificationTargetMode)NotificationTargetModeBox.SelectedIndex;
        var version = ++_modeChangeVersion;
        // Complete the custom-list backfill before publishing the new mode to the monitor.
        var synchronized = mode != NotificationTargetMode.CustomCreators
            || await SynchronizeSourceNotificationsAsync(mode);
        if (version != _modeChangeVersion || !IsLoaded)
            return;
        if (!synchronized)
        {
            _isLoadingSettings = true;
            NotificationTargetModeBox.SelectedIndex = (int)AppSettings.NotificationTargetMode;
            _isLoadingSettings = false;
            NotificationTargetCard.Description = LocalizationHelper.GetString("SourceNotificationSyncFailed");
            return;
        }
        NotificationTargetCard.Description = _defaultNotificationTargetDescription;
        AppSettings.NotificationTargetMode = mode;
        UpdateNotificationModeVisibility();
        await ReloadNotificationRulesAsync();
    }

    private async Task<bool> SynchronizeSourceNotificationsAsync(NotificationTargetMode mode)
    {
        if (_sourceSynchronizationCancellation is null)
            return false;
        var token = _sourceSynchronizationCancellation.Token;
        try
        {
            await using var session = new FeedPeekPanelSession();
            await session.SynchronizeSourceNotificationRulesAsync(mode, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            NotificationService.TraceNotification($"Source notification rule synchronization failed: {exception}");
        }
        return false;
    }

    private async void PluginAddNotification_Click(object sender, RoutedEventArgs e)
    {
        if (_addingNotification || _sourceSynchronizationCancellation is null
            || sender is not FrameworkElement { DataContext: NotificationRuleRow row }) return;
        var token = _sourceSynchronizationCancellation.Token;
        _addingNotification = true;
        NotificationGroupsList.IsEnabled = false;
        try
        {
            await ShowAddNotificationDialogAsync(token,
                new NotificationAddRequest(row.ProviderId, row.SourceId, AuthorsOnly: true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && !token.IsCancellationRequested)
                SetCustomNotificationStatus(LocalizationHelper.GetString("CreatorQueryFailed") + " " + exception.Message);
        }
        finally
        {
            _addingNotification = false;
            if (IsLoaded) NotificationGroupsList.IsEnabled = true;
        }
    }

    private void PluginExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsExpander { DataContext: NotificationSettingsGroup group } expander
            && (group.Plugin?.PluginId ?? group.Items.FirstOrDefault()?.ProviderId) is { } providerId)
            expander.IsExpanded = _expandedPlugins.GetValueOrDefault(providerId);
    }

    private void PluginExpander_StateChanged(object sender, object e)
    {
        if (sender is SettingsExpander { DataContext: NotificationSettingsGroup group } expander
            && expander.IsLoaded
            && (group.Plugin?.PluginId ?? group.Items.FirstOrDefault()?.ProviderId) is { } providerId)
            _expandedPlugins[providerId] = expander.IsExpanded;
    }

    private int _ruleLoadVersion;

    private async Task ReloadNotificationRulesAsync()
    {
        if (_sourceSynchronizationCancellation is null)
            return;
        var token = _sourceSynchronizationCancellation.Token;
        var version = ++_ruleLoadVersion;
        try
        {
            await using var session = new FeedPeekPanelSession();
            var data = await NotificationSettingsData.LoadAsync(session, token);
            if (!IsLoaded || token.IsCancellationRequested || version != _ruleLoadVersion)
                return;
            NotificationGroups.Clear();
            foreach (var group in data.Groups)
                NotificationGroups.Add(group);
            EmptyNotificationsText.Visibility = NotificationGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && !token.IsCancellationRequested)
                SetCustomNotificationStatus(LocalizationHelper.GetString("NotificationListLoadFailed", "无法加载通知列表，请重新打开此页面。"));
            NotificationService.TraceNotification($"Notification settings list failed: {exception}");
        }
    }

    private void NotificationRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotificationRuleRow row })
            Frame.Navigate(typeof(NotificationDetailPage), row);
    }

    private void SetCustomNotificationStatus(string message)
    {
        CustomNotificationStatusText.Text = message;
        CustomNotificationStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNotificationModeVisibility()
    {
        var mode = AppSettings.NotificationTargetMode;
        var showSettings = mode != NotificationTargetMode.None;
        var showGlobal = mode == NotificationTargetMode.AllFollowing;
        var showCustom = mode == NotificationTargetMode.CustomCreators;

        ActiveNotificationPanel.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        GlobalNotificationPanel.Visibility = showGlobal ? Visibility.Visible : Visibility.Collapsed;
        CustomNotificationPanel.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
    }

}
