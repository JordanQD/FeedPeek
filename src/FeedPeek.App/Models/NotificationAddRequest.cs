namespace FeedPeek.Models;

/// <summary>Reopens the unified picker from a source's notification details.</summary>
public sealed record NotificationAddRequest(string ProviderId, string SourceId, bool AuthorsOnly = false);
