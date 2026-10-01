using FeedPeek.Core.Models;

namespace FeedPeek.Core.Notifications;

/// <summary>
/// Converts the old source/creator rules into creator policies without widening
/// either notification kind. It is deliberately keyed only by the complete
/// provider/source identity; site-specific IDs are not interpreted here.
/// </summary>
public static class CreatorNotificationPolicyMigration
{
    public static IReadOnlyList<CreatorNotificationPolicy> FromLegacy(
        IEnumerable<SourceNotificationRule> sourceRules,
        IEnumerable<CreatorNotificationRule> creatorRules)
    {
        ArgumentNullException.ThrowIfNull(sourceRules);
        ArgumentNullException.ThrowIfNull(creatorRules);

        var sources = sourceRules
            .GroupBy(rule => rule.Key)
            .ToDictionary(group => group.Key, group => group.Last());
        var creators = creatorRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ProviderId)
                && !string.IsNullOrWhiteSpace(rule.SourceId)
                && !string.IsNullOrWhiteSpace(rule.CreatorKey))
            .GroupBy(rule => new SourceNotificationKey(rule.ProviderId, rule.SourceId))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var keys = sources.Keys.Concat(creators.Keys).Distinct().ToArray();
        return keys.Select(key => Create(key,
            sources.TryGetValue(key, out var source) ? source : null,
            creators.TryGetValue(key, out var rules) ? rules : []))
            .ToArray();
    }

    public static CreatorNotificationPolicy FromLegacy(
        SourceNotificationKey key,
        SourceNotificationRule? sourceRule,
        IEnumerable<CreatorNotificationRule> creatorRules)
    {
        ArgumentNullException.ThrowIfNull(creatorRules);
        if (sourceRule is not null && sourceRule.Key != key)
            sourceRule = null;
        var rules = creatorRules
            .Where(rule => rule.ProviderId == key.ProviderId && rule.SourceId == key.SourceId)
            .ToArray();
        return Create(key, sourceRule, rules);
    }

    private static CreatorNotificationPolicy Create(
        SourceNotificationKey key,
        SourceNotificationRule? source,
        IReadOnlyList<CreatorNotificationRule> creators)
    {
        var updates = GetKind(source?.UpdateNotificationsEnabled, creators.Any(rule => rule.UpdatesEnabled));
        var live = GetKind(source?.LiveNotificationsEnabled, creators.Any(rule => rule.LiveEnabled));
        return new(key.ProviderId, key.SourceId,
            updates.Scope, live.Scope, updates.Enabled, live.Enabled);
    }

    private static (CreatorNotificationScope Scope, bool Enabled) GetKind(
        bool? sourceEnabled, bool hasEnabledCreator)
    {
        if (sourceEnabled == true)
            return (CreatorNotificationScope.AllFollowing, true);

        // A creator rule enabled for this kind preserves the old creator-only
        // range even when a source rule exists but had this kind disabled.
        if (hasEnabledCreator)
            return (CreatorNotificationScope.SelectedCreators, true);

        // A source rule, even when its switch is off, owns the source-wide
        // range. Retain that closed state instead of silently changing it to a
        // selected range. Only a creator-only legacy configuration is selected.
        if (sourceEnabled.HasValue)
            return (CreatorNotificationScope.AllFollowing, false);

        return (CreatorNotificationScope.SelectedCreators, false);
    }
}
