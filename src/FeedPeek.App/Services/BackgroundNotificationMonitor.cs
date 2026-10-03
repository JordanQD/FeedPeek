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
    private readonly NotificationDeliveryQueue<FeedItemKey, FeedItem> _pendingUpdates = new(item => item.Key);
    private readonly NotificationDeliveryQueue<string, LiveDelivery> _pendingLive = new(delivery => delivery.Item.StableKey);
    private sealed record LiveDelivery(NotificationFeedKey Feed, FeedLiveNotificationItem Item);

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
        _baselines.Dispose();
        _pendingUpdates.Dispose();
        _pendingLive.Dispose();
        _shutdown.Dispose();
    }

    private async Task RefreshNotificationDataAsync()
    {
        var token = _shutdown.Token;
        using var configurationLease = await ConfigurationMutationGate.EnterAsync(token);
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
        var updates = batch.Updates.SelectMany(result => _baselines.ObserveUpdates(result.Feed, result.Items,
            result.ObservedAt, result.CanInitializeBaseline))
            .DistinctBy(item => item.Key).ToArray();
        var live = batch.Live.SelectMany(result =>
        {
            var starts = _baselines.ObserveLive(result.Feed, result.Items
                .Select(item => new NotificationLiveObservation(item.StableKey, item.Item.CreatorKey)).ToArray()).ToHashSet(StringComparer.Ordinal);
            return result.Items.Where(item => starts.Contains(item.StableKey)).Select(item => new LiveDelivery(result.Feed, item));
        }).DistinctBy(delivery => delivery.Item.StableKey).ToArray();
        using (_pendingUpdates.BeginBatch())
            foreach (var item in updates)
                _pendingUpdates.Enqueue(item, batch.Watches.Where(watch => watch.Key.Feed ==
                    new NotificationFeedKey(item.ProviderId, item.SourceId, item.FeedId, NotificationKind.Updates)
                    && (watch.Key.CreatorKey is null || watch.Key.CreatorKey == item.CreatorKey)));
        var liveSnapshots = batch.Live.ToDictionary(result => result.Feed,
            result => result.Items.Select(item => item.StableKey).ToHashSet());
        // A full successful offline snapshot also retires an unsent old start; failure never does.
        _pendingLive.RemoveWhere(delivery => liveSnapshots.TryGetValue(delivery.Feed, out var online)
            && !online.Contains(delivery.Item.StableKey));
        using (_pendingLive.BeginBatch())
            foreach (var delivery in live)
                _pendingLive.Enqueue(delivery, batch.Watches.Where(watch => watch.Key.Feed == delivery.Feed
                    && (watch.Key.CreatorKey is null || watch.Key.CreatorKey == delivery.Item.Item.CreatorKey)));
        // Observation acknowledgment transfers responsibility to the delivery queues, including retry on submission errors.
        _session.AcknowledgeUpdates(batch);
        NotificationService.TraceNotification($"Notification plan: watches={batch.Watches.Count}, updates={updates.Length}, live={live.Length}");
        bool IsCurrent() => !token.IsCancellationRequested && revision == NotificationStateChanges.Revision;
        var observableFeeds = batch.Updates.Select(result => result.Feed).ToHashSet();
        var pendingUpdates = _pendingUpdates.GetPending(batch.Watches, item => observableFeeds.Contains(
            new(item.ProviderId, item.SourceId, item.FeedId, NotificationKind.Updates)));
        var pendingLive = _pendingLive.GetPending(batch.Watches, delivery => liveSnapshots.TryGetValue(delivery.Feed, out var online)
            && online.Contains(delivery.Item.StableKey));
        await _notificationService.NotifyFeedUpdatesAsync(pendingUpdates,
            item => _session.CanAddToViewLater(item.ProviderId), canNotify: _ => IsCurrent(), cancellationToken: token,
            getDisplayName: _session.GetNotificationDisplayName, onSubmitted: _pendingUpdates.Acknowledge);
        await _notificationService.NotifyLiveStartsAsync(pendingLive.Select(delivery => delivery.Item).ToArray(),
            canNotify: _ => IsCurrent(), cancellationToken: token,
            onSubmitted: item => _pendingLive.RemoveWhere(delivery => delivery.Item.StableKey == item.StableKey));
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
