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
        DefaultIconUri = entry.IconUri;
        IconUri = Services.AppSettings.GetPluginIcon(entry.PluginId) ?? entry.IconUri;
    }

    public string PluginId { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public bool IsEnabled { get; }

    public PluginManifest? Manifest { get; }

    public string? ErrorMessage { get; }

    /// <summary>The plugin package's own icon, or null when it does not ship one.</summary>
    public string? DefaultIconUri { get; }

    /// <summary>The icon the user chose for this plugin, falling back to the package icon.</summary>
    public string? IconUri { get; }
}
