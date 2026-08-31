using BiliRadar.Helpers;
using BiliRadar.Models;
using FeedPeek.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace BiliRadar.Services;

public sealed class NotificationService
{
    public const string OpenAction = "open";
    public const string WatchLaterAction = "watchLater";
    private const int MaxStoredVideoUpdateIds = 80;
    private const int MaxStoredLiveRoomIds = 80;
    private const int MaxNotificationsPerRefresh = 3;
    private static readonly TimeSpan NotificationSpacing = TimeSpan.FromMilliseconds(750);
    private DispatcherTimer? _timer;
    private Func<Task>? _refreshAction;
    private bool _isRefreshing;
    private DateTimeOffset _serviceStartedAt;

    public DateTimeOffset ServiceStartedAt => _serviceStartedAt;

    public async Task TryStartAsync(Func<Task> refreshAction)
    {
        _refreshAction = refreshAction;
        _serviceStartedAt = DateTimeOffset.Now;
        AppSettings.VideoNotificationBaselineInitialized = false;
        AppSettings.LiveNotificationBaselineInitialized = false;
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
    }

    public async Task NotifyVideoUpdatesAsync(IReadOnlyList<BiliVideoUpdate> updates, bool showNotifications = true)
    {
        var plan = NotificationBaselinePlanner.ForVideos(
            updates,
            AppSettings.KnownVideoUpdateIds,
            AppSettings.VideoNotificationBaselineInitialized,
            MaxNotificationsPerRefresh,
            MaxStoredVideoUpdateIds);
        AppSettings.KnownVideoUpdateIds = plan.KnownIds;
        if (plan.InitializesBaseline)
        {
            AppSettings.VideoNotificationBaselineInitialized = true;
            return;
        }

        if (!showNotifications || !IsVideoNotificationEnabled() || plan.NewItems.Count == 0)
        {
            return;
        }

        foreach (var update in plan.NewItems.OrderBy(item => item.PublishedAt))
        {
            ShowVideoNotification(update);
            await Task.Delay(NotificationSpacing);
        }
    }

    public async Task NotifyLiveStartsAsync(IReadOnlyList<BiliLiveCreator> liveCreators, bool showNotifications = true)
    {
        var plan = NotificationBaselinePlanner.ForLiveCreators(
            liveCreators,
            AppSettings.KnownLiveRoomIds,
            AppSettings.LiveNotificationBaselineInitialized,
            MaxNotificationsPerRefresh,
            MaxStoredLiveRoomIds);
        AppSettings.KnownLiveRoomIds = plan.KnownIds;
        if (plan.InitializesBaseline)
        {
            AppSettings.LiveNotificationBaselineInitialized = true;
            return;
        }

        if (!showNotifications || !IsLiveNotificationEnabled() || plan.NewItems.Count == 0)
        {
            return;
        }

        foreach (var creator in plan.NewItems)
        {
            ShowLiveNotification(creator);
            await Task.Delay(NotificationSpacing);
        }
    }

    public async Task NotifyFeedUpdatesAsync(
        IReadOnlyList<FeedItem> items,
        bool showNotifications = true)
    {
        var plan = FeedNotificationBaselinePlanner.ForFeedItems(
            items,
            AppSettings.KnownVideoUpdateIds,
            AppSettings.VideoNotificationBaselineInitialized,
            MaxNotificationsPerRefresh,
            MaxStoredVideoUpdateIds);
        AppSettings.KnownVideoUpdateIds = plan.KnownIds;
        if (plan.InitializesBaseline)
        {
            AppSettings.VideoNotificationBaselineInitialized = true;
            return;
        }

        if (!showNotifications || !IsVideoNotificationEnabled() || plan.NewItems.Count == 0)
            return;

        foreach (var item in plan.NewItems.OrderBy(item => item.PublishedAt))
        {
            ShowFeedNotification(item);
            await Task.Delay(NotificationSpacing);
        }
    }

