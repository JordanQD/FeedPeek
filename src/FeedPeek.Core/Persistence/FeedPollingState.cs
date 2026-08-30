namespace FeedPeek.Core.Persistence;

public sealed record FeedPollingState(
    string SubscriptionId,
    string? ETag,
    DateTimeOffset? LastModified,
    string? NextCursor,
    bool BaselineInitialized,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    int FailureCount,
    string? LastError);
