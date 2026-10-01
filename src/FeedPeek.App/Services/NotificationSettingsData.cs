using FeedPeek.Core.Models;
using FeedPeek.Models;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Services;

internal sealed record NotificationSettingsData(
    IReadOnlyList<NotificationRuleRow> Rules,
    IReadOnlyList<NotificationRuleRow> AvailableSources,
    IReadOnlyList<NotificationRuleRow> VisibleRules,
    IReadOnlyList<NotificationSettingsGroup> Groups)
{
    public static async Task<NotificationSettingsData> LoadAsync(FeedPeekPanelSession session, CancellationToken token)
    {
        // The session serializes its operations; keep these calls sequential.
        var sources = await session.GetSourcesAsync(token);
        var plugins = (await session.GetPluginsAsync(token)).Select(entry => new PluginSettingsRow(entry)).ToArray();
        var descriptors = await session.GetNotificationSourcesAsync(token);
        var policies = await session.GetCreatorNotificationPoliciesAsync(token);
        var sourceRules = await session.GetSourceNotificationRulesAsync(token);
        var sourceRows = sources.ToDictionary(source => new SourceNotificationKey(source.ProviderId, source.SourceId),
            source => new SourceSettingsRow(source, plugins.FirstOrDefault(plugin => plugin.PluginId == source.ProviderId)));
        // A disabled or broken plugin is deliberately absent here, so its saved
        // source/policy row remains visible with an unavailable state.
        var capabilities = descriptors.Where(item => !sourceRows.TryGetValue(new(item.Source.ProviderId, item.Source.SourceId), out var source)
                || source.Plugin is null || source.Plugin is { IsEnabled: true, Manifest: not null, ErrorMessage: null })
            .ToDictionary(item => new SourceNotificationKey(item.Source.ProviderId, item.Source.SourceId), item => item.Capabilities);
        var policyByKey = policies.Where(policy => sourceRows.ContainsKey(policy.Key)).ToDictionary(policy => policy.Key);
        var sourceRuleByKey = sourceRules.Where(rule => sourceRows.ContainsKey(rule.Key)).ToDictionary(rule => rule.Key);
        var selectedCreatorCounts = AppSettings.CustomNotificationCreators
            .GroupBy(creator => new SourceNotificationKey(creator.ProviderId, creator.SourceId))
            .ToDictionary(group => group.Key, group => group.Count());

        var rows = new List<NotificationRuleRow>();
        foreach (var source in sources)
        {
            var key = new SourceNotificationKey(source.ProviderId, source.SourceId);
            var sourceRow = sourceRows[key];
            var sourceCapabilities = capabilities.GetValueOrDefault(key);
            var creatorEligible = SupportsAuthorNotifications(sourceRow, sourceCapabilities);
            if (policyByKey.TryGetValue(key, out var policy))
            {
                rows.Add(new(sourceRow, sourceCapabilities, null, null, policy,
                    selectedCreatorCounts.GetValueOrDefault(key), isPersisted: true));
            }
            else if (sourceRuleByKey.TryGetValue(key, out var rule))
            {
                rows.Add(new(sourceRow, sourceCapabilities, rule, null, null,
                    selectedCreatorCounts.GetValueOrDefault(key), isPersisted: true));
            }
            else if (creatorEligible)
            {
                rows.Add(new(sourceRow, sourceCapabilities, null, null,
                    new CreatorNotificationPolicy(key.ProviderId, key.SourceId,
                        CreatorNotificationScope.SelectedCreators, CreatorNotificationScope.SelectedCreators,
                        UpdatesEnabled: false, LiveEnabled: false),
                    selectedCreatorCounts.GetValueOrDefault(key), isPersisted: false));
            }
            else
            {
                rows.Add(new(sourceRow, sourceCapabilities,
                    new SourceNotificationRule(key.ProviderId, key.SourceId,
                        sourceCapabilities?.UpdateFeedIds ?? [], sourceCapabilities?.LiveFeedIds ?? [],
                        UpdateNotificationsEnabled: false, LiveNotificationsEnabled: false),
                    null, null, selectedCreatorCounts.GetValueOrDefault(key), isPersisted: false));
            }
        }

        foreach (var creator in AppSettings.CustomNotificationCreators.Where(item =>
            sourceRows.ContainsKey(new SourceNotificationKey(item.ProviderId, item.SourceId))))
        {
            var key = new SourceNotificationKey(creator.ProviderId, creator.SourceId);
            var source = sourceRows[key];
            var followState = CreatorNotificationStatusCache.GetState(key, creator.CreatorKey, source.Source.Source.CredentialProfileId);
            rows.Add(new(source, capabilities.GetValueOrDefault(key), null, creator,
                null, selectedCreatorCounts.GetValueOrDefault(key), followState, isPersisted: true));
        }

        var available = descriptors.Where(item => capabilities.ContainsKey(new(item.Source.ProviderId, item.Source.SourceId)))
            .Select(item =>
            {
                var key = new SourceNotificationKey(item.Source.ProviderId, item.Source.SourceId);
                var source = sourceRows.GetValueOrDefault(key);
                var creatorEligible = source is not null
                    && SupportsAuthorNotifications(source, item.Capabilities);
                var policy = policyByKey.GetValueOrDefault(key)
                    ?? (creatorEligible
                        ? new CreatorNotificationPolicy(key.ProviderId, key.SourceId,
                            CreatorNotificationScope.SelectedCreators,
                            CreatorNotificationScope.SelectedCreators,
                            item.Capabilities.SupportsUpdates,
                            item.Capabilities.SupportsLive)
                        : null);
                return new NotificationRuleRow(source, item.Capabilities, item.Capabilities.CreateRule(key), null,
                    policy, selectedCreatorCounts.GetValueOrDefault(key));
            }).Where(row => row.Source is not null).ToArray();

        var policyRows = rows.Where(row => row.Policy is not null && row.Creator is null)
            .ToDictionary(row => row.Key);
        var visible = rows.Where(row => row.Creator is null
            || !policyRows.TryGetValue(row.Key, out var policy)
            || policy.Policy!.ShouldShowSelectedCreators(row.Key, policy.Capabilities))
            .ToArray();

        var sourceConfigRows = rows.Where(row => row.Creator is null)
            .ToDictionary(row => row.Key);
        var authorRows = visible.Where(row => row.Creator is not null)
            .GroupBy(row => row.Key)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<NotificationRuleRow>)group.ToArray());
        var addTargets = sourceConfigRows.Values
            .Where(row => row.Source is not null
                && SupportsAuthorNotifications(row.Source, row.Capabilities))
            .ToArray();

        var groups = new List<NotificationSettingsGroup>();
        foreach (var row in sourceConfigRows.Values.Where(row =>
            row.ProviderId == RssProviderInfo.ProviderId && row.Source is not null))
        {
            groups.Add(new NotificationSettingsGroup(null, row,
                visible.Where(item => item.Key == row.Key).ToArray(), []));
        }
        foreach (var providerGroup in sourceConfigRows.Values
            .Where(row => row.ProviderId != RssProviderInfo.ProviderId && row.Source is not null)
            .GroupBy(row => row.ProviderId, StringComparer.Ordinal))
        {
            var plugin = plugins.FirstOrDefault(item => item.PluginId == providerGroup.Key);
            var sourceConfig = providerGroup.ToArray();
            var items = sourceConfig
                .SelectMany(row => new[] { row }.Concat(authorRows.GetValueOrDefault(row.Key, [])))
                .Where(visible.Contains)
                .ToArray();
            groups.Add(new NotificationSettingsGroup(plugin, null, items,
                addTargets.Where(row => row.ProviderId == providerGroup.Key).ToArray()));
        }

        return new(rows, available, visible, groups);
    }

    private static bool SupportsAuthorNotifications(
        SourceSettingsRow source,
        SourceNotificationCapabilities? capabilities)
    {
        if (!CreatorNotificationPolicies.SupportsAuthors(source.Plugin?.Manifest))
            return false;

        // Capabilities are derived from currently enabled subscriptions and
        // disappear while a plugin is disabled or unavailable. Keep the
        // author entry point when the persisted source still declares a
        // notification-capable feed, so it can be restored after re-enabling.
        return capabilities?.HasAny == true
            || source.Source.Feeds.Any(feed => feed.SupportsNotifications
                && feed.Kind is "feed" or "rss" or "atom" or "live");
    }
}
