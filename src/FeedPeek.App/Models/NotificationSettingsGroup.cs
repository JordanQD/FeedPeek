using FeedPeek.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Linq;

namespace FeedPeek.Models;

public sealed class NotificationSettingsGroup
{
    public NotificationSettingsGroup(
        PluginSettingsRow? plugin,
        NotificationRuleRow? source,
        IReadOnlyList<NotificationRuleRow> items,
        IReadOnlyList<NotificationRuleRow> addTargets)
    {
        Plugin = plugin;
        Source = source;
        Items = items;
        AddTargets = addTargets;
    }

    public PluginSettingsRow? Plugin { get; }
    /// <summary>
    /// The direct source card shown for RSS/Atom groups. Keeping this as a
    /// notification row lets the existing detail navigation receive the same
    /// data as a row inside a plugin group.
    /// </summary>
    public NotificationRuleRow? Source { get; }
    public IReadOnlyList<NotificationRuleRow> Items { get; }
    public IReadOnlyList<NotificationRuleRow> AddTargets { get; }

    // Source-level author policies are configuration actions, not notification entries.
    public IReadOnlyList<NotificationRuleRow> NotificationItems => Items
        .Where(item => item.Creator is not null || item.Policy is null).ToArray();
    public IReadOnlyList<NotificationFooterRow> SettingsTargets => FooterRows(Items
        .Where(item => item.Creator is null && item.Policy is not null));
    public IReadOnlyList<NotificationFooterRow> FooterAddTargets => FooterRows(AddTargets);

    private IReadOnlyList<NotificationFooterRow> FooterRows(IEnumerable<NotificationRuleRow> rows)
    {
        var multipleSources = Items.Count(item => item.Creator is null) > 1;
        return rows.Select(row => new NotificationFooterRow(row,
            multipleSources ? row.DisplayName : null)).ToArray();
    }


    public string DisplayName
    {
        get
        {
            var first = Items.FirstOrDefault();
            return Plugin?.DisplayName
                ?? Source?.DisplayName
                ?? first?.Source?.Plugin?.DisplayName
                ?? first?.ProviderId
                ?? string.Empty;
        }
    }

    public string Detail
    {
        get
        {
            if (Source is not null)
                return Source.Detail;

            // Each room/source is one followed target. An author policy is a
            // configuration entry, so count its saved authors instead of the policy itself.
            // AllFollowing may hide personal cards; retain the saved selection count.
            var count = Items.Where(item => item.Creator is null)
                .GroupBy(item => item.Key)
                .Sum(group => (group.First().Policy is null ? 1 : 0) + group.First().SelectedCreatorCount);
            return LocalizationHelper.Format("NotificationPluginGroupCount", count);
        }
    }

    public ImageSource? IconSource => Plugin?.IconSource ?? Source?.IconSource ?? Items.FirstOrDefault()?.IconSource;

    /// <summary>All groups without a direct source card are plugin groups, including orphaned sources.</summary>
    public bool IsPluginGroup => Source is null;
    public Visibility SourceVisibility => Source is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PluginVisibility => IsPluginGroup ? Visibility.Visible : Visibility.Collapsed;
}
