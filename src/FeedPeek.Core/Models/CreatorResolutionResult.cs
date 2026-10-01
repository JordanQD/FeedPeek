namespace FeedPeek.Core.Models;

public enum CreatorFollowState { Unknown, Followed, NotFollowed }

public sealed record CreatorResolutionResult(CreatorDescriptor? Creator, CreatorFollowState FollowState);

/// <summary>Old plugins omit completeness; absence is not evidence of an unfollowed author.</summary>
public sealed record FollowingCreatorsPage(IReadOnlyList<CreatorDescriptor> Creators, bool IsComplete);
