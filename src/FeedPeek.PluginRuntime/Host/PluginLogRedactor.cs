using System.Text.RegularExpressions;

namespace FeedPeek.PluginRuntime.Host;

public static partial class PluginLogRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var redacted = AuthorizationHeaderPattern().Replace(value, "$1: [REDACTED]");
        redacted = CookieHeaderPattern().Replace(redacted, "$1: [REDACTED]");
        redacted = NamedSecretPattern().Replace(redacted, "$1=[REDACTED]");
        return QuerySecretPattern().Replace(redacted, "$1[REDACTED]");
    }

    public static IReadOnlyDictionary<string, string>? Redact(IReadOnlyDictionary<string, string>? values)
        => values?.ToDictionary(
            pair => pair.Key,
            pair => SensitiveKeyPattern().IsMatch(pair.Key) ? "[REDACTED]" : Redact(pair.Value),
            StringComparer.Ordinal);

    [GeneratedRegex("(?im)\\b(authorization|proxy-authorization)\\s*[:=]\\s*[^\\r\\n]+")]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex("(?im)\\b(cookie|set-cookie)\\s*[:=]\\s*[^\\r\\n]+")]
    private static partial Regex CookieHeaderPattern();

    [GeneratedRegex("(?i)\\b(api[_-]?key|x-api-key|client[_-]?secret|password|token|secret|signature|session)\\b\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)")]
    private static partial Regex NamedSecretPattern();

    [GeneratedRegex("(?i)([?&](?:token|access_token|refresh_token|sign|signature|session|key|api_key|apikey|client_secret|password|code)=)[^&#\\s]+")]
    private static partial Regex QuerySecretPattern();

    [GeneratedRegex("(?i)^(?:authorization|proxy-authorization|cookie|set-cookie|api[_-]?key|apikey|x-api-key|client[_-]?secret|password|access[_-]?token|refresh[_-]?token|token|secret|signature|session)$")]
    private static partial Regex SensitiveKeyPattern();
}
