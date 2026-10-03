using FeedPeek.Models;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginHost;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedPeek.Services;

internal sealed record NotificationLivePoll(NotificationFeedKey Feed, IReadOnlyList<FeedLiveNotificationItem> Items);
internal sealed record NotificationPollBatch(IReadOnlyList<NotificationWatch> Watches,
    IReadOnlyList<NotificationCatchUpResult> Updates, IReadOnlyList<NotificationLivePoll> Live);

internal sealed class FeedPeekNotificationSession : IDisposable, IAsyncDisposable
{
    private readonly SqliteFeedStore _store;
    private readonly RssFeedProvider _rssProvider = new();
    private readonly FeedPeekPluginRuntime _pluginRuntime;
    private readonly FeedProviderRegistry _providerRegistry;
    private readonly FeedPollingService _pollingService;
    private readonly FeedSession _feedSession;
    private readonly NotificationUpdatePoller _updatePoller;
    private readonly Dictionary<SourceNotificationKey, string> _sourceNames = [];
    private readonly Dictionary<(SourceNotificationKey Source, string Creator), string> _creatorNames = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HashSet<string> _registeredPluginProviders = new(StringComparer.Ordinal);
    private readonly CreatorNotificationMembershipTracker _creatorMembership = new();
    private bool _initialized;
    private bool _disposed;