    public async Task NotifyLiveStartsAsync(
        IReadOnlyList<FeedLiveNotificationItem> liveItems,
        bool showNotifications = true)
    {
        var plan = FeedNotificationBaselinePlanner.ForLiveItems(
            liveItems,
            AppSettings.KnownLiveRoomIds,
            AppSettings.LiveNotificationBaselineInitialized,
            MaxNotificationsPerRefresh,
            MaxStoredLiveRoomIds);
        AppSettings.KnownLiveRoomIds = plan.KnownIds;
        if (plan.InitializesBaseline)
        {
            AppSettings.LiveNotificationBaselineInitialized = true;
            return;
        }

        if (!showNotifications || !IsLiveNotificationEnabled() || plan.NewItems.Count == 0)
            return;

        foreach (var item in plan.NewItems)
        {
            ShowLiveNotification(item);
            await Task.Delay(NotificationSpacing);
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
        if (_timer is null)
        {
            return;
        }

        UpdateTimerState();
        if (_timer.IsEnabled)
        {
            _ = RefreshForNotificationsAsync();
        }
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
    }

    private async Task RefreshForNotificationsAsync()
    {
        if (_refreshAction is null || _isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            await _refreshAction();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static void ShowVideoNotification(BiliVideoUpdate update)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("kind", "video")
            .AddArgument("action", OpenAction)
            .AddArgument("url", update.Url)
            .AddText(LocalizationHelper.Format("VideoNotificationTitle", update.CreatorName))
            .AddText(update.Title)
            .AddButton(new AppNotificationButton(LocalizationHelper.GetString("AddToViewLaterButton"))
                .AddArgument("kind", "video")
                .AddArgument("action", WatchLaterAction)
                .AddArgument("aid", update.Aid.ToString()))
            .AddButton(new AppNotificationButton(LocalizationHelper.GetString("OpenVideoButton"))
                .AddArgument("kind", "video")
                .AddArgument("action", OpenAction)
                .AddArgument("url", update.Url));

        AddImages(builder, update.AvatarUrl, update.CoverUrl);
        TryShow(builder.BuildNotification());
    }

    private static void ShowLiveNotification(BiliLiveCreator creator)
    {
        var title = string.IsNullOrWhiteSpace(creator.Title)
            ? LocalizationHelper.GetString("LiveNotificationFallbackTitle")
            : creator.Title;

        var builder = new AppNotificationBuilder()
            .AddArgument("kind", "live")
            .AddArgument("action", OpenAction)
            .AddArgument("url", creator.Url)
            .AddText(LocalizationHelper.Format("LiveNotificationTitle", creator.Name))
            .AddText(title);

        AddImages(builder, creator.AvatarUrl, string.Empty);
        TryShow(builder.BuildNotification());
    }

    private static void ShowFeedNotification(FeedItem item)
    {
        var isVideoSite = string.Equals(
            item.ProviderId,
            BuiltInVideoSitePlugin.PluginId,
            StringComparison.Ordinal);
        var builder = new AppNotificationBuilder()
            .AddArgument("kind", "update")
            .AddArgument("action", OpenAction)
            .AddArgument("url", item.Url)
            .AddText(LocalizationHelper.Format(
                isVideoSite ? "VideoNotificationTitle" : "UpdateNotificationTitle",
                item.AuthorName))
            .AddText(item.Title);

        if (isVideoSite)
        {
            var watchLaterButton = new AppNotificationButton(LocalizationHelper.GetString("AddToViewLaterButton"))
                .AddArgument("kind", "update")
                .AddArgument("action", WatchLaterAction);
            AddFeedItemKey(watchLaterButton, item.Key);
            builder.AddButton(watchLaterButton);
        }

        builder.AddButton(new AppNotificationButton(LocalizationHelper.GetString(
                isVideoSite ? "OpenVideoButton" : "OpenItemButton"))
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
        }
        catch
        {
        }
    }

    private static bool IsAnyNotificationEnabled()
    {
        if (AppSettings.NotificationTargetMode == NotificationTargetMode.None)
        {
            return false;
        }

        if (AppSettings.NotificationTargetMode == NotificationTargetMode.CustomCreators)
        {
            return AppSettings.CustomNotificationCreators.Any(item =>
                item.VideoNotificationsEnabled || item.LiveNotificationsEnabled);
        }

        return AppSettings.VideoNotificationsEnabled || AppSettings.LiveNotificationsEnabled;
    }

    private static bool IsVideoNotificationEnabled()
    {
        if (AppSettings.NotificationTargetMode == NotificationTargetMode.None)
        {
            return false;
        }

        if (AppSettings.NotificationTargetMode == NotificationTargetMode.CustomCreators)
        {
            return AppSettings.CustomNotificationCreators.Any(item => item.VideoNotificationsEnabled);
        }

        return AppSettings.VideoNotificationsEnabled;
    }

    private static bool IsLiveNotificationEnabled()
    {
        if (AppSettings.NotificationTargetMode == NotificationTargetMode.None)
        {
            return false;
        }

        if (AppSettings.NotificationTargetMode == NotificationTargetMode.CustomCreators)
        {
            return AppSettings.CustomNotificationCreators.Any(item => item.LiveNotificationsEnabled);
        }

        return AppSettings.LiveNotificationsEnabled;
    }

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
