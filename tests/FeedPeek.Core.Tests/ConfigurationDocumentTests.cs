using FeedPeek.Core.Configuration;
using FeedPeek.Core.Models;
using FeedPeek.Core.Persistence;

namespace FeedPeek.Core.Tests;

public sealed class ConfigurationDocumentTests
{
    [Fact]
    public async Task SerializedFileRestoresSourcesScopesAndDeletedNotificationState()
    {
        await using var originalStore = new SqliteFeedStore("Data Source=:memory:");
        foreach (var sourceId in new[] { "room", "authors", "removed" })
        {
            var source = new SourceDefinition("provider", sourceId, sourceId, "local-account",
                [new FeedDefinition("updates", "Updates", "updates", true, true),
                 new FeedDefinition("live", "Live", "live", false, true)],
                new Dictionary<string, string> { ["source.address"] = "https://example.com/" + sourceId });
            await originalStore.SaveSourceAndSubscriptionAsync(source, new FeedSubscription(
                "subscription-" + sourceId, "provider", sourceId, "updates", sourceId,
                "https://example.com/feed", "local-account", true, TimeSpan.FromMinutes(15), source.Options));
        }
        await originalStore.SaveSourceNotificationRuleAsync(new("provider", "room", ["updates"], ["live"], false, true));
        await originalStore.SaveCreatorNotificationPolicyAsync(new("provider", "authors",
            CreatorNotificationScope.AllFollowing, CreatorNotificationScope.SelectedCreators, true, false));
        await originalStore.DeleteSourceNotificationRuleAsync(new("provider", "removed"));
        var document = CreateDocument();
        document.Database = await originalStore.ExportConfigurationAsync();
        document.Settings.CustomNotificationCreators = [new("provider", "authors", "123", "作者", "", false, true)];

        var restoredFile = ConfigurationDocument.Parse(document.ToJson());
        await using var restoredStore = new SqliteFeedStore("Data Source=:memory:");
        await restoredStore.ImportConfigurationAsync(restoredFile.Database!);

        Assert.Equal(3, (await restoredStore.GetSourceSummariesAsync()).Count);
        var roomRule = Assert.Single(await restoredStore.GetSourceNotificationRulesAsync());
        Assert.False(roomRule.UpdateNotificationsEnabled);
        Assert.True(roomRule.LiveNotificationsEnabled);
        var policy = Assert.Single(await restoredStore.GetCreatorNotificationPoliciesAsync());
        Assert.Equal(CreatorNotificationScope.AllFollowing, policy.UpdatesScope);
        Assert.Equal(CreatorNotificationScope.SelectedCreators, policy.LiveScope);
        Assert.False(policy.LiveEnabled);
        Assert.Equal(0, await restoredStore.SynchronizeSourceNotificationRulesAsync(
            [new("provider", "removed", ["updates"], [], true, false, true)]));
        var creator = Assert.Single(restoredFile.Settings.CustomNotificationCreators!);
        Assert.False(creator.VideoNotificationsEnabled);
        Assert.Empty(creator.AvatarUrl);
        Assert.All(restoredFile.Database!.Sources, source => Assert.Null(source.CredentialProfileId));
    }

    [Fact]
    public void VersionOneRoundTripPreservesFalseAndExplicitNull()
    {
        var original = CreateDocument();
        original.Settings.VideoNotificationsEnabled = false;
        original.Settings.PanelSourceFilter = null;

        var json = original.ToJson();
        var parsed = ConfigurationDocument.Parse(json);

        Assert.False(parsed.IsLegacy);
        Assert.False(parsed.Settings.VideoNotificationsEnabled);
        Assert.True(parsed.Settings.HasPanelSourceFilter);
        Assert.Null(parsed.Settings.PanelSourceFilter);
        Assert.Equal("OpenSettings", parsed.Settings.RunningLaunchAction);
        Assert.Equal("https://example.com/", parsed.Settings.CustomLaunchWebPageUrl);
    }

