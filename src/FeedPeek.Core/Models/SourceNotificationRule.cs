namespace FeedPeek.Core.Models;

/// <summary>A source rule is independent of any creator within that source.</summary>
public readonly record struct SourceNotificationKey(string ProviderId, string SourceId);

public sealed record SourceNotificationRule(
    string ProviderId,
    string SourceId,
    IReadOnlyList<string> UpdateFeedIds,
    IReadOnlyList<string> LiveFeedIds,
    bool UpdateNotificationsEnabled,
    bool LiveNotificationsEnabled,
    bool IsAutomatic = false)
{
    public SourceNotificationKey Key => new(ProviderId, SourceId);
}
