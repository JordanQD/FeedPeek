using FeedPeek.Core.Models;
using FeedPeek.Helpers;
using Microsoft.UI.Xaml.Media;
using System.Linq;

namespace FeedPeek.Models;

/// <summary>A display snapshot; detail pages reload rules by identity before editing.</summary>
public sealed class NotificationRuleRow
{
    public NotificationRuleRow(SourceSettingsRow? source, SourceNotificationCapabilities? capabilities,
        SourceNotificationRule? rule, NotificationCreatorSubscription? creator,
        CreatorNotificationPolicy? policy = null, int selectedCreatorCount = 0,
        CreatorFollowState? followState = null, bool isPersisted = true)
    {
        Source = source;
        Capabilities = capabilities;
        Rule = rule;
        Creator = creator;
        Policy = policy;
        SelectedCreatorCount = selectedCreatorCount;
        FollowState = followState;
        IsPersisted = isPersisted;
    }

    public SourceSettingsRow? Source { get; }
    public SourceNotificationCapabilities? Capabilities { get; }
    public SourceNotificationRule? Rule { get; }
    public NotificationCreatorSubscription? Creator { get; }
    public CreatorNotificationPolicy? Policy { get; }
    public int SelectedCreatorCount { get; }
    public CreatorFollowState? FollowState { get; }
    public bool IsPersisted { get; }
    public string ProviderId => Rule?.ProviderId ?? Creator?.ProviderId ?? Policy?.ProviderId ?? Source?.Source.ProviderId ?? string.Empty;
    public string SourceId => Rule?.SourceId ?? Creator?.SourceId ?? Policy?.SourceId ?? Source?.Source.SourceId ?? string.Empty;
    public SourceNotificationKey Key => new(ProviderId, SourceId);
    public string DisplayName => Creator?.Name ?? Source?.DisplayName ?? SourceId;
    public ImageSource? IconSource => Creator?.AvatarImage ?? Source?.IconSource;
    public string Initials => IconSource is null
        ? new string(DisplayName.Trim().Take(2).Select(char.ToUpperInvariant).ToArray()) : string.Empty;
    public bool SupportsUpdates => Capabilities?.SupportsUpdates == true;
    public bool SupportsLive => Capabilities?.SupportsLive == true;
    public bool UpdatesEnabled => Creator is not null
        ? Creator.VideoNotificationsEnabled
        : Policy?.UpdatesEnabled ?? Rule?.UpdateNotificationsEnabled == true;
    public bool LiveEnabled => Creator is not null
        ? Creator.LiveNotificationsEnabled
        : Policy?.LiveEnabled ?? Rule?.LiveNotificationsEnabled == true;
    public bool IsAvailable => Source is not null && Capabilities?.HasAny == true;

    /// <summary>
    /// Whether this policy still has a selected-author type to show in the
    /// main list. When capabilities are unavailable, use the saved scopes so
    /// a disabled or temporarily unavailable source does not hide its policy.
    /// </summary>
    public bool HasSelectedCreatorScope
    {
        get
        {
            if (Policy is null)
                return false;
            if (Capabilities?.HasAny == true)
            {
                return (SupportsUpdates && Policy.UpdatesScope == CreatorNotificationScope.SelectedCreators)
                    || (SupportsLive && Policy.LiveScope == CreatorNotificationScope.SelectedCreators);
            }

            return Policy.UpdatesScope == CreatorNotificationScope.SelectedCreators
                || Policy.LiveScope == CreatorNotificationScope.SelectedCreators;
        }
    }

    public string KindText
    {
        get
        {
            if (Policy is not null && Creator is null)
                return L("NotificationCreatorPolicy", "作者") + " · " + (Source?.Plugin?.DisplayName ?? ProviderId);
            return Creator is null ? Source?.Detail ?? L("NotificationSourceMissing", "来源已删除")
                : L("NotificationAuthor", "作者") + " · " + (Source?.Plugin?.DisplayName ?? ProviderId);
        }
    }
    public string Detail => Describe(KindText);

    /// <summary>Grouped rows only show notification state; their parent identifies the source.</summary>
    public string GroupDetail => Describe(string.Empty);

    private string Describe(string kindText)
    {
        var prefix = string.IsNullOrWhiteSpace(kindText) ? string.Empty : kindText + " · ";
        if (Creator is not null && FollowState is CreatorFollowState.NotFollowed)
            return prefix + L("NotificationCreatorNotFollowed", "已取消关注 / 暂不可用");
        if (Creator is not null && FollowState is CreatorFollowState.Unknown)
            return prefix + L("NotificationCreatorRelationUnknown", "关系待确认");
        if (Policy is not null && Creator is null)
        {
            // A missing/disabled plugin may temporarily hide capabilities;
            // keep displaying the persisted policy scopes in that state.
            var showBothKinds = Capabilities?.HasAny != true;
            var policyStates = new[]
            {
                (showBothKinds || SupportsUpdates)
                    ? ScopeDetail(Policy.UpdatesScope, UpdatesEnabled, "NotificationCreatorUpdates", "更新") : null,
                (showBothKinds || SupportsLive)
                    ? ScopeDetail(Policy.LiveScope, LiveEnabled, "NotificationCreatorLive", "开播") : null,
            };
            var detail = string.Join(" / ", policyStates.Where(value => value is not null));
            return string.IsNullOrWhiteSpace(detail)
                ? prefix + L("NotificationUnavailable", "暂不可用")
                : prefix + detail;
        }
        if (!IsAvailable)
            return prefix + L("NotificationUnavailable", "暂不可用");
        var states = new[]
        {
            SupportsUpdates ? L("NotificationUpdates", "更新通知") + " " + L(UpdatesEnabled ? "On" : "Off", UpdatesEnabled ? "开" : "关") : null,
            SupportsLive ? L("NotificationLive", "开播通知") + " " + L(LiveEnabled ? "On" : "Off", LiveEnabled ? "开" : "关") : null,
        };
        return prefix + string.Join(" / ", states.Where(value => value is not null));
    }

    private string ScopeDetail(CreatorNotificationScope scope, bool enabled, string key, string fallback)
    {
        var scopeText = scope == CreatorNotificationScope.AllFollowing
            ? L("NotificationAllFollowingCreators", "全部已关注作者")
            : L("NotificationSelectedCreators", "自选作者") + $" · {SelectedCreatorCount} 人";
        return L(key, fallback) + " " + scopeText + " " + L(enabled ? "On" : "Off", enabled ? "开" : "关");
    }

    private static string L(string key, string fallback) => LocalizationHelper.GetString(key, fallback);
}
