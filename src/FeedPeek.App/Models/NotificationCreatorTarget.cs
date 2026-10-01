using FeedPeek.Core.Models;
using System.Collections.Generic;

namespace FeedPeek.Models;

internal sealed record NotificationCreatorTarget(
    FeedSubscription Subscription,
    string DisplayName,
    bool SupportsResolve,
    IReadOnlyList<string> InputHosts,
    SourceNotificationCapabilities Capabilities,
    bool SupportsFollowing = false);
