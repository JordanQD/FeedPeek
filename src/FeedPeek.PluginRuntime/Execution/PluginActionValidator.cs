using FeedPeek.PluginContracts.Dtos;

namespace FeedPeek.PluginRuntime.Execution;

public static class PluginActionValidator
{
    public static void Validate(IReadOnlyList<ActionDescriptor> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.ActionId) || action.ActionId.Length > 100 || !ids.Add(action.ActionId))
                throw new InvalidDataException("Action ids must be non-empty, unique, and no longer than 100 characters.");
            if (string.IsNullOrWhiteSpace(action.FallbackText) || action.FallbackText.Length > 200)
                throw new InvalidDataException("An action fallbackText is required and must not exceed 200 characters.");
            if (!PluginActionRules.SupportedDangerLevels.Contains(action.DangerLevel))
                throw new InvalidDataException("An action uses an unsupported dangerLevel.");
            if (action.IconSemantic is not null && !PluginActionRules.SupportedIconSemantics.Contains(action.IconSemantic))
                throw new InvalidDataException("An action uses an unsupported iconSemantic.");
            if (action.Payload is { Count: > 32 }
                || action.Payload?.Any(pair => pair.Key.Length > 100 || pair.Value.Length > 4096) == true)
            {
                throw new InvalidDataException("An action payload exceeds the protocol limits.");
            }
        }
    }
}
