namespace FeedPeek.Core.Models;

/// <summary>Which creator set is eligible for a source's creator notifications.</summary>
public enum CreatorNotificationScope
{
    SelectedCreators = 0,
    AllFollowing = 1,
}

/// <summary>
/// Persisted creator notification policy for one provider/source pair.
/// The two notification kinds deliberately have independent scopes and switches so
/// a legacy configuration cannot be widened while it is being migrated.
/// </summary>
public sealed record CreatorNotificationPolicy(
    string ProviderId,
    string SourceId,
    CreatorNotificationScope UpdatesScope,
    CreatorNotificationScope LiveScope,
    bool UpdatesEnabled,
    bool LiveEnabled)
{
    public SourceNotificationKey Key => new(ProviderId, SourceId);

    /// <summary>Changes both kinds' scope while retaining their enabled switches.</summary>
    public CreatorNotificationPolicy WithScope(CreatorNotificationScope scope)
        => this with { UpdatesScope = scope, LiveScope = scope };

    /// <summary>
    /// Determines whether saved creator rows belong in the main list. Current
    /// capabilities take precedence; when they are unavailable, saved scopes
    /// remain the display fallback so a disabled source does not erase context.
    /// </summary>
    public bool ShouldShowSelectedCreators(
        SourceNotificationKey source,
        SourceNotificationCapabilities? capabilities)
    {
        if (source != Key)
            return false;
        if (capabilities?.HasAny == true)
        {
            return (capabilities.SupportsUpdates && UpdatesScope == CreatorNotificationScope.SelectedCreators)
                || (capabilities.SupportsLive && LiveScope == CreatorNotificationScope.SelectedCreators);
        }

        return UpdatesScope == CreatorNotificationScope.SelectedCreators
            || LiveScope == CreatorNotificationScope.SelectedCreators;
    }
}
