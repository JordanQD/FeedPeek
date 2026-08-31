using FeedPeek.PluginContracts.Manifest;
using System;
using System.Linq;

namespace FeedPeek.Models;

public sealed class PluginSettingsRow
{
    internal PluginSettingsRow(Services.PluginManagementEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PluginId = entry.PluginId;
        DisplayName = entry.DisplayName;
        Version = entry.Version;
        IsEnabled = entry.IsEnabled;
        Manifest = entry.Manifest;
        ErrorMessage = entry.ErrorMessage;
    }

    public string PluginId { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public bool IsEnabled { get; }

    public PluginManifest? Manifest { get; }

    public string? ErrorMessage { get; }

    public string Detail => ErrorMessage is not null
        ? ErrorMessage
        : $"版本 {Version} · {(IsEnabled ? "已启用" : "已停用")}";

    public bool CanConfigureSource => IsEnabled
        && Manifest?.Capabilities.Contains("source.configuration", StringComparer.Ordinal) == true;

    public bool HasLogin => IsEnabled && Manifest?.Auth?.LoginFlow is not null;

    public string ToggleLabel => IsEnabled ? "停用" : "启用";
}
