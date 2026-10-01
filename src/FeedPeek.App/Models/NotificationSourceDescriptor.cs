using FeedPeek.Core.Models;

namespace FeedPeek.Models;

/// <summary>Current source data and host defaults, without changing saved notification switches.</summary>
public sealed record NotificationSourceDescriptor(
    SourceDefinition Source,
    SourceNotificationCapabilities Capabilities,
    bool AutoAddToCustom);
