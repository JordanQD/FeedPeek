using System;
using System.IO;
using System.Threading.Tasks;

namespace FeedPeek.Helpers;

/// <summary>Reads local image bytes independently of the decoder's lifetime.</summary>
internal static class LocalImageFile
{
    public static string ResolvePath(Uri uri, string localRoot, string roamingRoot, string temporaryRoot)
    {
        if (uri.IsFile) return uri.LocalPath;
        if (!string.Equals(uri.Scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Unsupported local image URI.", nameof(uri));

        var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        if (parts.Length != 2) throw new ArgumentException("Local image URI has no file path.", nameof(uri));
        var root = parts[0] switch
        {
            "local" => localRoot,
            "roaming" => roamingRoot,
            "temp" => temporaryRoot,
            _ => throw new ArgumentException("Unsupported app data folder.", nameof(uri)),
        };
        var relative = Uri.UnescapeDataString(parts[1]).Replace('/', Path.DirectorySeparatorChar);
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootPrefix, relative));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Local image URI leaves its app data folder.", nameof(uri));
        return path;
    }

    public static async Task<byte[]> ReadDetachedAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory).ConfigureAwait(false);
        return memory.ToArray();
    }
}
