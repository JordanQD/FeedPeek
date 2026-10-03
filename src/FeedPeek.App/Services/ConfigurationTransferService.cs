using FeedPeek.Core.Configuration;
using FeedPeek.Core.Feeds;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;
using FeedPeek.Core.Notifications;
using FeedPeek.Models;
using FeedPeek.PluginContracts.Manifest;
using FeedPeek.Rss;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedPeek.Services;

internal static class ConfigurationTransferService
{
    private static SqliteFeedStore OpenStore() => new(Path.Combine(
        ApplicationData.Current.LocalFolder.Path, "FeedPeek", "feedpeek.db"));

    public static async Task<ConfigurationDocument> ExportAsync(bool autoStartEnabled)
    {
        using var lease = await ConfigurationMutationGate.EnterAsync();
        await using var store = OpenStore();
        var database = await store.ExportConfigurationAsync();
        var keys = database.Sources.Select(source => new SourceNotificationKey(source.ProviderId, source.SourceId)).ToHashSet();
        return new ConfigurationDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Database = database,
            Settings = new ConfigurationSettings
            {
                AutoStartEnabled = autoStartEnabled,
                RunningLaunchAction = AppSettings.RunningLaunchAction.ToString(),
                CustomLaunchWebPageUrl = AppSettings.CustomLaunchWebPageUrl,
                DefaultOpenPage = AppSettings.DefaultOpenPage.ToString(),
                SaveMainPanelPosition = AppSettings.SaveMainPanelPosition,
                MainPanelHeight = AppSettings.MainPanelHeight,
                NotificationCheckIntervalMinutes = AppSettings.NotificationCheckIntervalMinutes,
                NotificationTargetMode = AppSettings.NotificationTargetMode.ToString(),
                LiveSectionDisplayMode = AppSettings.LiveSectionDisplayMode.ToString(),
                VideoNotificationsEnabled = AppSettings.VideoNotificationsEnabled,
                LiveNotificationsEnabled = AppSettings.LiveNotificationsEnabled,
                KeepPanelSourceFilter = AppSettings.KeepPanelSourceFilter,
                PanelSourceFilter = AppSettings.PanelSourceFilter,
                AppLanguage = AppSettings.AppLanguage,
                AutoCreatedPluginSources = AppSettings.AutoCreatedPluginSources,
                CustomNotificationCreators = AppSettings.CustomNotificationCreators
                    .Where(item => keys.Contains(new(item.ProviderId, item.SourceId)))
                    .Select(item => new ConfigurationCreator(item.ProviderId, item.SourceId, item.CreatorKey,
                        item.Name, item.AvatarUrl, item.VideoNotificationsEnabled, item.LiveNotificationsEnabled)).ToArray(),
            },
        };
    }

    public static async Task ImportAsync(ConfigurationDocument document)
    {
        // Parsing and cross-reference validation must finish before touching either storage system.
        document.Validate();
        using var lease = await ConfigurationMutationGate.EnterAsync();
        await using var store = OpenStore();
        var database = document.Database;
        if (document.IsLegacy && (document.Settings.NotificationTargetMode
            ?? AppSettings.NotificationTargetMode.ToString()) == nameof(NotificationTargetMode.CustomCreators))
            database = await PrepareLegacyNotificationConfigurationAsync(store, document);
        var values = ApplicationData.Current.LocalSettings.Values;
        var previous = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        NotificationStateChanges.Pulse();
        try
        {
            if (database is not null)
            {
                await store.ImportConfigurationAsync(database, () => ApplySettings(document));
                foreach (var source in database.Sources)
                {
                    NotificationStateChanges.MarkSource(source.ProviderId, source.SourceId);
                    CreatorNotificationStatusCache.Remove(new(source.ProviderId, source.SourceId));
                    FeedPollSnapshotCache.RemoveSource(source.ProviderId, source.SourceId);
                }
            }
            else
            {
                ApplySettings(document);
            }
        }
        catch
        {
            // SQL rolls back if the callback or commit fails. Restore LocalSettings as well.
            foreach (var key in values.Keys.Where(key => !previous.ContainsKey(key)).ToArray()) values.Remove(key);
            foreach (var pair in previous) values[pair.Key] = pair.Value;
            throw;
        }
        finally
        {
            AppSettings.NotifyNotificationSettingsChanged();
        }
        SourceDirectory.Refresh();
    }

    private static async Task<FeedConfigurationSnapshot> PrepareLegacyNotificationConfigurationAsync(
        SqliteFeedStore store, ConfigurationDocument document)
    {
        var snapshot = await store.ExportConfigurationAsync();
        // Read package declarations only: restoring a file must not call source.create or fetch feeds.
        var manifests = (await new PluginInstallationService().GetEntriesAsync())
            .Where(entry => entry.Manifest is not null && entry.ErrorCode is null)
            .ToDictionary(entry => entry.PluginId, entry => entry.Manifest!, StringComparer.Ordinal);
        var creators = document.Settings.CustomNotificationCreators?.Select(item => new CreatorNotificationRule(
            item.ProviderId, item.SourceId, item.CreatorKey, item.VideoNotificationsEnabled, item.LiveNotificationsEnabled))
            .ToArray() ?? CreatorNotificationPolicies.SavedCreators().ToArray();
        var rules = snapshot.SourceNotifications.ToList();
        var policies = snapshot.CreatorPolicies.ToList();
        var suppressions = snapshot.NotificationSuppressions.ToHashSet();
        foreach (var source in snapshot.Sources)
        {
            var key = new SourceNotificationKey(source.ProviderId, source.SourceId);
            manifests.TryGetValue(source.ProviderId, out var manifest);
            var isRss = source.ProviderId == RssProviderInfo.ProviderId;
            var capabilities = SourceNotificationCapabilities.Resolve(source, snapshot.Subscriptions,
                isRss || manifest?.Capabilities.Contains("feed.updates", StringComparer.Ordinal) == true,
                !isRss && manifest?.Capabilities.Contains("feed.live", StringComparer.Ordinal) == true);
            var sourceCreators = creators.Where(item => item.ProviderId == key.ProviderId && item.SourceId == key.SourceId).ToArray();
            if (!policies.Any(item => item.Key == key) && sourceCreators.Length > 0
                && CreatorNotificationPolicies.SupportsAuthors(manifest) && capabilities.HasAny)
            {
                policies.Add(CreatorNotificationPolicyMigration.FromLegacy(key,
                    rules.FirstOrDefault(item => item.Key == key), sourceCreators));
                rules.RemoveAll(item => item.Key == key);
                suppressions.Remove(key);
            }
            if (!capabilities.HasAny || suppressions.Contains(key) || policies.Any(item => item.Key == key)
                || rules.Any(item => item.Key == key)
                || (!isRss && (manifest is null || !PluginManifestRules.ShouldAutoAddToCustomNotifications(manifest))))
                continue;
            rules.Add(capabilities.CreateRule(key, isAutomatic: true));
        }
        return snapshot with { SourceNotifications = rules, CreatorPolicies = policies, NotificationSuppressions = suppressions.ToArray() };
    }

    private static void ApplySettings(ConfigurationDocument document)
    {
        var settings = document.Settings;
        if (settings.RunningLaunchAction is { } action) AppSettings.RunningLaunchAction = Enum.Parse<RunningLaunchAction>(action);
        if (settings.CustomLaunchWebPageUrl is { } url) AppSettings.CustomLaunchWebPageUrl = url;
        if (settings.DefaultOpenPage is { } page) AppSettings.DefaultOpenPage = Enum.Parse<DefaultOpenPage>(page);
        if (settings.SaveMainPanelPosition is { } position) AppSettings.SaveMainPanelPosition = position;
        if (settings.MainPanelHeight is { } height) AppSettings.MainPanelHeight = height;
        if (settings.NotificationCheckIntervalMinutes is { } interval) AppSettings.NotificationCheckIntervalMinutes = interval;
        if (settings.NotificationTargetMode is { } mode) AppSettings.NotificationTargetMode = Enum.Parse<NotificationTargetMode>(mode);
        if (settings.VideoNotificationsEnabled is { } updates) AppSettings.VideoNotificationsEnabled = updates;
        if (settings.LiveNotificationsEnabled is { } live) AppSettings.LiveNotificationsEnabled = live;
        if (settings.LiveSectionDisplayMode is { } display) AppSettings.LiveSectionDisplayMode = Enum.Parse<LiveSectionDisplayMode>(display);
        if (settings.KeepPanelSourceFilter is { } keep) AppSettings.KeepPanelSourceFilter = keep;
        if (settings.HasPanelSourceFilter) AppSettings.PanelSourceFilter = AppSettings.KeepPanelSourceFilter ? settings.PanelSourceFilter : null;
        if (settings.AppLanguage is { } language) AppSettings.AppLanguage = language;
        if (settings.AutoCreatedPluginSources is { } automaticSources)
            AppSettings.AutoCreatedPluginSources = AppSettings.AutoCreatedPluginSources.Union(automaticSources).ToArray();
        if (settings.CustomNotificationCreators is { } creators)
        {
            var restored = creators.Select(item => new NotificationCreatorSubscription
            {
                ProviderId = item.ProviderId, SourceId = item.SourceId, CreatorKey = item.CreatorKey,
                Name = item.Name, AvatarUrl = item.AvatarUrl, Mid = item.Mid,
                VideoNotificationsEnabled = item.VideoNotificationsEnabled,
                LiveNotificationsEnabled = item.LiveNotificationsEnabled,
            });
            if (document.Database is { } database)
            {
                var keys = database.Sources.Select(source => new SourceNotificationKey(source.ProviderId, source.SourceId)).ToHashSet();
                restored = AppSettings.CustomNotificationCreators.Where(item => !keys.Contains(new(item.ProviderId, item.SourceId)))
                    .Concat(restored);
            }
            AppSettings.CustomNotificationCreators = restored.ToArray();
        }
    }
}
