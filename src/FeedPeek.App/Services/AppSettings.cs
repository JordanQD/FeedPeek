using FeedPeek.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace FeedPeek.Services;

public static class AppSettings
{
    public const string SystemLanguage = "system";

    private const int DefaultNotificationCheckIntervalMinutes = 15;
    private const string VideoNotificationsEnabledKey = "VideoNotificationsEnabled";
    private const string LiveNotificationsEnabledKey = "LiveNotificationsEnabled";
    private const string NotificationCheckIntervalMinutesKey = "NotificationCheckIntervalMinutes";
    private const string KnownVideoUpdateIdsKey = "KnownVideoUpdateIds";
    private const string KnownLiveRoomIdsKey = "KnownLiveRoomIds";
    private const string AutoCreatedPluginSourcesKey = "AutoCreatedPluginSources";
    private const string VideoNotificationBaselineInitializedKey = "VideoNotificationBaselineInitialized";
    private const string LiveNotificationBaselineInitializedKey = "LiveNotificationBaselineInitialized";
    private const string NotificationTargetModeKey = "NotificationTargetMode";
    private const string RunningLaunchActionKey = "RunningLaunchAction";
    private const string CustomLaunchWebPageUrlKey = "CustomLaunchWebPageUrl";
    private const string DefaultOpenPageKey = "DefaultOpenPage";
    private const string SaveMainPanelPositionKey = "SaveMainPanelPosition";
    private const string LiveSectionDisplayModeKey = "LiveSectionDisplayMode";
    private const string MainPanelHeightKey = "MainPanelHeight";
    private const string CustomNotificationCreatorsKey = "CustomNotificationCreators";
    private const string AppLanguageKey = "AppLanguage";
    private const string PluginIconKeyPrefix = "PluginIcon.";
    private const string PluginWebsiteKeyPrefix = "PluginWebsite.";
    private const string PanelSourceFilterKey = "PanelSourceFilter";
    private const string KeepPanelSourceFilterKey = "KeepPanelSourceFilter";
    private const string DefaultCustomLaunchWebPageUrl = "https://github.com/JordanQD/FeedPeek";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ApplicationDataContainer LocalSettings => ApplicationData.Current.LocalSettings;

    public static event EventHandler? NotificationSettingsChanged;

    /// <summary>Raised when a source/plugin "open website" URL is saved from any surface.</summary>
    public static event EventHandler? WebsiteSettingsChanged;

