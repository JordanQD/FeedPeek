namespace FeedPeek.Models;

/// <summary>A footer action retains its full source identity; names only disambiguate multiple sources.</summary>
public sealed record NotificationFooterRow(NotificationRuleRow Rule, string? Description);
