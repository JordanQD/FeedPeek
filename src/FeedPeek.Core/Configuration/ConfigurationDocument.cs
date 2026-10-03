using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedPeek.Core.Configuration;

/// <summary>A portable configuration export. Legacy documents are patch documents.</summary>
public sealed class ConfigurationDocument
{
    public const string CurrentFormat = "FeedPeek.Configuration";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> DocumentProperties = new(StringComparer.OrdinalIgnoreCase)
        { "Format", "Version", "ExportedAt", "Settings", "Database" };
    private static readonly HashSet<string> LegacyProperties = new(StringComparer.OrdinalIgnoreCase)
        { "ExportedAt", "AutoStartEnabled", "RunningLaunchAction", "CustomLaunchWebPageUrl",
          "DefaultOpenPage", "SaveMainPanelPosition", "MainPanelHeight", "NotificationCheckIntervalMinutes",
          "NotificationTargetMode", "LiveSectionDisplayMode", "VideoNotificationsEnabled",
          "LiveNotificationsEnabled", "CustomNotificationCreators" };
    private static readonly string[] RunningLaunchActions = ["OpenSettings", "OpenCustomWebPage"];
    private static readonly string[] DefaultOpenPages = ["Following", "History", "ViewLater"];
    private static readonly string[] NotificationTargetModes = ["None", "AllFollowing", "CustomCreators"];
    private static readonly string[] LiveSectionDisplayModes = ["Expanded", "Collapsed", "Hidden"];
    private static readonly string[] AppLanguages = ["system", "zh-CN", "zh-HK"];

    public string Format { get; set; } = CurrentFormat;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public ConfigurationSettings Settings { get; set; } = new();
    public FeedConfigurationSnapshot? Database { get; set; }

    [JsonIgnore]
    public bool IsLegacy { get; private set; }

    public static ConfigurationDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("The configuration document is empty.");

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("The configuration document must be a JSON object.");

