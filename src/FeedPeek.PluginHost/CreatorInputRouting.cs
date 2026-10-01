using FeedPeek.Core.Models;

namespace FeedPeek.PluginHost;

/// <summary>Routes exact declared hosts. Site-specific paths and author IDs belong to the plugin.</summary>
public static class CreatorInputRouting
{
    public static bool TryGetWebUri(string input, out Uri? uri)
    {
        uri = null;
        var text = input.Trim();
        if (!text.Contains('.') && !text.Contains("://")) return false;
        if (!text.Contains("://")) text = "https://" + text;
        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || authority.Contains(':')) return false;
        uri = parsed;
        return true;
    }

    public static bool MatchesHost(Uri uri, IReadOnlyList<string> declaredHosts)
        => declaredHosts.Any(host => string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase));

    public static CreatorResolutionResult FromFollowingPage(FollowingCreatorsPage page, string input)
    {
        var exactIds = page.Creators.Where(creator => string.Equals(creator.CreatorKey, input.Trim(), StringComparison.Ordinal)).ToArray();
        if (exactIds.Length > 1) return new(null, CreatorFollowState.Unknown);
        if (exactIds.Length == 1) return new(exactIds[0], CreatorFollowState.Followed);
        var isUrl = TryGetWebUri(input, out var uri);
        var matches = page.Creators.Where(creator => isUrl
            ? TryGetWebUri(creator.ProfileUrl ?? string.Empty, out var profile)
                && string.Equals(uri!.IdnHost, profile!.IdnHost, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.AbsolutePath.TrimEnd('/'), profile.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
            : string.Equals(creator.CreatorKey, input.Trim(), StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1) return new(null, CreatorFollowState.Unknown);
        if (matches.Length == 1) return new(matches[0], CreatorFollowState.Followed);
        // A full list can disprove an exact author ID; unknown URL paths cannot.
        var looksLikeUrl = input.Contains("://", StringComparison.Ordinal) || input.Contains('/') || input.Contains('.');
        return new(null, page.IsComplete && !isUrl && !looksLikeUrl && !string.IsNullOrWhiteSpace(input)
            ? CreatorFollowState.NotFollowed : CreatorFollowState.Unknown);
    }
}
