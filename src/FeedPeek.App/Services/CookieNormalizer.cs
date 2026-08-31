using System;
using System.Collections.Generic;
using System.Linq;

namespace FeedPeek.Services;

internal static class CookieNormalizer
{
    public static string Normalize(string cookie)
    {
        var pairs = ParsePairs(cookie);
        return string.Join("; ", pairs.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static IDictionary<string, string> ParsePairs(string cookie)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var segments = cookie
            .ReplaceLineEndings(";")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }
}
