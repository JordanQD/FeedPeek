using FeedPeek.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace FeedPeek.Services;

/// <summary>Display-only account-bound relationship observations; never deletes saved choices.</summary>
internal static class CreatorNotificationStatusCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SourceNotificationKey, Status> Sources = [];

    public static CreatorFollowState GetState(SourceNotificationKey source, string creatorKey, string? credentialProfileId)
    {
        var generation = NotificationStateChanges.MembershipGeneration(source, credentialProfileId);
        lock (Gate)
        {
            if (!Sources.TryGetValue(source, out var status) || status.Generation != generation) return CreatorFollowState.Unknown;
            if (status.Overrides.TryGetValue(creatorKey, out var state)) return state;
            return status.Following.Contains(creatorKey) ? CreatorFollowState.Followed
                : status.IsComplete ? CreatorFollowState.NotFollowed : CreatorFollowState.Unknown;
        }
    }

    public static void Publish(SourceNotificationKey source, string generation, IReadOnlyList<CreatorDescriptor> creators, bool isComplete)
    {
        lock (Gate) Sources[source] = new(generation, creators.Select(creator => creator.CreatorKey).ToHashSet(), isComplete);
    }

    public static void SetCreatorState(SourceNotificationKey source, string? credentialProfileId, string creatorKey, CreatorFollowState state,
        string? expectedGeneration = null)
    {
        // Stamp a response with the account that was queried, never with a newer account at write time.
        var generation = expectedGeneration ?? NotificationStateChanges.MembershipGeneration(source, credentialProfileId);
        if (generation != NotificationStateChanges.MembershipGeneration(source, credentialProfileId)) return;
        lock (Gate)
        {
            if (!Sources.TryGetValue(source, out var status) || status.Generation != generation)
                Sources[source] = status = new(generation, [], false);
            status.Overrides[creatorKey] = state;
        }
    }

    public static void Remove(SourceNotificationKey source) { lock (Gate) Sources.Remove(source); }

    private sealed record Status(string Generation, HashSet<string> Following, bool IsComplete)
    {
        public Dictionary<string, CreatorFollowState> Overrides { get; } = [];
    }
}
