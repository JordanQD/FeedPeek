using FeedPeek.Helpers;
using FeedPeek.Models;
using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace FeedPeek.Services;

internal sealed class BackgroundNotificationMonitor : IDisposable
{
    private readonly FeedPeekNotificationSession _session;
    private readonly NotificationService _notificationService = new();
    private readonly NotificationBaselineTracker _baselines = new();
    private readonly CancellationTokenSource _shutdown = new();

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
        _shutdown.Cancel();
        _session.Dispose();
        _shutdown.Dispose();
    }

    private async Task RefreshNotificationDataAsync()
    {
        var token = _shutdown.Token;
        var revision = NotificationStateChanges.Revision;
        var selection = new NotificationSelection((NotificationSelectionMode)(int)AppSettings.NotificationTargetMode,
            AppSettings.VideoNotificationsEnabled, AppSettings.LiveNotificationsEnabled, [],
            AppSettings.CustomNotificationCreators.Select(rule => new CreatorNotificationRule(rule.ProviderId, rule.SourceId,
                rule.CreatorKey, rule.VideoNotificationsEnabled, rule.LiveNotificationsEnabled)).ToArray());
        var batch = await _session.PollNotificationsAsync(selection, token);
        token.ThrowIfCancellationRequested();
        // Settings may have changed while the network was pending. The service queues another pass.
        if (revision != NotificationStateChanges.Revision)
            return;
        _baselines.Synchronize(batch.Watches);
        var updates = batch.Updates.SelectMany(result => _baselines.ObserveUpdates(result.Feed, result.Items, result.ObservedAt))
            .DistinctBy(item => item.Key).ToArray();
        var live = batch.Live.SelectMany(result =>
        {
            var starts = _baselines.ObserveLive(result.Feed, result.Items
                .Select(item => new NotificationLiveObservation(item.StableKey, item.Item.CreatorKey)).ToArray()).ToHashSet(StringComparer.Ordinal);
            return result.Items.Where(item => starts.Contains(item.StableKey));
        }).DistinctBy(item => item.StableKey).ToArray();
        NotificationService.TraceNotification($"Notification plan: watches={batch.Watches.Count}, updates={updates.Length}, live={live.Length}");
        bool IsCurrent() => !token.IsCancellationRequested && revision == NotificationStateChanges.Revision;
        await _notificationService.NotifyFeedUpdatesAsync(updates,
            item => _session.CanAddToViewLater(item.ProviderId), canNotify: _ => IsCurrent(), cancellationToken: token);
        await _notificationService.NotifyLiveStartsAsync(live, canNotify: _ => IsCurrent(), cancellationToken: token);
    }

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
