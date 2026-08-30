using FeedPeek.PluginRuntime.Manifest;

namespace FeedPeek.PluginRuntime.Discovery;

public sealed class PluginDiscoveryService(PluginManifestLoader? manifestLoader = null)
{
    private readonly PluginManifestLoader _manifestLoader = manifestLoader ?? new PluginManifestLoader();

    public async Task<IReadOnlyList<PluginDiscoveryResult>> DiscoverAsync(
        string pluginsRoot,
        IReadOnlySet<string>? disabledPluginIds = null,
        IReadOnlyDictionary<string, string>? expectedPackageHashes = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(pluginsRoot))
            return [];

        var results = new List<PluginDiscoveryResult>();
        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var package = await _manifestLoader.LoadAsync(directory, cancellationToken: cancellationToken);
                if (expectedPackageHashes is not null
                    && expectedPackageHashes.TryGetValue(package.Manifest.PluginId, out var expectedHash)
                    && !string.Equals(expectedHash, package.PackageHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginManifestException("PACKAGE_HASH_MISMATCH", "Plugin package hash does not match its installation record.");
                }

                results.Add(new PluginDiscoveryResult(
                    directory,
                    package,
                    disabledPluginIds?.Contains(package.Manifest.PluginId) == true,
                    null,
                    null));
            }
            catch (PluginManifestException ex)
            {
                results.Add(new PluginDiscoveryResult(directory, null, false, ex.Code, ex.Message));
            }
            catch (Exception ex)
            {
                results.Add(new PluginDiscoveryResult(directory, null, false, "PLUGIN_DISCOVERY_ERROR", ex.Message));
            }
        }

        var duplicates = results
            .Where(result => result.Package is not null)
            .GroupBy(result => result.Package!.Manifest.PluginId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        if (duplicates.Count == 0)
            return results;

        return results.Select(result => duplicates.Contains(result)
            ? result with
            {
                Package = null,
                ErrorCode = "PLUGIN_ID_DUPLICATE",
                ErrorMessage = "More than one installed plugin declares the same pluginId.",
            }
            : result).ToArray();
    }
}
