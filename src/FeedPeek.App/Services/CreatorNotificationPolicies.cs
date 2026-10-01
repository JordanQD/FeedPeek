using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using FeedPeek.Core.Persistence;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Services;

/// <summary>Host eligibility and one-way migration; never creates a policy merely for installing a plugin.</summary>
internal static class CreatorNotificationPolicies
{
    public static bool SupportsAuthors(PluginManifest? manifest) => manifest is not null
        && PluginManifestRules.ResolveRuntime(manifest) != PluginManifestRules.LiveParseRuntime
        && manifest.Capabilities.Any(capability => capability is "creator.following" or "creator.resolve");

    public static bool IsEligible(SourceDefinition source, IEnumerable<FeedSubscription> subscriptions, PluginManifest? manifest)
        => source.ProviderId != RssProviderInfo.ProviderId && SupportsAuthors(manifest)
            && SourceNotificationCapabilities.Resolve(source, subscriptions,
                manifest!.Capabilities.Contains("feed.updates", StringComparer.Ordinal),
                manifest.Capabilities.Contains("feed.live", StringComparer.Ordinal)).HasAny;

    public static async Task<IReadOnlyList<CreatorNotificationPolicy>> MigrateAsync(IFeedStore store,
        IReadOnlyList<SourceDefinition> sources, IReadOnlyList<FeedSubscription> subscriptions,
        Func<string, PluginManifest?> getManifest, IReadOnlyList<CreatorNotificationRule> creators, CancellationToken token)
    {
        var sourceRules = await store.GetSourceNotificationRulesAsync(token).ConfigureAwait(false);
        var candidates = new List<CreatorNotificationPolicy>();
        foreach (var source in sources.Where(source => IsEligible(source, subscriptions, getManifest(source.ProviderId))))
        {
            var key = new SourceNotificationKey(source.ProviderId, source.SourceId);
            var legacyRule = sourceRules.FirstOrDefault(rule => rule.Key == key);
            var legacyCreators = creators.Where(rule => rule.ProviderId == key.ProviderId && rule.SourceId == key.SourceId).ToArray();
            if (legacyRule is null && legacyCreators.Length == 0) continue;
            candidates.Add(CreatorNotificationPolicyMigration.FromLegacy(key, legacyRule, legacyCreators));
        }
        if (candidates.Count > 0)
            await store.MigrateCreatorNotificationPoliciesAsync(candidates, token).ConfigureAwait(false);
        return await store.GetCreatorNotificationPoliciesAsync(token).ConfigureAwait(false);
    }

    public static IReadOnlyList<CreatorNotificationRule> SavedCreators() => AppSettings.CustomNotificationCreators
        .Select(rule => new CreatorNotificationRule(rule.ProviderId, rule.SourceId, rule.CreatorKey,
            rule.VideoNotificationsEnabled, rule.LiveNotificationsEnabled)).ToArray();
}
