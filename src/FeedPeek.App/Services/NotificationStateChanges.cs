using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;
using System.Collections.Generic;
using System.Threading;

namespace FeedPeek.Services;

/// <summary>Process-local generations catch even an off/on sequence between two polls.</summary>
internal static class NotificationStateChanges
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SourceNotificationKey, long> Sources = [];
    private static readonly Dictionary<string, long> Providers = [];
    private static readonly Dictionary<(SourceNotificationKey Source, NotificationKind Kind, string? Creator), long> Scopes = [];
    private static long _revision, _mode, _updates, _live;
    public static long Revision => Interlocked.Read(ref _revision);

    public static void MarkMode() { lock (Gate) _mode = ++_revision; }
    public static void MarkGlobal(NotificationKind kind)
    {
        lock (Gate)
        {
            if (kind == NotificationKind.Updates) _updates = ++_revision;
            else _live = ++_revision;
        }
    }
    public static void MarkSource(string providerId, string sourceId)
    {
        lock (Gate) Sources[new(providerId, sourceId)] = ++_revision;
    }
    public static void MarkProvider(string providerId)
    {
        lock (Gate) Providers[providerId] = ++_revision;
    }
    public static void MarkScope(string providerId, string sourceId, NotificationKind kind, string? creator = null)
    {
        lock (Gate) Scopes[(new(providerId, sourceId), kind, creator)] = ++_revision;
    }
    public static void Pulse() { lock (Gate) ++_revision; }

    public static string MembershipGeneration(SourceNotificationKey source, string? credentialProfileId)
    {
        lock (Gate) return $"{credentialProfileId}|{Sources.GetValueOrDefault(source)}|{Providers.GetValueOrDefault(source.ProviderId)}";
    }

    public static string Generation(NotificationWatchKey key, NotificationSelectionMode mode, string subscriptionId,
        bool includeCreatorScope = true)
    {
        lock (Gate)
        {
            var source = new SourceNotificationKey(key.Feed.ProviderId, key.Feed.SourceId);
            var global = mode == NotificationSelectionMode.AllSources
                ? key.Feed.Kind == NotificationKind.Updates ? _updates : _live : 0;
            var sourceScope = mode == NotificationSelectionMode.Custom ? Scopes.GetValueOrDefault((source, key.Feed.Kind, null)) : 0;
            var creatorScope = mode == NotificationSelectionMode.Custom && includeCreatorScope && key.CreatorKey is not null
                ? Scopes.GetValueOrDefault((source, key.Feed.Kind, key.CreatorKey)) : 0;
            return $"{subscriptionId}|{_mode}|{global}|{Sources.GetValueOrDefault(source)}|"
                + $"{Providers.GetValueOrDefault(key.Feed.ProviderId)}|"
                + $"{sourceScope}|{creatorScope}";
        }
    }
}
