using FeedPeek.PluginContracts.Manifest;

namespace FeedPeek.PluginRuntime.Manifest;

public sealed record PluginPackage(
    string RootPath,
    string ManifestPath,
    PluginManifest Manifest,
    string PackageHash,
    IReadOnlyDictionary<string, string> Scripts);