    public FeedPeekNotificationSession(CookieStore cookieStore)
    {
        ArgumentNullException.ThrowIfNull(cookieStore);
        var databasePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "FeedPeek",
            "feedpeek.db");
        _store = new SqliteFeedStore(databasePath);
        _pluginRuntime = new FeedPeekPluginRuntime(cookieStore);
        _providerRegistry = new FeedProviderRegistry([_rssProvider]);
        _pollingService = new FeedPollingService(_store, _providerRegistry);
        _feedSession = new FeedSession(_store, _pollingService);
        _updatePoller = new NotificationUpdatePoller(_store, _providerRegistry);
    }

    public Task<NotificationPollBatch> PollNotificationsAsync(NotificationSelection selection,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (selection.Mode == NotificationSelectionMode.None)
            {
                _updatePoller.Prune([]);
                return new NotificationPollBatch([], [], []);
            }
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            selection = selection with { Sources = await _store.GetSourceNotificationRulesAsync(token).ConfigureAwait(false) };
            var sources = (await _feedSession.GetSourceSummariesAsync(token).ConfigureAwait(false))
                .ToDictionary(item => new SourceNotificationKey(item.Source.ProviderId, item.Source.SourceId), item => item.Source);
            var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
            _sourceNames.Clear();
            _creatorNames.Clear();
            foreach (var source in sources.Values)
                _sourceNames[new(source.ProviderId, source.SourceId)] = source.DisplayName;
            selection = selection with { CreatorPolicies = await CreatorNotificationPolicies.MigrateAsync(_store,
                sources.Values.ToArray(), subscriptions, _pluginRuntime.GetManifest, selection.Creators, token).ConfigureAwait(false) };
            _creatorMembership.Prune(sources.Keys);
            var membership = new Dictionary<SourceNotificationKey, CreatorNotificationMembershipSnapshot>();
            // Saved IDs are a creation-time snapshot, not a user selection of individual feeds.
            // Re-resolve capability changes without saving stale switches over concurrent UI edits.
            selection = selection with { Sources = selection.Sources.Select(rule =>
            {
                if (!sources.TryGetValue(rule.Key, out var source)) return rule;
                var isRss = source.ProviderId == RssProviderInfo.ProviderId;
                return SourceNotificationCapabilities.Resolve(source, subscriptions,
                    supportsUpdates: isRss || _pluginRuntime.SupportsCapability(source.ProviderId, "feed.updates"),
                    supportsLive: !isRss && _pluginRuntime.SupportsCapability(source.ProviderId, "feed.live"))
                    .ApplyTo(rule);
            }).ToArray() };
            var watches = new List<NotificationWatch>();
            var updates = new List<NotificationCatchUpResult>();
            var live = new List<NotificationLivePoll>();
            foreach (var subscription in subscriptions.Where(item => item.IsEnabled && IsAvailableProvider(item.ProviderId)))
            {
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(new(subscription.ProviderId, subscription.SourceId), out var source)) continue;
                var definition = source.Feeds.FirstOrDefault(feed => feed.FeedId == subscription.FeedId && feed.SupportsNotifications);
                var kind = definition?.Kind switch
                {
                    "feed" or "rss" or "atom" => NotificationKind.Updates,
                    "live" => NotificationKind.Live,
                    _ => (NotificationKind?)null,
                };
                if (kind is null) continue;
                var isRss = subscription.ProviderId == RssProviderInfo.ProviderId;
                if (!isRss && !_pluginRuntime.SupportsCapability(subscription.ProviderId,
                        kind == NotificationKind.Updates ? "feed.updates" : "feed.live")) continue;
                if (isRss && kind == NotificationKind.Live) continue;
                var key = new NotificationFeedKey(subscription.ProviderId, subscription.SourceId, subscription.FeedId, kind.Value);
                IReadOnlyList<NotificationWatchKey> selected = NotificationWatchSelector.Select(key, selection);
                if (selected.Count == 0) continue;
                var sourceKey = new SourceNotificationKey(key.ProviderId, key.SourceId);
                var authors = CreatorNotificationPolicies.SupportsAuthors(_pluginRuntime.GetManifest(key.ProviderId));
                CreatorNotificationMembershipSnapshot? authorSnapshot = null;
                if (authors)
                {
                    if (!membership.TryGetValue(sourceKey, out authorSnapshot))
                    {
                        // Use an actual notification feed context, preferring updates over history/collections.
                        var context = subscriptions.FirstOrDefault(item => item.IsEnabled
                            && item.ProviderId == key.ProviderId && item.SourceId == key.SourceId
                            && source.Feeds.Any(feed => feed.FeedId == item.FeedId && feed.SupportsNotifications
                                && feed.Kind is "feed" or "rss" or "atom")) ?? subscription;
                        authorSnapshot = await GetNotificationMembershipAsync(context, selection, token).ConfigureAwait(false);
                        membership[sourceKey] = authorSnapshot;
                    }
                    selected = CreatorNotificationMembershipTracker.ExpandWatches(key, selected, authorSnapshot);
                    foreach (var creator in authorSnapshot.Creators.Values)
                        _creatorNames[(sourceKey, creator.CreatorKey)] = creator.DisplayName;
                }
                var policy = selection.CreatorPolicies?.FirstOrDefault(item => item.Key == sourceKey);
                var useCreatorScope = policy is null || (kind == NotificationKind.Updates
                    ? policy.UpdatesScope : policy.LiveScope) == CreatorNotificationScope.SelectedCreators;
                watches.AddRange(selected.Select(watch => new NotificationWatch(watch,
                    NotificationStateChanges.Generation(watch, selection.Mode,
                        subscription.SubscriptionId + "|" + subscription.CredentialProfileId + "|" + subscription.Endpoint,
                        includeCreatorScope: useCreatorScope))));
                // Retain existing watches on failure, but never turn a failed relationship query into observations.
                if (selected.Count == 0 || authorSnapshot is { CanObserve: false }) continue;
                try
                {
                    if (kind == NotificationKind.Updates)
                    {
                        var generation = NotificationStateChanges.Generation(new(key), selection.Mode,
                            subscription.SubscriptionId + "|" + subscription.CredentialProfileId + "|" + subscription.Endpoint,
                            includeCreatorScope: false);
                        var poll = await _updatePoller.PollAsync(subscription, generation, token).ConfigureAwait(false);
                        updates.Add(poll);
                        if (!poll.IsComplete)
                            NotificationService.TraceNotification($"Notification catch-up pending. provider={key.ProviderId}, source={key.SourceId}, feed={key.FeedId}, gaps={poll.PendingGaps}, cursor={poll.ContinuationCursor}, error={poll.Error}");
                    }
                    else
                    {
                        var page = await _pluginRuntime.GetLivePageAsync(subscription, token).ConfigureAwait(false);
                        if (!page.IsComplete)
                        {
                            NotificationService.TraceNotification($"Incomplete live query; retaining state. source={key.SourceId}");
                            continue;
                        }
                        if (page.Items.Any(item => string.IsNullOrWhiteSpace(item.CreatorKey) || string.IsNullOrWhiteSpace(item.Url)))
                            throw new InvalidOperationException("直播查询返回了无效的房间身份。");
                        live.Add(new(key, page.Items.Select(item => new FeedLiveNotificationItem(key.ProviderId, key.SourceId, item)).ToArray()));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    // Do not emit a successful empty observation for a failed or partial query.
                    NotificationService.TraceNotification($"Notification query failed. provider={key.ProviderId}, source={key.SourceId}, feed={key.FeedId}: {exception}");
                }
            }
            _updatePoller.Prune(watches.Where(watch => watch.Key.Feed.Kind == NotificationKind.Updates)
                .Select(watch => watch.Key.Feed));
            return new NotificationPollBatch(watches, updates, live);
        }, cancellationToken);

    private async Task<CreatorNotificationMembershipSnapshot> GetNotificationMembershipAsync(
        FeedSubscription subscription, NotificationSelection selection, CancellationToken token)
    {
        var key = new SourceNotificationKey(subscription.ProviderId, subscription.SourceId);
        var generation = NotificationStateChanges.MembershipGeneration(key, subscription.CredentialProfileId);
        try
        {
            if (_pluginRuntime.GetManifest(key.ProviderId)?.Auth?.Required == true
                && (string.IsNullOrWhiteSpace(subscription.CredentialProfileId)
                    || !await _pluginRuntime.HasCredentialsAsync(key.ProviderId, subscription.CredentialProfileId, token).ConfigureAwait(false)))
                throw new PluginInvocationException("AUTH_REQUIRED", "请先登录来源插件账号。");
            if (_pluginRuntime.SupportsCapability(key.ProviderId, "creator.following"))
            {
                var page = await _pluginRuntime.GetFollowingCreatorPageAsync(subscription, token).ConfigureAwait(false);
                ThrowIfCreatorGenerationChanged(key, subscription.CredentialProfileId, generation);
                CreatorNotificationStatusCache.Publish(key, generation, page.Creators, page.IsComplete);
                return _creatorMembership.Observe(key, generation, page);
            }

            // Resolve-only plugins can confirm explicit choices, but cannot enumerate all followed authors.
            var policy = selection.CreatorPolicies?.FirstOrDefault(item => item.Key == key);
            if (selection.Mode != NotificationSelectionMode.Custom || policy is null
                || (policy.UpdatesEnabled && policy.UpdatesScope == CreatorNotificationScope.AllFollowing)
                || (policy.LiveEnabled && policy.LiveScope == CreatorNotificationScope.AllFollowing))
            {
                _creatorMembership.Clear(key);
                CreatorNotificationStatusCache.Publish(key, generation, [], false);
                return CreatorNotificationMembershipSnapshot.Empty;
            }
            var followed = new List<CreatorDescriptor>();
            var states = new List<(string Key, CreatorFollowState State)>();
            foreach (var creatorKey in selection.Creators.Where(creator => creator.ProviderId == key.ProviderId
                && creator.SourceId == key.SourceId).Select(creator => creator.CreatorKey).Distinct(StringComparer.Ordinal))
            {
                var result = await _pluginRuntime.ResolveCreatorAsync(subscription, creatorKey, token).ConfigureAwait(false);
                if (result.FollowState == CreatorFollowState.Unknown)
                    throw new PluginInvocationException("CREATOR_RELATION_UNKNOWN", "无法确认作者关注关系。");
                if (result.FollowState == CreatorFollowState.Followed)
                    followed.Add(result.Creator ?? throw new InvalidOperationException("作者查询缺少身份。"));
                states.Add((creatorKey, result.FollowState));
            }
            ThrowIfCreatorGenerationChanged(key, subscription.CredentialProfileId, generation);
            CreatorNotificationStatusCache.Publish(key, generation, followed, false);
            foreach (var state in states)
                CreatorNotificationStatusCache.SetCreatorState(key, subscription.CredentialProfileId, state.Key, state.State, generation);
            return _creatorMembership.Observe(key, generation, new(followed, true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PluginInvocationException exception) when (exception.Code == "CREATOR_ACCOUNT_CHANGED")
        {
            NotificationService.TraceNotification(
                $"Following query discarded after account change. provider={key.ProviderId}, source={key.SourceId}");
            return _creatorMembership.GetSnapshot(key, generation);
        }
        catch (Exception exception)
        {
            NotificationService.TraceNotification($"Following query failed; retaining notification state. provider={key.ProviderId}, source={key.SourceId}: {exception}");
            return _creatorMembership.GetSnapshot(key, generation);
        }
    }

    public Task EnsureCreatorNotificationPolicyAsync(NotificationCreatorTarget requestedTarget,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            var target = (await GetCreatorTargetsCoreAsync(token).ConfigureAwait(false)).FirstOrDefault(item =>
                item.Subscription.ProviderId == requestedTarget.Subscription.ProviderId
                && item.Subscription.SourceId == requestedTarget.Subscription.SourceId)
                ?? throw new PluginInvocationException("CREATOR_SOURCE_UNAVAILABLE", "所选来源已被删除或停用。");
            var key = new SourceNotificationKey(target.Subscription.ProviderId, target.Subscription.SourceId);
            // A direct author-add call can precede the settings list load. Migrate any valid old
            // source/author configuration before INSERT-if-missing so its All/mixed range is preserved.
            var sources = await _feedSession.GetSourceSummariesAsync(token).ConfigureAwait(false);
            var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
            await CreatorNotificationPolicies.MigrateAsync(_store, sources.Select(item => item.Source).ToArray(),
                subscriptions, _pluginRuntime.GetManifest, CreatorNotificationPolicies.SavedCreators(), token).ConfigureAwait(false);
            // User-initiated only. INSERT-if-missing preserves an existing AllFollowing policy and disabled gates.
            await _store.EnsureCreatorNotificationPolicyAsync(new(key.ProviderId, key.SourceId,
                CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.SelectedCreators,
                target.Capabilities.SupportsUpdates, target.Capabilities.SupportsLive), token).ConfigureAwait(false);
            AppSettings.NotifyNotificationSettingsChanged();
        }, cancellationToken);

    public bool CanAddToViewLater(string providerId)
        => _pluginRuntime.SupportsCapability(providerId, "collection.viewLater")
            && _pluginRuntime.SupportsCapability(providerId, "item.actions");

    public void AcknowledgeUpdates(NotificationPollBatch batch)
    {
        foreach (var result in batch.Updates) _updatePoller.Acknowledge(result);
    }

    public string GetNotificationDisplayName(FeedItem item)
    {
        if (CreatorNotificationPolicies.SupportsAuthors(_pluginRuntime.GetManifest(item.ProviderId)))
            return item.CreatorKey is not null && _creatorNames.TryGetValue((new(item.ProviderId, item.SourceId), item.CreatorKey), out var name)
                ? name : string.IsNullOrWhiteSpace(item.AuthorName) ? "作者" : item.AuthorName;
        return _sourceNames.GetValueOrDefault(new(item.ProviderId, item.SourceId)) ?? item.AuthorName;
    }

    public Task<IReadOnlyList<NotificationCreatorTarget>> GetCreatorNotificationTargetsAsync(
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            return await GetCreatorTargetsCoreAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<FollowingCreatorsPage> GetFollowingNotificationCreatorsAsync(
        NotificationCreatorTarget requestedTarget,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            var target = (await GetCreatorTargetsCoreAsync(token).ConfigureAwait(false))
                .FirstOrDefault(item => item.Subscription.ProviderId == requestedTarget.Subscription.ProviderId
                    && item.Subscription.SourceId == requestedTarget.Subscription.SourceId)
                ?? throw new PluginInvocationException(
                    "CREATOR_SOURCE_UNAVAILABLE",
                    "所选来源已被删除、停用或不再支持作者通知。");
            if (!target.SupportsFollowing)
                throw new PluginInvocationException(
                    "NOT_SUPPORTED",
                    "当前来源不支持查询关注作者列表。");

            var key = new SourceNotificationKey(target.Subscription.ProviderId, target.Subscription.SourceId);
            var generation = NotificationStateChanges.MembershipGeneration(
                key,
                target.Subscription.CredentialProfileId);
            await EnsureCreatorCredentialsAsync(target, token).ConfigureAwait(false);
            ThrowIfCreatorGenerationChanged(key, target.Subscription.CredentialProfileId, generation);

            var page = await _pluginRuntime.GetFollowingCreatorPageAsync(target.Subscription, token)
                .ConfigureAwait(false);
            ThrowIfCreatorGenerationChanged(key, target.Subscription.CredentialProfileId, generation);

            var filtered = FilterCreatorNotificationKinds(page.Creators, target.Capabilities);
            ThrowIfCreatorGenerationChanged(key, target.Subscription.CredentialProfileId, generation);
            CreatorNotificationStatusCache.Publish(key, generation, filtered, page.IsComplete);
            return new FollowingCreatorsPage(filtered, page.IsComplete);
        }, cancellationToken);

    private async Task<IReadOnlyList<NotificationCreatorTarget>> GetCreatorTargetsCoreAsync(CancellationToken token)
    {
        var sources = (await _feedSession.GetSourceSummariesAsync(token).ConfigureAwait(false))
            .ToDictionary(summary => new SourceNotificationKey(summary.Source.ProviderId, summary.Source.SourceId), summary => summary.Source);
        var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
        var targets = new List<NotificationCreatorTarget>();
        foreach (var group in subscriptions.Where(item => item.IsEnabled && _pluginRuntime.IsPluginProvider(item.ProviderId))
            .GroupBy(item => new SourceNotificationKey(item.ProviderId, item.SourceId)))
        {
            var manifest = _pluginRuntime.GetManifest(group.Key.ProviderId)!;
            var canResolve = manifest.Capabilities.Contains("creator.resolve", StringComparer.Ordinal);
            var canFollow = manifest.Capabilities.Contains("creator.following", StringComparer.Ordinal);
            if (!CreatorNotificationPolicies.SupportsAuthors(manifest)
                || !sources.TryGetValue(group.Key, out var source)) continue;
            var capabilities = SourceNotificationCapabilities.Resolve(source, subscriptions,
                _pluginRuntime.SupportsCapability(source.ProviderId, "feed.updates"),
                _pluginRuntime.SupportsCapability(source.ProviderId, "feed.live"));
            if (!capabilities.HasAny) continue;
            var subscription = group.FirstOrDefault(item =>
                source.Feeds.Any(feed => feed.FeedId == item.FeedId
                    && feed.SupportsNotifications
                    && feed.Kind is "feed" or "rss" or "atom"))
                ?? group.First();
            var displayName = string.Equals(manifest.DisplayName, source.DisplayName, StringComparison.Ordinal)
                ? source.DisplayName : manifest.DisplayName + " · " + source.DisplayName;
            targets.Add(new(subscription, displayName, canResolve, manifest.CreatorResolution?.InputHosts ?? [], capabilities,
                SupportsFollowing: canFollow));
        }
        return targets;
    }

    public Task<CreatorResolutionResult> ResolveNotificationCreatorAsync(
        NotificationCreatorTarget requestedTarget, string input, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            var target = (await GetCreatorTargetsCoreAsync(token).ConfigureAwait(false))
                .FirstOrDefault(item => item.Subscription.ProviderId == requestedTarget.Subscription.ProviderId
                    && item.Subscription.SourceId == requestedTarget.Subscription.SourceId)
                ?? throw new PluginInvocationException("CREATOR_SOURCE_UNAVAILABLE", "所选来源已被删除、停用或不再支持作者通知。");
            var key = new SourceNotificationKey(target.Subscription.ProviderId, target.Subscription.SourceId);
            var generation = NotificationStateChanges.MembershipGeneration(
                key,
                target.Subscription.CredentialProfileId);
            await EnsureCreatorCredentialsAsync(target, token).ConfigureAwait(false);
            ThrowIfCreatorGenerationChanged(key, target.Subscription.CredentialProfileId, generation);
            if (target.SupportsResolve && CreatorInputRouting.TryGetWebUri(input, out var uri)
                && target.InputHosts.Count > 0 && !CreatorInputRouting.MatchesHost(uri!, target.InputHosts))
                throw new PluginInvocationException("CREATOR_INPUT_UNSUPPORTED", "作者链接不属于所选插件。");
            var result = target.SupportsResolve
                ? await _pluginRuntime.ResolveCreatorAsync(target.Subscription, input, token).ConfigureAwait(false)
                : !target.SupportsFollowing
                    ? throw new PluginInvocationException("NOT_SUPPORTED", "当前来源不支持查询关注作者列表。")
                    : CreatorInputRouting.FromFollowingPage(
                    await _pluginRuntime.GetFollowingCreatorPageAsync(target.Subscription, token).ConfigureAwait(false), input);
            ThrowIfCreatorGenerationChanged(key, target.Subscription.CredentialProfileId, generation);
            if (result.Creator is not { } creator) return result;
            var filtered = FilterCreatorNotificationKinds(creator, target.Capabilities);
            ThrowIfCreatorGenerationChanged(key, target.Subscription.CredentialProfileId, generation);
            CreatorNotificationStatusCache.SetCreatorState(key,
                target.Subscription.CredentialProfileId, creator.CreatorKey, result.FollowState, generation);
            return result with { Creator = filtered };
        }, cancellationToken);

    private async Task EnsureCreatorCredentialsAsync(
        NotificationCreatorTarget target,
        CancellationToken token)
    {
        var manifest = _pluginRuntime.GetManifest(target.Subscription.ProviderId);
        if (manifest?.Auth?.Required == true
            && (string.IsNullOrWhiteSpace(target.Subscription.CredentialProfileId)
                || !await _pluginRuntime.HasCredentialsAsync(
                    target.Subscription.ProviderId,
                    target.Subscription.CredentialProfileId,
                    token).ConfigureAwait(false)))
        {
            throw new PluginInvocationException("AUTH_REQUIRED", "请先登录所选来源的插件账号。");
        }
    }

    private static IReadOnlyList<CreatorDescriptor> FilterCreatorNotificationKinds(
        IReadOnlyList<CreatorDescriptor> creators,
        SourceNotificationCapabilities capabilities)
        => creators.Select(creator => FilterCreatorNotificationKinds(creator, capabilities)).ToArray();

    private static CreatorDescriptor FilterCreatorNotificationKinds(
        CreatorDescriptor creator,
        SourceNotificationCapabilities capabilities)
    {
        var kinds = creator.SupportedNotificationKinds
            .Where(kind => kind switch
            {
                "live" => capabilities.SupportsLive,
                "video" or "updates" => capabilities.SupportsUpdates,
                _ => false,
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return creator with { SupportedNotificationKinds = kinds };
    }

    private static void ThrowIfCreatorGenerationChanged(
        SourceNotificationKey source,
        string? credentialProfileId,
        string expectedGeneration)
    {
        if (!string.Equals(
                NotificationStateChanges.MembershipGeneration(source, credentialProfileId),
                expectedGeneration,
                StringComparison.Ordinal))
        {
            throw new PluginInvocationException(
                "CREATOR_ACCOUNT_CHANGED",
                "插件账号状态已变化，请重新查询作者信息。",
                retryable: true);
        }
    }

    public Task AddToViewLaterAsync(
        FeedItemKey key,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(
            async token =>
            {
                await EnsureInitializedAsync(token).ConfigureAwait(false);
                var stored = await _store.GetItemAsync(key, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("找不到通知对应的条目。");
                if (!CanAddToViewLater(stored.Item.ProviderId))
                    throw new InvalidOperationException("当前来源不支持稍后再看。");

                var subscriptions = await _feedSession.GetSubscriptionsAsync(token).ConfigureAwait(false);
                var subscription = subscriptions.FirstOrDefault(item =>
                    string.Equals(item.SubscriptionId, stored.SubscriptionId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("找不到通知对应的来源订阅。");
                await _pluginRuntime.ExecuteViewLaterActionAsync(
                    subscription,
                    stored.Item,
                    isSaved: true,
                    token).ConfigureAwait(false);
                await _feedSession.SetSavedForItemAsync(
                    stored.Item.ProviderId,
                    stored.Item.SourceId,
                    stored.Item.ItemId,
                    isSaved: true,
                    token).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await _feedSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }

        // Re-synced on every call: LoadAsync is a no-op until the install state changes, and this is what
        // lets a plugin installed from Settings start working without restarting the app.
        await _pluginRuntime.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in _pluginRuntime.FeedProviders)
            _providerRegistry.Register(provider);
        foreach (var stale in _registeredPluginProviders
            .Where(id => !_pluginRuntime.IsPluginProvider(id))
            .ToArray())
        {
            _providerRegistry.Unregister(stale);
            _registeredPluginProviders.Remove(stale);
        }

        foreach (var provider in _pluginRuntime.FeedProviders)
            _registeredPluginProviders.Add(provider.ProviderId);
        await _store.RepairLiveFeedNotificationCapabilitiesAsync(_pluginRuntime.AvailableProviderIds
            .Where(id => PluginManifestRules.ResolveRuntime(_pluginRuntime.GetManifest(id)!)
                == PluginManifestRules.LiveParseRuntime).ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (var subscription in await _feedSession.GetSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (_pluginRuntime.IsPluginProvider(subscription.ProviderId))
                _pluginRuntime.Register(subscription);
        }
    }

    private bool IsAvailableProvider(string providerId)
        => string.Equals(providerId, RssProviderInfo.ProviderId, StringComparison.Ordinal)
            || _pluginRuntime.IsPluginProvider(providerId);

    private async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
        => RunExclusiveAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _updatePoller.Dispose();
            await _pluginRuntime.DisposeAsync().ConfigureAwait(false);
            _rssProvider.Dispose();
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
