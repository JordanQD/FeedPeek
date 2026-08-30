using BiliRadar.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliRadar.Services;

internal static class BiliLegacyJsonMapper
{
    public static BiliVideoUpdatePage ParseVideoUpdatesResponse(JsonElement root)
    {
        EnsureBiliSuccess(root);
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return new BiliVideoUpdatePage([], string.Empty, false);
        }

        var updates = new List<BiliVideoUpdate>();
        foreach (var item in items.EnumerateArray())
        {
            var update = TryCreateVideoUpdate(item);
            if (update is not null)
            {
                updates.Add(update);
            }
        }

        return new BiliVideoUpdatePage(updates, GetString(data, "offset"), GetBool(data, "has_more"));
    }

    public static BiliVideoHistoryPage ParseVideoHistoryResponse(JsonElement root, long previousNextMax = 0)
    {
        EnsureBiliSuccess(root);
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return new BiliVideoHistoryPage([], 0, 0, false);
        }

        var items = new List<BiliVideoUpdate>();
        foreach (var item in list.EnumerateArray())
        {
            var historyItem = TryCreateVideoHistoryItem(item);
            if (historyItem is not null)
            {
                items.Add(historyItem);
            }
        }

        var cursor = data.TryGetProperty("cursor", out var cursorElement) ? cursorElement : default;
        var nextMax = GetInt64(cursor, "max");
        var nextViewAt = GetInt64(cursor, "view_at");
        var hasMore = GetBool(data, "has_more")
            || GetBool(cursor, "has_more")
            || (items.Count > 0 && nextMax > 0 && nextMax != previousNextMax);

        return new BiliVideoHistoryPage(items, nextMax, nextViewAt, hasMore);
    }

    public static BiliViewLaterPage ParseViewLaterResponse(
        JsonElement root,
        int loadedCount,
        int currentPageNumber)
    {
        EnsureBiliSuccess(root);
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return new BiliViewLaterPage([], 0, currentPageNumber, false);
        }

        var items = new List<BiliVideoUpdate>();
        foreach (var item in list.EnumerateArray())
        {
            var video = TryCreateViewLaterItem(item);
            if (video is not null)
            {
                items.Add(video);
            }
        }

        var totalCount = GetInt32(data, "count");
        var nextPageNumber = currentPageNumber + 1;
        var hasMore = items.Count > 0 && loadedCount + items.Count < totalCount;
        return new BiliViewLaterPage(items, totalCount, nextPageNumber, hasMore);
    }

    private static BiliVideoUpdate? TryCreateVideoUpdate(JsonElement item)
    {
        if (!item.TryGetProperty("modules", out var modules)
            || !modules.TryGetProperty("module_dynamic", out var dynamicModule)
            || !dynamicModule.TryGetProperty("major", out var major))
        {
            return null;
        }

        JsonElement video;
        var isOpus = false;
        if (major.TryGetProperty("archive", out var archive))
        {
            video = archive;
        }
        else if (major.TryGetProperty("pgc", out var pgc))
        {
            video = pgc;
        }
        else if (major.TryGetProperty("opus", out var opus))
        {
            video = opus;
            isOpus = true;
        }
        else
        {
            return null;
        }

        var jumpUrl = NormalizeUrl(GetString(video, "jump_url"));
        var bvid = GetString(video, "bvid");
        if (string.IsNullOrWhiteSpace(bvid) && !string.IsNullOrWhiteSpace(jumpUrl))
        {
            bvid = ExtractBvidFromUrl(jumpUrl);
        }

        var aid = GetInt64(video, "aid");
        if (aid <= 0 && isOpus && !string.IsNullOrWhiteSpace(jumpUrl))
        {
            aid = ExtractAidFromUrl(jumpUrl);
        }

        var id = !string.IsNullOrWhiteSpace(bvid) ? bvid : aid.ToString();
        var title = GetString(video, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cover = NormalizeUrl(GetString(video, "cover"));
        if (string.IsNullOrWhiteSpace(cover) && isOpus && video.TryGetProperty("pics", out var pics)
            && pics.ValueKind == JsonValueKind.Array)
        {
            foreach (var pic in pics.EnumerateArray())
            {
                cover = NormalizeUrl(GetString(pic, "url"));
                if (!string.IsNullOrWhiteSpace(cover))
                {
                    break;
                }
            }
        }

        var author = modules.TryGetProperty("module_author", out var authorModule) ? authorModule : default;
        var publishedAt = TryGetPublishedAt(author, out var time) ? time : DateTimeOffset.Now;
        var likeCount = 0;
        var commentCount = 0;
        if (modules.TryGetProperty("module_stat", out var stat))
        {
            likeCount = GetNestedCount(stat, "like");
            commentCount = GetNestedCount(stat, "comment");
        }

        if (video.TryGetProperty("stat", out var videoStat))
        {
            likeCount = likeCount == 0 ? GetInt32(videoStat, "like") : likeCount;
            commentCount = commentCount == 0 ? GetInt32(videoStat, "reply") : commentCount;
        }

        if (string.IsNullOrWhiteSpace(jumpUrl))
        {
            jumpUrl = !string.IsNullOrWhiteSpace(bvid)
                ? $"https://www.bilibili.com/video/{bvid}"
                : $"https://www.bilibili.com/av{aid}";
        }

        return new BiliVideoUpdate(
            id,
            aid,
            GetInt64(author, "mid"),
            GetString(author, "name"),
            title,
            publishedAt,
            jumpUrl,
            true,
            cover,
            NormalizeUrl(GetString(author, "face")),
            FormatRelativeTime(publishedAt),
            GetString(video, "duration_text"),
            GetString(video, "desc"),
            likeCount,
            commentCount);
    }

    private static BiliVideoUpdate? TryCreateVideoHistoryItem(JsonElement item)
    {
        if (!item.TryGetProperty("history", out var history))
        {
            return null;
        }

        var business = GetString(history, "business");
        if (!string.IsNullOrWhiteSpace(business)
            && !string.Equals(business, "archive", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = GetString(item, "title");
        var bvid = GetString(history, "bvid");
        var aid = GetInt64(history, "oid");
        if (aid <= 0)
        {
            aid = GetInt64(item, "aid");
        }

        var id = !string.IsNullOrWhiteSpace(bvid) ? bvid : aid.ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var durationSeconds = GetInt32(item, "duration");
        var progressSeconds = GetInt32(item, "progress");
        var viewAtTimestamp = GetInt64(item, "view_at");
        var page = GetInt32(history, "page");
        var videos = GetInt32(item, "videos");
        var viewedAt = viewAtTimestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(viewAtTimestamp).ToLocalTime()
            : DateTimeOffset.Now;
        var url = NormalizeUrl(GetString(item, "uri"));
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = !string.IsNullOrWhiteSpace(bvid)
                ? $"https://www.bilibili.com/video/{bvid}"
                : $"https://www.bilibili.com/av{aid}";
        }

        return new BiliVideoUpdate(
            id,
            aid,
            GetInt64(item, "author_mid"),
            GetString(item, "author_name"),
            title,
            viewedAt,
            url,
            false,
            NormalizeUrl(GetString(item, "cover")),
            NormalizeUrl(GetString(item, "author_face")),
            $"{FormatRelativeTime(viewedAt)}观看",
            FormatDuration(durationSeconds),
            FormatHistoryDescription(progressSeconds, durationSeconds, page, videos),
            0,
            0,
            page,
            videos);
    }

    private static BiliVideoUpdate? TryCreateViewLaterItem(JsonElement item)
    {
        var title = GetString(item, "title");
        var bvid = GetString(item, "bvid");
        var aid = GetInt64(item, "aid");
        var id = !string.IsNullOrWhiteSpace(bvid) ? bvid : aid.ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var owner = item.TryGetProperty("owner", out var ownerElement) ? ownerElement : default;
        var durationSeconds = GetInt32(item, "duration");
        var progressSeconds = GetInt32(item, "progress");
        var addAtTimestamp = GetInt64(item, "add_at");
        var addedAt = addAtTimestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(addAtTimestamp).ToLocalTime()
            : DateTimeOffset.Now;
        var url = NormalizeUrl(GetString(item, "redirect_url"));
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = !string.IsNullOrWhiteSpace(bvid)
                ? $"https://www.bilibili.com/video/{bvid}"
                : $"https://www.bilibili.com/av{aid}";
        }

        var stat = item.TryGetProperty("stat", out var statElement) ? statElement : default;
        return new BiliVideoUpdate(
            id,
            aid,
            GetInt64(owner, "mid"),
            GetString(owner, "name"),
            title,
            addedAt,
            url,
            false,
            NormalizeUrl(GetString(item, "pic")),
            NormalizeUrl(GetString(owner, "face")),
            $"{FormatRelativeTime(addedAt)}添加",
            FormatDuration(durationSeconds),
            FormatProgress(progressSeconds, durationSeconds),
            GetInt32(stat, "like"),
            GetInt32(stat, "reply"));
    }

    private static void EnsureBiliSuccess(JsonElement root)
    {
        var code = GetInt32(root, "code");
        if (code == 0)
        {
            return;
        }

        var message = GetString(root, "message");
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"B 站接口返回错误：{code}"
            : $"B 站接口返回错误：{message} ({code})");
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => 0,
        };
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0,
        };
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var flag) => flag,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number != 0,
            _ => false,
        };
    }

    private static int GetNestedCount(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Object
            ? GetInt32(value, "count")
            : TryGetInt32(value, out var count) ? count : 0;
    }

    private static bool TryGetPublishedAt(JsonElement author, out DateTimeOffset publishedAt)
    {
        publishedAt = default;
        if (author.ValueKind != JsonValueKind.Object
            || !author.TryGetProperty("pub_ts", out var value)
            || !TryGetInt64(value, out var timestamp)
            || timestamp <= 0)
        {
            return false;
        }

        publishedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime();
        return true;
    }

    private static bool TryGetInt32(JsonElement value, out int number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt32(out number);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(value.GetString(), out number);
        }

        number = 0;
        return false;
    }

    private static bool TryGetInt64(JsonElement value, out long number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out number);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(value.GetString(), out number);
        }

        number = 0;
        return false;
    }

    private static string FormatRelativeTime(DateTimeOffset time)
    {
        var now = DateTimeOffset.Now;
        var delta = now - time;
        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
        }

        if (delta.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalHours)} 小时前";
        }

        if (time.Date == now.AddDays(-1).Date)
        {
            return $"昨天 {time:HH:mm}";
        }

        return time.Year == now.Year ? time.ToString("M-d HH:mm") : time.ToString("yyyy-M-d HH:mm");
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
        {
            return string.Empty;
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static string FormatProgress(int progressSeconds, int durationSeconds)
    {
        if (progressSeconds < 0 || (durationSeconds > 0 && progressSeconds >= durationSeconds))
        {
            return "已看完";
        }

        return progressSeconds <= 0 ? string.Empty : $"看到 {FormatDuration(progressSeconds)}";
    }

    private static string FormatHistoryDescription(int progressSeconds, int durationSeconds, int page, int videos)
    {
        var pagePart = videos > 1 ? $"P{page}/{videos} · " : string.Empty;
        return pagePart + FormatProgress(progressSeconds, durationSeconds);
    }

    private static string NormalizeUrl(string url)
        => string.IsNullOrWhiteSpace(url)
            ? string.Empty
            : url.StartsWith("//", StringComparison.Ordinal) ? $"https:{url}" : url;

    private static string ExtractBvidFromUrl(string url)
    {
        var match = Regex.Match(url, @"BV[\w]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : string.Empty;
    }

    private static long ExtractAidFromUrl(string url)
    {
        var match = Regex.Match(url, @"av(\d+)", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var aid) ? aid : 0;
    }
}
