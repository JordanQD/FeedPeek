namespace FeedPeek.PluginContracts.Dtos;

public sealed record ActionDescriptor(
    string ActionId,
    string? LocalizationKey,
    string FallbackText,
    string? IconSemantic,
    string DangerLevel,
    bool RequiresConfirmation,
    bool IsRepeatable,
    IReadOnlyDictionary<string, string>? Payload);
