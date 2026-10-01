using FeedPeek.Core.Models;
using FeedPeek.Helpers;
using Microsoft.UI.Xaml.Media;

namespace FeedPeek.Models;

/// <summary>A local source/configuration or one positively queried author in the unified picker.</summary>
public sealed class NotificationCandidateRow
{
    public NotificationRuleRow? Source { get; init; }
    public CreatorDescriptor? Creator { get; init; }
    internal NotificationCreatorTarget? Target { get; init; }
    public PluginSettingsRow? Plugin { get; init; }
    public bool IsExisting { get; init; }
    public string DisplayName => Creator?.DisplayName ?? Source?.DisplayName ?? Plugin?.DisplayName ?? string.Empty;
    public string Detail => (Creator is not null
        ? LocalizationHelper.GetString("NotificationAuthor", "作者") + " · " + Target?.DisplayName
        : Source?.Detail ?? LocalizationHelper.GetString("NotificationPluginNeedsSource", "请在插件设置中登录或添加来源"))
        + (IsExisting ? " · " + LocalizationHelper.GetString("NotificationAlreadyConfigured", "已添加，查看设置") : string.Empty);
    public ImageSource? IconSource => Creator is not null ? ImageSourceCache.GetSettingsAvatar(Creator.AvatarUrl)
        : Source?.IconSource ?? Plugin?.IconSource;
}