    [Fact]
    public void LegacyMissingFieldsRemainNullAndMidGetsPortableIdentity()
    {
        const string json = """
            {
              "ExportedAt": "2026-10-03T00:00:00+08:00",
              "RunningLaunchAction": "OpenBilibiliWebPage",
              "CustomNotificationCreators": [
                { "Mid": 123, "Name": "作者", "AvatarUrl": "", "VideoNotificationsEnabled": true, "LiveNotificationsEnabled": false }
              ]
            }
            """;

        var parsed = ConfigurationDocument.Parse(json);

        Assert.True(parsed.IsLegacy);
        Assert.Equal("OpenCustomWebPage", parsed.Settings.RunningLaunchAction);
        Assert.Null(parsed.Settings.AutoStartEnabled);
        Assert.False(parsed.Settings.HasPanelSourceFilter);
        var creator = Assert.Single(parsed.Settings.CustomNotificationCreators!);
        Assert.Equal("feedpeek.video-site", creator.ProviderId);
        Assert.Equal("account-default", creator.SourceId);
        Assert.Equal("123", creator.CreatorKey);
        Assert.False(creator.LiveNotificationsEnabled);
        Assert.Throws<InvalidOperationException>(() => parsed.ToJson());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"format\":\"FeedPeek.Configuration\",\"FORMAT\":\"FeedPeek.Configuration\"}")]
    [InlineData("{\"format\":\"FeedPeek.Configuration\",\"version\":2}")]
    [InlineData("{\"autoStartEnabled\":true,\"AutoStartEnabled\":false}")]
    public void RejectsEmptyUnknownDuplicateAndUnsupportedDocuments(string json)
    {
        Assert.ThrowsAny<Exception>(() => ConfigurationDocument.Parse(json));
    }

    [Fact]
    public void RejectsInvalidEnumAndCreatorSourceReference()
    {
        var document = CreateDocument();
        document.Settings.DefaultOpenPage = "NoSuchPage";
        Assert.ThrowsAny<Exception>(() => document.ToJson());

        document = CreateDocument();
        document.Settings.DefaultOpenPage = "Following";
        document.Settings.CustomNotificationCreators =
        [new ConfigurationCreator("other", "source", "creator", "name", "", true, true)];
        Assert.ThrowsAny<Exception>(() => document.ToJson());
    }

    [Fact]
    public void RejectsVersionOneSettingsWithoutExplicitPanelSourceFilter()
    {
        var json = CreateDocument().ToJson();
        json = json.Replace("\"panelSourceFilter\": null,", string.Empty, StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => ConfigurationDocument.Parse(json));
    }

    private static ConfigurationDocument CreateDocument()
        => new()
        {
            ExportedAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
            Settings = new ConfigurationSettings
            {
                AutoStartEnabled = false,
                RunningLaunchAction = "OpenSettings",
                CustomLaunchWebPageUrl = "example.com",
                DefaultOpenPage = "Following",
                NotificationTargetMode = "AllFollowing",
                LiveSectionDisplayMode = "Expanded",
                AppLanguage = "system",
                SaveMainPanelPosition = false,
                VideoNotificationsEnabled = true,
                LiveNotificationsEnabled = true,
                KeepPanelSourceFilter = true,
                MainPanelHeight = 800,
                NotificationCheckIntervalMinutes = 15,
                PanelSourceFilter = null,
                CustomNotificationCreators = [],
                AutoCreatedPluginSources = ["provider"],
            },
            Database = new FeedConfigurationSnapshot(
                Sources:
                [new SourceDefinition(
                    "provider", "source", "Source", null,
                    [new FeedDefinition("updates", "Updates", "video", true, true)],
                    new Dictionary<string, string>())],
                Subscriptions:
                [new FeedSubscription("subscription", "provider", "source", "updates", "Updates", null, null,
                    true, TimeSpan.FromMinutes(15), new Dictionary<string, string>())],
                SourceNotifications:
                [new SourceNotificationRule("provider", "source", ["updates"], [], true, false)],
                CreatorPolicies: [],
                NotificationSuppressions: []),
        };
}
