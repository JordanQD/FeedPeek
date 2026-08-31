namespace BiliRadar.Services;

// Identifiers retained only for one-way migration of pre-plugin BiliRadar data.
// They must not be used to install a plugin or create a source automatically.
internal static class LegacyVideoSiteCompatibility
{
    public const string PluginId = "feedpeek.video-site";
    public const string SourceId = "account-default";
    public const string DefaultCredentialProfileId = "account-default";
}
