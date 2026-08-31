using BiliRadar.Helpers;
using BiliRadar.Models;
using FeedPeek.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BiliRadar.Services;

internal sealed class BackgroundNotificationMonitor : IDisposable
{
    private readonly FeedPeekNotificationSession _session;
    private readonly NotificationService _notificationService = new();

    public BackgroundNotificationMonitor(CookieStore cookieStore)
    {
        _session = new FeedPeekNotificationSession(cookieStore);
    }

    public Task StartAsync()
        => _notificationService.TryStartAsync(RefreshNotificationDataAsync);

    public async Task HandleActivationAsync(NotificationService.NotificationActivationRequest request)
    {
        if (string.Equals(request.Action, NotificationService.WatchLaterAction, StringComparison.OrdinalIgnoreCase)
            && request.ItemKey is FeedItemKey itemKey)
        {
            await AddToViewLaterFromNotificationAsync(itemKey);
            return;
        }

        if (request.Uri is not null)
            await NotificationService.LaunchUriAsync(request.Uri);
    }

    public void Dispose()
    {
        _notificationService.Stop();
        _session.Dispose();
    }

    private async Task RefreshNotificationDataAsync()
    {
        IReadOnlyList<FeedItem> updates;
        try
        {
            updates = await _session.RefreshUpdatesAsync();
        }
        catch (Exception exception)
        {
            NotificationService.TraceNotification(
                $"Update refresh failed: {exception.GetType().Name}: {exception.Message}");
            updates = [];
        }

        IReadOnlyList<FeedLiveNotificationItem> liveItems;
        try
        {
            liveItems = await _session.GetLiveItemsAsync();
        }
        catch (Exception exception)
        {
            NotificationService.TraceNotification(
                $"Live refresh failed: {exception.GetType().Name}: {exception.Message}");
            liveItems = [];
        }

        NotificationService.TraceNotification(
            $"Refresh returned updates={updates.Count}, live={liveItems.Count}, " +
            $"mode={AppSettings.NotificationTargetMode}");

        if (AppSettings.NotificationTargetMode == NotificationTargetMode.CustomCreators)
        {
            var rules = AppSettings.CustomNotificationCreators;
            updates = updates.Where(item => MatchesRule(item, rules)).ToArray();
            liveItems = liveItems.Where(item => MatchesRule(item, rules)).ToArray();
            NotificationService.TraceNotification(
                $"Custom creator filter retained updates={updates.Count}, live={liveItems.Count}, " +
                $"rules={rules.Count}");
        }

        await _notificationService.NotifyFeedUpdatesAsync(
            updates,
            item => _session.CanAddToViewLater(item.ProviderId));
        await _notificationService.NotifyLiveStartsAsync(liveItems);
    }

    private static bool MatchesRule(
        FeedItem item,
        IReadOnlyList<NotificationCreatorSubscription> rules)
        => !string.IsNullOrWhiteSpace(item.CreatorKey)
            && rules.Any(rule =>
                rule.VideoNotificationsEnabled
                && string.Equals(rule.ProviderId, item.ProviderId, StringComparison.Ordinal)
                && string.Equals(rule.SourceId, item.SourceId, StringComparison.Ordinal)
                && string.Equals(rule.CreatorKey, item.CreatorKey, StringComparison.Ordinal));

    private static bool MatchesRule(
        FeedLiveNotificationItem item,
        IReadOnlyList<NotificationCreatorSubscription> rules)
        => rules.Any(rule =>
            rule.LiveNotificationsEnabled
            && string.Equals(rule.ProviderId, item.ProviderId, StringComparison.Ordinal)
            && string.Equals(rule.SourceId, item.SourceId, StringComparison.Ordinal)
            && string.Equals(rule.CreatorKey, item.Item.CreatorKey, StringComparison.Ordinal));

    private async Task AddToViewLaterFromNotificationAsync(FeedItemKey itemKey)
    {
        try
        {
            await _session.AddToViewLaterAsync(itemKey);
            NotificationService.ShowStatusNotification(
                LocalizationHelper.GetString("AddedToViewLater"),
                LocalizationHelper.GetString("AddedToViewLaterDetail"));
        }
        catch (Exception ex)
        {
            NotificationService.ShowStatusNotification(
                LocalizationHelper.GetString("AddToViewLaterFailed"),
                ex.Message);
        }
    }
}
