using FeedPeek.PluginRuntime.Manifest;

namespace FeedPeek.PluginRuntime.Discovery;

public sealed record PluginDiscoveryResult(
    string DirectoryPath,
    PluginPackage? Package,
    bool IsDisabled,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsAvailable => Package is not null && !IsDisabled && ErrorCode is null;
}
