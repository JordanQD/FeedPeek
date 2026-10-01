namespace FeedPeek.Core.Models;

/// <summary>Address matching does not change a provider's HTTP or credential permissions.</summary>
public static class SourceAddress
{
    public const string AddressOption = "source.address";

    public static bool TryParseWebUri(string? input, out Uri uri)
    {
        uri = null!;
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) || value.Contains('\\')) return false;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        else if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    public static bool TryMatchRoomTemplate(string? template, Uri address, out string roomId, out string canonicalAddress)
    {
        roomId = canonicalAddress = string.Empty;
        const string marker = "{roomId}";
        const string placeholder = "feedpeek-room-id";
        if (string.IsNullOrWhiteSpace(template) || !template.Contains(marker, StringComparison.Ordinal)
            || template.IndexOf(marker, StringComparison.Ordinal) != template.LastIndexOf(marker, StringComparison.Ordinal)
            || !TryParseWebUri(template.Replace(marker, placeholder, StringComparison.Ordinal), out var canonical)
            || address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo)
            || !(canonical.IsDefaultPort && address.IsDefaultPort || canonical.Port == address.Port)) return false;

        var hostMatches = string.Equals(canonical.IdnHost, address.IdnHost, StringComparison.OrdinalIgnoreCase)
            || canonical.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                && string.Equals(canonical.IdnHost[4..], address.IdnHost, StringComparison.OrdinalIgnoreCase);
        if (!hostMatches) return false;
        var markerIndex = canonical.AbsolutePath.IndexOf(placeholder, StringComparison.Ordinal);
        if (markerIndex < 0) return false;
        var prefix = canonical.AbsolutePath[..markerIndex];
        var suffix = canonical.AbsolutePath[(markerIndex + placeholder.Length)..];
        var path = address.AbsolutePath;
        if (suffix.Length == 0) path = path.TrimEnd('/');
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)
            || path.Length <= prefix.Length + suffix.Length) return false;
        var id = path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length);
        if (!id.All(char.IsAsciiDigit)) return false;
        roomId = id;
        canonicalAddress = template.Replace(marker, id, StringComparison.Ordinal);
        if (!TryParseWebUri(canonicalAddress, out var result)) return false;
        canonicalAddress = result.AbsoluteUri;
        return true;
    }

    public static bool TryGetRoomAddress(SourceDefinition source, string? template, out string address)
    {
        address = string.Empty;
        if (source.Options is null) return false;
        if (source.Options.TryGetValue(AddressOption, out var saved) && TryParseWebUri(saved, out var savedUri))
        {
            address = savedUri.AbsoluteUri;
            return true;
        }
        if (string.IsNullOrWhiteSpace(template) || !source.Options.TryGetValue("roomIds", out var roomIds)) return false;
        var tokens = roomIds.Split([',', ' ', ';', '\n', '\r', '\t', '，', '、'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 1) return false;
        if (!tokens[0].All(char.IsAsciiDigit))
            return TryParseWebUri(tokens[0], out var roomUri) && TryMatchRoomTemplate(template, roomUri, out _, out address);
        if (!TryParseWebUri(template.Replace("{roomId}", tokens[0], StringComparison.Ordinal), out var uri)) return false;
        return TryMatchRoomTemplate(template, uri, out _, out address);
    }
}
