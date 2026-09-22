using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace FeedPeek.Helpers;

/// <summary>
/// Shared BitmapImage cache with decode-size limits and a bounded LRU.
/// Rows must not construct full-resolution bitmaps for 20–128 px slots.
/// </summary>
public static class ImageSourceCache
{
    public const int CoverDecodePixelWidth = 256;
    public const int IconDecodePixelWidth = 40;
    public const int AvatarDecodePixelWidth = 88;
    public const int SettingsAvatarDecodePixelWidth = 80;
    private const int DefaultCapacity = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Map = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> Lru = new();
    private static int _capacity = DefaultCapacity;

    public static int Capacity
    {
        get
        {
            lock (Gate) return _capacity;
        }
        set
        {
            lock (Gate)
            {
                _capacity = Math.Max(16, value);
                TrimLocked();
            }
        }
    }

    public static int Count
    {
        get
        {
            lock (Gate) return Map.Count;
        }
    }

    public static BitmapImage? GetOrCreate(string? imageUrl, int decodePixelWidth)
    {
        if (!TryNormalizeUri(imageUrl, out var uri))
        {
            return null;
        }

        decodePixelWidth = Math.Max(1, decodePixelWidth);
        var key = $"{uri.AbsoluteUri}|{decodePixelWidth}";

        lock (Gate)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                return node.Value.Image;
            }

            var image = CreateBitmap(uri, decodePixelWidth);
            var entry = new CacheEntry(key, image);
            var created = Lru.AddFirst(entry);
            Map[key] = created;
            TrimLocked();
            return image;
        }
    }

    public static BitmapImage? GetCover(string? imageUrl) => GetOrCreate(imageUrl, CoverDecodePixelWidth);

    public static BitmapImage? GetIcon(string? imageUrl) => GetOrCreate(imageUrl, IconDecodePixelWidth);

    public static BitmapImage? GetAvatar(string? imageUrl) => GetOrCreate(imageUrl, AvatarDecodePixelWidth);

    public static BitmapImage? GetSettingsAvatar(string? imageUrl) => GetOrCreate(imageUrl, SettingsAvatarDecodePixelWidth);

    public static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Lru.Clear();
        }
    }

    private static BitmapImage CreateBitmap(Uri uri, int decodePixelWidth)
    {
        var image = new BitmapImage
        {
            DecodePixelWidth = decodePixelWidth,
            UriSource = uri,
        };
        return image;
    }

    private static void TrimLocked()
    {
        while (Map.Count > _capacity && Lru.Last is not null)
        {
            var node = Lru.Last;
            Lru.RemoveLast();
            Map.Remove(node.Value.Key);
        }
    }

    private static bool TryNormalizeUri(string? imageUrl, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme is "http" or "https" or "file"
            || string.Equals(parsed.Scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    private sealed record CacheEntry(string Key, BitmapImage Image);
}