    public static bool VideoNotificationsEnabled
    {
        get => ReadBool(VideoNotificationsEnabledKey, true);
        set
        {
            if (VideoNotificationsEnabled == value)
            {
                return;
            }

            LocalSettings.Values[VideoNotificationsEnabledKey] = value;
            NotificationSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool LiveNotificationsEnabled
    {
        get => ReadBool(LiveNotificationsEnabledKey, true);
        set
        {
            if (LiveNotificationsEnabled == value)
            {
                return;
            }

            LocalSettings.Values[LiveNotificationsEnabledKey] = value;
            NotificationSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static int NotificationCheckIntervalMinutes
    {
        get => ReadInt(NotificationCheckIntervalMinutesKey, DefaultNotificationCheckIntervalMinutes);
        set
        {
            var normalizedValue = Math.Max(1, value);
            if (NotificationCheckIntervalMinutes == normalizedValue)
            {
                return;
            }

            LocalSettings.Values[NotificationCheckIntervalMinutesKey] = normalizedValue;
            NotificationSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static NotificationTargetMode NotificationTargetMode
    {
        get
        {
            var value = ReadRawInt(NotificationTargetModeKey, (int)NotificationTargetMode.None);
            return Enum.IsDefined(typeof(NotificationTargetMode), value)
                ? (NotificationTargetMode)value
                : NotificationTargetMode.AllFollowing;
        }
        set
        {
            if (NotificationTargetMode == value)
            {
                return;
            }

            LocalSettings.Values[NotificationTargetModeKey] = (int)value;
            NotificationSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static RunningLaunchAction RunningLaunchAction
    {
        get
        {
            var value = ReadRawInt(RunningLaunchActionKey, (int)RunningLaunchAction.OpenSettings);
            return Enum.IsDefined(typeof(RunningLaunchAction), value)
                ? (RunningLaunchAction)value
                : RunningLaunchAction.OpenSettings;
        }
        set
        {
            if (RunningLaunchAction == value)
            {
                return;
            }

            LocalSettings.Values[RunningLaunchActionKey] = (int)value;
        }
    }

    public static string CustomLaunchWebPageUrl
    {
        get => ReadString(CustomLaunchWebPageUrlKey, DefaultCustomLaunchWebPageUrl);
        set => LocalSettings.Values[CustomLaunchWebPageUrlKey] = NormalizeWebPageUrl(value);
    }

    public static DefaultOpenPage DefaultOpenPage
    {
        get
        {
            var value = ReadRawInt(DefaultOpenPageKey, (int)DefaultOpenPage.Following);
            return Enum.IsDefined(typeof(DefaultOpenPage), value)
                ? (DefaultOpenPage)value
                : DefaultOpenPage.Following;
        }
        set => LocalSettings.Values[DefaultOpenPageKey] = (int)value;
    }

    public static bool SaveMainPanelPosition
    {
        get => ReadBool(SaveMainPanelPositionKey, false);
        set => LocalSettings.Values[SaveMainPanelPositionKey] = value;
    }

    /// <summary>
    /// Persisted panel source filter as <c>providerId|sourceId</c>, or null for "all sources".
    /// Ignored while <see cref="KeepPanelSourceFilter"/> is off.
    /// </summary>
    public static string? PanelSourceFilter
    {
        get
        {
            if (!KeepPanelSourceFilter)
            {
                return null;
            }

            var raw = LocalSettings.Values[PanelSourceFilterKey] as string;
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                LocalSettings.Values.Remove(PanelSourceFilterKey);
                return;
            }

            LocalSettings.Values[PanelSourceFilterKey] = value.Trim();
        }
    }

    /// <summary>
    /// When off, the panel always starts with "all sources" and does not remember the last filter.
    /// </summary>
    public static bool KeepPanelSourceFilter
    {
        get => ReadBool(KeepPanelSourceFilterKey, true);
        set
        {
            LocalSettings.Values[KeepPanelSourceFilterKey] = value;
            if (!value)
            {
                LocalSettings.Values.Remove(PanelSourceFilterKey);
            }
        }
    }

    public static LiveSectionDisplayMode LiveSectionDisplayMode
    {
        get
        {
            var value = ReadRawInt(LiveSectionDisplayModeKey, (int)LiveSectionDisplayMode.Expanded);
            return Enum.IsDefined(typeof(LiveSectionDisplayMode), value)
                ? (LiveSectionDisplayMode)value
                : LiveSectionDisplayMode.Expanded;
        }
        set => LocalSettings.Values[LiveSectionDisplayModeKey] = (int)value;
    }

    public static int MainPanelHeight
    {
        get
        {
            var value = ReadRawInt(MainPanelHeightKey, 800);
            return value switch
            {
                600 or 800 or 1000 => value,
                _ => 800,
            };
        }
        set => LocalSettings.Values[MainPanelHeightKey] = value switch
        {
            600 or 800 or 1000 => value,
            _ => 800,
        };
    }

    public static bool VideoNotificationBaselineInitialized
    {
        get => ReadBool(VideoNotificationBaselineInitializedKey, false);
        set => LocalSettings.Values[VideoNotificationBaselineInitializedKey] = value;
    }

    public static bool LiveNotificationBaselineInitialized
    {
        get => ReadBool(LiveNotificationBaselineInitializedKey, false);
        set => LocalSettings.Values[LiveNotificationBaselineInitializedKey] = value;
    }

    public static IReadOnlyList<string> KnownVideoUpdateIds
    {
        get => ReadStringList(KnownVideoUpdateIdsKey);
        set => WriteStringList(KnownVideoUpdateIdsKey, value);
    }

    public static IReadOnlyList<string> KnownLiveRoomIds
    {
        get => ReadStringList(KnownLiveRoomIdsKey);
        set => WriteStringList(KnownLiveRoomIdsKey, value);
    }

    /// <summary>
    /// Plugins whose default source FeedPeek created automatically. A plugin is only ever auto-created
    /// once, so deleting the source stays deleted instead of reappearing on the next launch.
    /// </summary>
    public static bool HasAutoCreatedPluginSource(string pluginId)
        => ReadStringList(AutoCreatedPluginSourcesKey).Contains(pluginId, StringComparer.Ordinal);

    public static void MarkAutoCreatedPluginSource(string pluginId)
    {
        var ids = ReadStringList(AutoCreatedPluginSourcesKey);
        if (ids.Contains(pluginId, StringComparer.Ordinal))
        {
            return;
        }

        WriteStringList(AutoCreatedPluginSourcesKey, [.. ids, pluginId]);
    }

    public static void ClearAutoCreatedPluginSource(string pluginId)
    {
        var ids = ReadStringList(AutoCreatedPluginSourcesKey);
        if (!ids.Contains(pluginId, StringComparer.Ordinal))
        {
            return;
        }

        WriteStringList(
            AutoCreatedPluginSourcesKey,
            ids.Where(item => !string.Equals(item, pluginId, StringComparison.Ordinal)).ToArray());
    }

    /// <summary>
    /// Icon the user picked for an installed plugin, or null while the plugin package's own icon
    /// (manifest <c>icon</c>) is used.
    /// </summary>
    public static string? GetPluginIcon(string pluginId)
        => LocalSettings.Values[PluginIconKeyPrefix + pluginId] as string;

    /// <summary>Website opened by the panel's "open source site" action for this plugin.</summary>
    public static string? GetPluginWebsite(string pluginId)
        => LocalSettings.Values[PluginWebsiteKeyPrefix + pluginId] as string;

    public static void SetPluginWebsite(string pluginId, string? website)
    {
        if (string.IsNullOrWhiteSpace(website))
        {
            LocalSettings.Values.Remove(PluginWebsiteKeyPrefix + pluginId);
        }
        else
        {
            LocalSettings.Values[PluginWebsiteKeyPrefix + pluginId] = website.Trim();
        }

        WebsiteSettingsChanged?.Invoke(null, EventArgs.Empty);
        SourceDirectory.Refresh();
    }

    public static void NotifyWebsiteSettingsChanged()
    {
        WebsiteSettingsChanged?.Invoke(null, EventArgs.Empty);
        SourceDirectory.Refresh();
    }

    public static void SetPluginIcon(string pluginId, string? iconUri)
    {
        if (string.IsNullOrWhiteSpace(iconUri))
        {
            LocalSettings.Values.Remove(PluginIconKeyPrefix + pluginId);
        }
        else
        {
            LocalSettings.Values[PluginIconKeyPrefix + pluginId] = iconUri;
        }

        SourceDirectory.Refresh();
    }

    public static IReadOnlyList<NotificationCreatorSubscription> CustomNotificationCreators
    {
        get => ReadCustomNotificationCreators();
        set
        {
            var creators = value
                .Select(NormalizeCreatorSubscription)
                .Where(IsValidCreatorSubscription)
                .GroupBy(item => item.IdentityKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            LocalSettings.Values[CustomNotificationCreatorsKey] = JsonSerializer.Serialize(creators, JsonOptions);
            NotificationSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static string AppLanguage
    {
        get => ReadString(AppLanguageKey, SystemLanguage);
        set => LocalSettings.Values[AppLanguageKey] = value;
    }

    private static bool ReadBool(string key, bool defaultValue)
    {
        return LocalSettings.Values.TryGetValue(key, out var value) && value is bool boolValue
            ? boolValue
            : defaultValue;
    }

    private static int ReadInt(string key, int defaultValue)
    {
        if (!LocalSettings.Values.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return value switch
        {
            int intValue => Math.Max(1, intValue),
            long longValue => (int)Math.Max(1, longValue),
            double doubleValue => Math.Max(1, (int)Math.Round(doubleValue)),
            _ => defaultValue,
        };
    }

    private static int ReadRawInt(string key, int defaultValue)
    {
        if (!LocalSettings.Values.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)Math.Round(doubleValue),
            _ => defaultValue,
        };
    }

    private static string ReadString(string key, string defaultValue)
    {
        return LocalSettings.Values.TryGetValue(key, out var value)
            && value is string text
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : defaultValue;
    }

    private static string NormalizeWebPageUrl(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? DefaultCustomLaunchWebPageUrl
            : value.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = $"https://{text}";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri
                : DefaultCustomLaunchWebPageUrl;
    }

    private static IReadOnlyList<string> ReadStringList(string key)
    {
        if (!LocalSettings.Values.TryGetValue(key, out var value)
            || value is not string text
            || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<string>>(text, JsonOptions)?
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? [];
            }
        }
        catch (JsonException)
        {
            // Fall through to the legacy newline format. The next write
            // migrates it to JSON, which can preserve composite feed keys.
        }

        return text
            .Split('\n')
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteStringList(string key, IReadOnlyList<string> value)
        => LocalSettings.Values[key] = JsonSerializer.Serialize(
            value
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            JsonOptions);

    private static IReadOnlyList<NotificationCreatorSubscription> ReadCustomNotificationCreators()
    {
        if (!LocalSettings.Values.TryGetValue(CustomNotificationCreatorsKey, out var value)
            || value is not string text
            || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<NotificationCreatorSubscription>>(text, JsonOptions)?
                .Select(NormalizeCreatorSubscription)
                .Where(IsValidCreatorSubscription)
                .GroupBy(item => item.IdentityKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static NotificationCreatorSubscription NormalizeCreatorSubscription(
        NotificationCreatorSubscription item)
    {
        if (string.IsNullOrWhiteSpace(item.CreatorKey) && item.Mid > 0)
        {
            item.ProviderId = LegacyVideoSiteCompatibility.PluginId;
            item.SourceId = LegacyVideoSiteCompatibility.SourceId;
            item.CreatorKey = item.Mid.ToString();
        }

        item.ProviderId = item.ProviderId?.Trim() ?? string.Empty;
        item.SourceId = item.SourceId?.Trim() ?? string.Empty;
        item.CreatorKey = item.CreatorKey?.Trim() ?? string.Empty;
        item.Name = item.Name?.Trim() ?? string.Empty;
        return item;
    }

    private static bool IsValidCreatorSubscription(NotificationCreatorSubscription item)
        => !string.IsNullOrWhiteSpace(item.ProviderId)
            && !string.IsNullOrWhiteSpace(item.SourceId)
            && !string.IsNullOrWhiteSpace(item.CreatorKey);
}

public enum NotificationTargetMode
{
    None = 0,
    AllFollowing = 1,
    CustomCreators = 2,
}

public enum RunningLaunchAction
{
    OpenSettings = 0,
    OpenCustomWebPage = 1,
}

public enum DefaultOpenPage
{
    Following = 0,
    History = 1,
    ViewLater = 2,
}

public enum LiveSectionDisplayMode
{
    Expanded = 0,
    Collapsed = 1,
    Hidden = 2,
}