            RejectDuplicateProperties(root);
            var isVersioned = HasProperty(root, "Format")
                || HasProperty(root, "Version")
                || HasProperty(root, "Settings")
                || HasProperty(root, "Database");
            return isVersioned ? ParseVersioned(root) : ParseLegacy(root);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("The configuration document is invalid.", ex);
        }
    }

    /// <summary>Validates this document before applying or serializing it.</summary>
    public void Validate()
    {
        if (IsLegacy)
        {
            ValidateSettings(Settings, requireComplete: false, requirePanelSourceFilter: false);
            return;
        }

        if (!string.Equals(Format, CurrentFormat, StringComparison.Ordinal))
            throw new FormatException($"Unsupported configuration format '{Format}'.");
        if (Version != CurrentVersion)
            throw new FormatException($"Unsupported configuration version '{Version}'.");
        if (ExportedAt == default)
            throw new FormatException("ExportedAt must be a non-default timestamp.");
        if (Settings is null)
            throw new FormatException("Settings is required.");
        if (Database is null)
            throw new FormatException("Database is required.");

        ValidateSettings(Settings, requireComplete: true, requirePanelSourceFilter: true);
        RejectCredentialProfiles(Database);
        SqliteFeedStore.ValidateConfiguration(Database);

        var sourceKeys = Database.Sources
            .Select(source => new SourceNotificationKey(source.ProviderId, source.SourceId))
            .ToHashSet();
        foreach (var creator in Settings.CustomNotificationCreators!)
        {
            if (!sourceKeys.Contains(new SourceNotificationKey(creator.ProviderId, creator.SourceId)))
                throw new FormatException("A creator notification entry references a missing source.");
        }
    }

    /// <summary>Serializes a complete version 1 document.</summary>
    public string ToJson()
    {
        if (IsLegacy)
            throw new InvalidOperationException("Legacy patch documents cannot be serialized as complete exports.");

        Validate();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    private static ConfigurationDocument ParseVersioned(JsonElement root)
    {
        ValidateKnownProperties(root, DocumentProperties);
        var format = RequiredString(root, "Format");
        var version = RequiredInt32(root, "Version");
        var exportedAt = RequiredDateTimeOffset(root, "ExportedAt");
        if (!string.Equals(format, CurrentFormat, StringComparison.Ordinal))
            throw new FormatException($"Unsupported configuration format '{format}'.");
        if (version != CurrentVersion)
            throw new FormatException($"Unsupported configuration version '{version}'.");

        var settingsElement = RequiredObject(root, "Settings");
        ValidateKnownProperties(settingsElement, typeof(ConfigurationSettings)
            .GetProperties()
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
        var settings = Deserialize<ConfigurationSettings>(settingsElement);
        settings.SetPanelSourceFilterPresence(HasProperty(settingsElement, "PanelSourceFilter"));
        if (HasProperty(settingsElement, "CustomNotificationCreators"))
            settings.CustomNotificationCreators = ParseCreators(GetProperty(settingsElement, "CustomNotificationCreators"), requireCompleteShape: true);

        var databaseElement = RequiredObject(root, "Database");
        var database = Deserialize<FeedConfigurationSnapshot>(databaseElement);
        var document = new ConfigurationDocument
        {
            Format = format,
            Version = version,
            ExportedAt = exportedAt,
            Settings = settings,
            Database = database,
            IsLegacy = false,
        };
        document.Validate();
        return document;
    }

    private static ConfigurationDocument ParseLegacy(JsonElement root)
    {
        ValidateKnownProperties(root, LegacyProperties);
        var realProperties = root.EnumerateObject()
            .Any(property => !property.Name.Equals("ExportedAt", StringComparison.OrdinalIgnoreCase));
        if (!realProperties)
            throw new FormatException("A legacy document must contain at least one configuration value.");

        var settings = new ConfigurationSettings
        {
            AutoStartEnabled = OptionalBool(root, "AutoStartEnabled"),
            RunningLaunchAction = OptionalEnum(root, "RunningLaunchAction", RunningLaunchActions,
                legacyOpenBilibiliAlias: true),
            CustomLaunchWebPageUrl = OptionalUrl(root, "CustomLaunchWebPageUrl"),
            DefaultOpenPage = OptionalEnum(root, "DefaultOpenPage", DefaultOpenPages),
            SaveMainPanelPosition = OptionalBool(root, "SaveMainPanelPosition"),
            MainPanelHeight = OptionalInt32(root, "MainPanelHeight"),
            NotificationCheckIntervalMinutes = OptionalInt32(root, "NotificationCheckIntervalMinutes"),
            NotificationTargetMode = OptionalEnum(root, "NotificationTargetMode", NotificationTargetModes),
            LiveSectionDisplayMode = OptionalEnum(root, "LiveSectionDisplayMode", LiveSectionDisplayModes),
            VideoNotificationsEnabled = OptionalBool(root, "VideoNotificationsEnabled"),
            LiveNotificationsEnabled = OptionalBool(root, "LiveNotificationsEnabled"),
            CustomNotificationCreators = OptionalCreators(root, "CustomNotificationCreators"),
        };
        settings.SetPanelSourceFilterPresence(false);

        var exportedAt = HasProperty(root, "ExportedAt")
            ? RequiredDateTimeOffset(root, "ExportedAt")
            : DateTimeOffset.UtcNow;
        var document = new ConfigurationDocument
        {
            ExportedAt = exportedAt,
            Settings = settings,
            Database = null,
            IsLegacy = true,
        };
        document.Validate();
        return document;
    }

    private static void ValidateSettings(ConfigurationSettings settings, bool requireComplete, bool requirePanelSourceFilter)
    {
        if (settings is null)
            throw new FormatException("Settings is required.");

        if (requireComplete)
        {
            if (settings.AutoStartEnabled is null
                || settings.RunningLaunchAction is null
                || settings.CustomLaunchWebPageUrl is null
                || settings.DefaultOpenPage is null
                || settings.NotificationTargetMode is null
                || settings.LiveSectionDisplayMode is null
                || settings.AppLanguage is null
                || settings.SaveMainPanelPosition is null
                || settings.VideoNotificationsEnabled is null
                || settings.LiveNotificationsEnabled is null
                || settings.KeepPanelSourceFilter is null
                || settings.MainPanelHeight is null
                || settings.NotificationCheckIntervalMinutes is null
                || settings.CustomNotificationCreators is null
                || settings.AutoCreatedPluginSources is null)
                throw new FormatException("Version 1 settings must contain every setting.");
            if (requirePanelSourceFilter && !settings.HasPanelSourceFilter)
                throw new FormatException("Version 1 settings must contain panelSourceFilter, including null.");
        }

        settings.RunningLaunchAction = NormalizeOptionalEnum(settings.RunningLaunchAction, RunningLaunchActions, nameof(settings.RunningLaunchAction));
        settings.DefaultOpenPage = NormalizeOptionalEnum(settings.DefaultOpenPage, DefaultOpenPages, nameof(settings.DefaultOpenPage));
        settings.NotificationTargetMode = NormalizeOptionalEnum(settings.NotificationTargetMode, NotificationTargetModes, nameof(settings.NotificationTargetMode));
        settings.LiveSectionDisplayMode = NormalizeOptionalEnum(settings.LiveSectionDisplayMode, LiveSectionDisplayModes, nameof(settings.LiveSectionDisplayMode));
        settings.AppLanguage = NormalizeOptionalEnum(settings.AppLanguage, AppLanguages, nameof(settings.AppLanguage));

        if (settings.CustomLaunchWebPageUrl is not null)
            settings.CustomLaunchWebPageUrl = NormalizeHttpUrl(settings.CustomLaunchWebPageUrl, nameof(settings.CustomLaunchWebPageUrl));
        if (settings.MainPanelHeight is not null && settings.MainPanelHeight is not (600 or 800 or 1000))
            throw new FormatException("MainPanelHeight must be 600, 800, or 1000.");
        if (settings.NotificationCheckIntervalMinutes is not null
            && settings.NotificationCheckIntervalMinutes <= 0)
            throw new FormatException("NotificationCheckIntervalMinutes must be positive.");
        if (settings.PanelSourceFilter is not null)
            settings.PanelSourceFilter = NormalizePanelSourceFilter(settings.PanelSourceFilter);

        if (settings.CustomNotificationCreators is not null)
            ValidateCreators(settings.CustomNotificationCreators);
        if (settings.AutoCreatedPluginSources is not null)
        {
            var sourceProviders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var providerId in settings.AutoCreatedPluginSources)
            {
                RequireText(providerId, "auto-created plugin source providerId");
                if (!sourceProviders.Add(providerId))
                    throw new FormatException("AutoCreatedPluginSources contains duplicate provider IDs.");
            }
        }
    }

    private static void ValidateCreators(IReadOnlyList<ConfigurationCreator> creators)
    {
        if (creators is null)
            throw new FormatException("CustomNotificationCreators cannot be null.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var creator in creators)
        {
            if (creator is null)
                throw new FormatException("CustomNotificationCreators cannot contain null entries.");
            RequireText(creator.ProviderId, "creator providerId");
            RequireText(creator.SourceId, "creator sourceId");
            RequireText(creator.CreatorKey, "creator creatorKey");
            RequireText(creator.Name, "creator name");
            if (creator.Mid < 0)
                throw new FormatException("Creator Mid cannot be negative.");
            var identity = $"{creator.ProviderId}\n{creator.SourceId}\n{creator.CreatorKey}";
            if (!identities.Add(identity))
                throw new FormatException("CustomNotificationCreators contains duplicate identities.");
        }
    }

    private static void RejectCredentialProfiles(FeedConfigurationSnapshot database)
    {
        if (database.Sources.Any(source => source.CredentialProfileId is not null)
            || database.Subscriptions.Any(subscription => subscription.CredentialProfileId is not null))
            throw new FormatException("Credential profile identifiers are local and cannot be imported.");
    }

    private static T Deserialize<T>(JsonElement element)
        => JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions)
            ?? throw new FormatException("A required configuration value was null.");

    private static string? OptionalUrl(JsonElement root, string name)
        => HasProperty(root, name) ? NormalizeHttpUrl(RequiredString(root, name), name) : null;

    private static bool? OptionalBool(JsonElement root, string name)
        => HasProperty(root, name) ? RequiredBool(root, name) : null;

    private static int? OptionalInt32(JsonElement root, string name)
        => HasProperty(root, name) ? RequiredInt32(root, name) : null;

    private static string? OptionalEnum(JsonElement root, string name, IReadOnlyList<string> allowed,
        bool legacyOpenBilibiliAlias = false)
    {
        if (!HasProperty(root, name))
            return null;
        var value = RequiredString(root, name);
        if (legacyOpenBilibiliAlias && value.Equals("OpenBilibiliWebPage", StringComparison.OrdinalIgnoreCase))
            value = "OpenCustomWebPage";
        return NormalizeEnum(value, allowed, name);
    }

    private static IReadOnlyList<ConfigurationCreator>? OptionalCreators(JsonElement root, string name)
    {
        if (!HasProperty(root, name))
            return null;
        var element = GetProperty(root, name);
        if (element.ValueKind == JsonValueKind.Null)
            throw new FormatException($"{name} cannot be null.");
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{name} must be an array.");
        return ParseCreators(element, requireCompleteShape: false);
    }

    private static IReadOnlyList<ConfigurationCreator> ParseCreators(JsonElement element, bool requireCompleteShape)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException("CustomNotificationCreators must be an array.");
        var creators = element.EnumerateArray().Select(item => ParseCreator(item, requireCompleteShape)).ToArray();
        ValidateCreators(creators);
        return creators;
    }

    private static ConfigurationCreator ParseCreator(JsonElement element, bool requireCompleteShape)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("Creator entries must be objects.");
        var allowed = new HashSet<string>(["ProviderId", "SourceId", "CreatorKey", "Name", "AvatarUrl",
            "VideoNotificationsEnabled", "LiveNotificationsEnabled", "Mid"], StringComparer.OrdinalIgnoreCase);
        ValidateKnownProperties(element, allowed);
        if (requireCompleteShape)
        {
            foreach (var property in new[] { "ProviderId", "SourceId", "CreatorKey", "Name", "AvatarUrl",
                "VideoNotificationsEnabled", "LiveNotificationsEnabled" })
            {
                if (!HasProperty(element, property))
                    throw new FormatException($"Creator property '{property}' is required.");
            }
        }
        var creatorKey = OptionalCreatorString(element, "CreatorKey") ?? string.Empty;
        var providerId = OptionalCreatorString(element, "ProviderId") ?? string.Empty;
        var sourceId = OptionalCreatorString(element, "SourceId") ?? string.Empty;
        var mid = HasProperty(element, "Mid") ? RequiredInt64(element, "Mid") : 0;
        if (string.IsNullOrWhiteSpace(creatorKey) && mid > 0)
        {
            providerId = "feedpeek.video-site";
            sourceId = "account-default";
            creatorKey = mid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ConfigurationCreator(
            providerId,
            sourceId,
            creatorKey,
            OptionalCreatorString(element, "Name") ?? string.Empty,
            OptionalCreatorString(element, "AvatarUrl") ?? string.Empty,
            HasProperty(element, "VideoNotificationsEnabled") ? RequiredBool(element, "VideoNotificationsEnabled") : true,
            HasProperty(element, "LiveNotificationsEnabled") ? RequiredBool(element, "LiveNotificationsEnabled") : true,
            mid);
    }

    private static string? OptionalCreatorString(JsonElement element, string name)
        => !HasProperty(element, name) ? null : RequiredString(element, name);

    private static string NormalizeEnum(string value, IReadOnlyList<string> allowed, string name)
    {
        var canonical = allowed.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            throw new FormatException($"{name} contains an unsupported value.");
        return canonical;
    }

    private static string? NormalizeOptionalEnum(string? value, IReadOnlyList<string> allowed, string name)
        => value is null ? null : NormalizeEnum(value, allowed, name);

    private static void ValidateEnum(string? value, IReadOnlyList<string> allowed, string name)
    {
        if (value is not null)
            _ = NormalizeEnum(value, allowed, name);
    }

    private static string NormalizeHttpUrl(string value, string name)
    {
        RequireText(value, name);
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new FormatException($"{name} must be an HTTP or HTTPS URL.");
        return uri.AbsoluteUri;
    }

    private static string NormalizePanelSourceFilter(string value)
    {
        var parts = value.Trim().Split('|');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new FormatException("PanelSourceFilter must use provider|source format.");
        return $"{parts[0].Trim()}|{parts[1].Trim()}";
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"{name} cannot be blank.");
    }

    private static bool HasProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        throw new FormatException($"Missing required property '{name}'.");
    }

    private static JsonElement RequiredObject(JsonElement root, string name)
    {
        var value = GetProperty(root, name);
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{name} must be an object.");
        return value;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = GetProperty(root, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException($"{name} must be a string.");
        return value.GetString() ?? throw new FormatException($"{name} cannot be null.");
    }

    private static bool RequiredBool(JsonElement root, string name)
    {
        var value = GetProperty(root, name);
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            throw new FormatException($"{name} must be a boolean.");
        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement root, string name)
    {
        var value = GetProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new FormatException($"{name} must be an integer.");
        return result;
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        var value = GetProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw new FormatException($"{name} must be an integer.");
        return result;
    }

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement root, string name)
    {
        var value = GetProperty(root, name);
        if (value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out var result)
            || result == default)
            throw new FormatException($"{name} must be a non-default timestamp.");
        return result;
    }

    private static void ValidateKnownProperties(JsonElement objectElement, IReadOnlySet<string> allowed)
    {
        foreach (var property in objectElement.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new FormatException($"Unknown configuration property '{property.Name}'.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new FormatException($"Duplicate configuration property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                RejectDuplicateProperties(child);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
}

public sealed class ConfigurationSettings
{
    public bool? AutoStartEnabled { get; set; }
    public string? RunningLaunchAction { get; set; }
    public string? CustomLaunchWebPageUrl { get; set; }
    public string? DefaultOpenPage { get; set; }
    public string? NotificationTargetMode { get; set; }
    public string? LiveSectionDisplayMode { get; set; }
    public string? AppLanguage { get; set; }
    public bool? SaveMainPanelPosition { get; set; }
    public bool? VideoNotificationsEnabled { get; set; }
    public bool? LiveNotificationsEnabled { get; set; }
    public bool? KeepPanelSourceFilter { get; set; }
    public int? MainPanelHeight { get; set; }
    public int? NotificationCheckIntervalMinutes { get; set; }
    public string? PanelSourceFilter { get; set; }
    public IReadOnlyList<ConfigurationCreator>? CustomNotificationCreators { get; set; }
    public IReadOnlyList<string>? AutoCreatedPluginSources { get; set; }

    [JsonIgnore]
    public bool HasPanelSourceFilter { get; private set; } = true;

    internal void SetPanelSourceFilterPresence(bool present) => HasPanelSourceFilter = present;
}

public sealed record ConfigurationCreator(
    string ProviderId,
    string SourceId,
    string CreatorKey,
    string Name,
    string AvatarUrl,
    bool VideoNotificationsEnabled,
    bool LiveNotificationsEnabled,
    long Mid = 0);
