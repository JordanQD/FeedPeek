using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Core.Models;
using Microsoft.UI.Xaml;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Windows.System;

namespace FeedPeek.Services;

public sealed class NotificationService
{
    public const string OpenAction = "open";
    public const string WatchLaterAction = "watchLater";
    private const int MaxNotificationsPerRefresh = 3;
    private const int MaxFeedNotificationsPerRefresh = 20;
    private static readonly TimeSpan NotificationSpacing = TimeSpan.FromMilliseconds(750);
    private DispatcherTimer? _timer;
    private Func<Task>? _refreshAction;
    private bool _isRefreshing;
    private bool _refreshPending;
    private DispatcherQueue? _dispatcherQueue;
    private DateTimeOffset _serviceStartedAt;

    public DateTimeOffset ServiceStartedAt => _serviceStartedAt;

    public async Task TryStartAsync(Func<Task> refreshAction)
    {
        _refreshAction = refreshAction;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _serviceStartedAt = DateTimeOffset.Now;
        if (_timer is null)
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(AppSettings.NotificationCheckIntervalMinutes),
            };
            _timer.Tick += OnTimerTick;
            AppSettings.NotificationSettingsChanged += AppSettings_NotificationSettingsChanged;
        }

        UpdateTimerState();
        TraceNotification(
            $"Started. mode={AppSettings.NotificationTargetMode}, " +
            $"updates={AppSettings.VideoNotificationsEnabled}, " +
            $"live={AppSettings.LiveNotificationsEnabled}, " +
            $"interval={AppSettings.NotificationCheckIntervalMinutes}m, " +
            $"timerEnabled={_timer.IsEnabled}");

        if (_timer.IsEnabled)
        {
            await RefreshForNotificationsAsync();
        }
    }

    public void Stop()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        AppSettings.NotificationSettingsChanged -= AppSettings_NotificationSettingsChanged;
        _timer = null;
        _refreshAction = null;
        _refreshPending = false;
    }

    // The monitor supplies a process-local, already filtered/deduplicated plan.
    public async Task NotifyFeedUpdatesAsync(IReadOnlyList<FeedItem> items,
        Func<FeedItem, bool>? canAddToViewLater = null, bool showNotifications = true,
        Func<FeedItem, bool>? canNotify = null, CancellationToken cancellationToken = default)
    {
        if (!showNotifications) return;
        foreach (var item in items.DistinctBy(item => item.Key).OrderBy(item => item.PublishedAt).Take(MaxFeedNotificationsPerRefresh))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canNotify?.Invoke(item) == false) continue;
            ShowFeedNotification(item, canAddToViewLater?.Invoke(item) == true);
            await Task.Delay(NotificationSpacing, cancellationToken);
        }
    }

    public async Task NotifyLiveStartsAsync(IReadOnlyList<FeedLiveNotificationItem> items,
        bool showNotifications = true, Func<FeedLiveNotificationItem, bool>? canNotify = null,
        CancellationToken cancellationToken = default)
    {
        if (!showNotifications) return;
        foreach (var item in items.DistinctBy(item => item.StableKey).Take(MaxNotificationsPerRefresh))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canNotify?.Invoke(item) == false) continue;
            ShowLiveNotification(item);
            await Task.Delay(NotificationSpacing, cancellationToken);
        }
    }

    public static bool TryGetActivationRequest(
        AppNotificationActivatedEventArgs args,
        [NotNullWhen(true)] out NotificationActivationRequest? request)
    {
        request = null;
        var action = args.Arguments.TryGetValue("action", out var actionValue)
            ? actionValue
            : OpenAction;

        if (string.Equals(action, WatchLaterAction, StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadFeedItemKey(args.Arguments, out var itemKey))
            {
                request = NotificationActivationRequest.ForWatchLater(itemKey);
                return true;
            }

            if (!args.Arguments.TryGetValue("aid", out var aidText)
                || !long.TryParse(aidText, out var aid) || aid <= 0)
            {
                return false;
            }

            request = NotificationActivationRequest.ForWatchLater(aid);
            return true;
        }

        if (!args.Arguments.TryGetValue("url", out var url)
            || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        request = NotificationActivationRequest.ForOpen(uri);
        return true;
    }

    public static Task LaunchUriAsync(Uri uri)
    {
        return Launcher.LaunchUriAsync(uri).AsTask();
    }

    public static void ShowStatusNotification(string title, string message)
    {
        TryShow(new AppNotificationBuilder()
            .AddText(title)
            .AddText(message)
            .BuildNotification());
    }

    private void OnTimerTick(object? sender, object e)
    {
        _ = RefreshForNotificationsAsync();
    }

    private void AppSettings_NotificationSettingsChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            if (_timer is null || _refreshAction is null) return;
            UpdateTimerState();
            // Even None must clear enabled watches; changes during a refresh queue a fresh pass.
            _ = RefreshForNotificationsAsync();
        }
        if (_dispatcherQueue?.HasThreadAccess == true) Apply();
        else _dispatcherQueue?.TryEnqueue(Apply);
    }

    private void UpdateTimerState()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Interval = TimeSpan.FromMinutes(AppSettings.NotificationCheckIntervalMinutes);
        if (IsAnyNotificationEnabled())
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }

        TraceNotification(
            $"Timer updated. enabled={_timer.IsEnabled}, " +
            $"interval={AppSettings.NotificationCheckIntervalMinutes}m, " +
            $"mode={AppSettings.NotificationTargetMode}");
    }

    private async Task RefreshForNotificationsAsync()
    {
        if (_refreshAction is null) return;
        if (_isRefreshing)
        {
            _refreshPending = true;
            return;
        }
        _isRefreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                var refresh = _refreshAction;
                if (refresh is null) break;
                try { await refresh(); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { TraceNotification($"Notification refresh failed: {exception}"); }
            } while (_refreshPending && _refreshAction is not null);
        }
        finally { _isRefreshing = false; }
    }

    private static void ShowFeedNotification(FeedItem item, bool canAddToViewLater)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("kind", "update")
            .AddArgument("action", OpenAction)
            .AddArgument("url", item.Url)
            .AddText(LocalizationHelper.Format(
                canAddToViewLater ? "VideoNotificationTitle" : "UpdateNotificationTitle",
                item.AuthorName))
            .AddText(item.Title);

        if (canAddToViewLater)
        {
            var watchLaterButton = new AppNotificationButton(LocalizationHelper.GetString("AddToViewLaterButton"))
                .AddArgument("kind", "update")
                .AddArgument("action", WatchLaterAction);
            AddFeedItemKey(watchLaterButton, item.Key);
            builder.AddButton(watchLaterButton);
        }

        builder.AddButton(new AppNotificationButton(LocalizationHelper.GetString(
                canAddToViewLater ? "OpenVideoButton" : "OpenItemButton"))
            .AddArgument("kind", "update")
            .AddArgument("action", OpenAction)
            .AddArgument("url", item.Url));
        AddImages(builder, item.AuthorImageUrl ?? string.Empty, item.ImageUrl ?? string.Empty);
        TryShow(builder.BuildNotification());
    }

    private static void ShowLiveNotification(FeedLiveNotificationItem liveItem)
    {
        var item = liveItem.Item;
        var title = string.IsNullOrWhiteSpace(item.Title)
            ? LocalizationHelper.GetString("LiveNotificationFallbackTitle")
            : item.Title;
        var builder = new AppNotificationBuilder()
            .AddArgument("kind", "live")
            .AddArgument("action", OpenAction)
            .AddArgument("url", item.Url)
            .AddText(LocalizationHelper.Format("LiveNotificationTitle", item.DisplayName))
            .AddText(title);
        AddImages(builder, item.AvatarUrl ?? string.Empty, item.CoverUrl ?? string.Empty);
        TryShow(builder.BuildNotification());
    }

    private static void AddFeedItemKey(AppNotificationButton button, FeedItemKey key)
    {
        button.AddArgument("providerId", key.ProviderId)
            .AddArgument("sourceId", key.SourceId)
            .AddArgument("feedId", key.FeedId)
            .AddArgument("itemId", key.ItemId);
    }

    private static bool TryReadFeedItemKey(
        IDictionary<string, string> arguments,
        out FeedItemKey key)
    {
        key = default;
        if (!arguments.TryGetValue("providerId", out var providerId)
            || !arguments.TryGetValue("sourceId", out var sourceId)
            || !arguments.TryGetValue("feedId", out var feedId)
            || !arguments.TryGetValue("itemId", out var itemId)
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(sourceId)
            || string.IsNullOrWhiteSpace(feedId)
            || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        key = new FeedItemKey(providerId, sourceId, feedId, itemId);
        return true;
    }

    private static void AddImages(AppNotificationBuilder builder, string avatarUrl, string coverUrl)
    {
        if (Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri))
        {
            builder.SetAppLogoOverride(avatarUri, AppNotificationImageCrop.Circle);
        }

        if (Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri))
        {
            builder.SetHeroImage(coverUri);
        }
    }

    private static void TryShow(AppNotification notification)
    {
        try
        {
            AppNotificationManager.Default.Show(notification);
            TraceNotification("App notification submitted");
        }
        catch (Exception exception)
        {
            TraceNotification(
                $"App notification submission failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static void TraceNotification(string message)
        => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Notification] {message}");

    private static bool IsAnyNotificationEnabled() => AppSettings.NotificationTargetMode switch
    {
        NotificationTargetMode.None => false,
        // Source rules live in SQLite. Keep the timer available in Custom mode; an empty/off
        // selection performs no provider requests, and newly added sources work immediately.
        NotificationTargetMode.CustomCreators => true,
        _ => AppSettings.VideoNotificationsEnabled || AppSettings.LiveNotificationsEnabled,
    };

    public sealed record NotificationActivationRequest(
        string Action,
        Uri? Uri,
        FeedItemKey? ItemKey,
        long Aid)
    {
        public static NotificationActivationRequest ForOpen(Uri uri)
            => new(OpenAction, uri, null, 0);

        public static NotificationActivationRequest ForWatchLater(FeedItemKey itemKey)
            => new(WatchLaterAction, null, itemKey, 0);

        public static NotificationActivationRequest ForWatchLater(long aid)
            => new(WatchLaterAction, null, null, aid);
    }
}
