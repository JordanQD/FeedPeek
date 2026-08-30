using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginContracts.Protocol;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FeedPeek.PluginRuntime.Manifest;

public sealed partial class PluginManifestLoader
{
    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxScriptBytes = 1024 * 1024;
    private const int MaxTotalScriptBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".msi", ".msix", ".appx", ".scr",
    };

    public async Task<PluginPackage> LoadAsync(
        string pluginRoot,
        string? expectedPackageHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        var root = Path.GetFullPath(pluginRoot);
        if (!Directory.Exists(root))
            throw new PluginManifestException("PLUGIN_DIRECTORY_MISSING", $"Plugin directory does not exist: {root}");

        RejectReparsePoint(root);
        var manifestPath = ResolveContainedPath(root, PluginManifestRules.FileName, requireJavaScript: false);
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists)
            throw new PluginManifestException("MANIFEST_MISSING", $"Missing {PluginManifestRules.FileName}.");
        if (manifestInfo.Length > MaxManifestBytes)
            throw new PluginManifestException("MANIFEST_TOO_LARGE", "Plugin manifest exceeds the size limit.");

        PluginManifest manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
                    stream,
                    PluginJson.SerializerOptions,
                    cancellationToken)
                ?? throw new PluginManifestException("MANIFEST_EMPTY", "Plugin manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException("MANIFEST_INVALID_JSON", $"Plugin manifest JSON is invalid: {ex.Message}");
        }

        Validate(root, manifest);
        var snapshot = await CreatePackageSnapshotAsync(root, manifest, cancellationToken);
        var hash = snapshot.PackageHash;
        if (!string.IsNullOrWhiteSpace(expectedPackageHash))
        {
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(hash),
                        Convert.FromHexString(expectedPackageHash)))
                {
                    throw new PluginManifestException("PACKAGE_HASH_MISMATCH", "Plugin package hash does not match its installation record.");
                }
            }
            catch (FormatException)
            {
                throw new PluginManifestException("PACKAGE_HASH_INVALID", "The expected plugin package hash is invalid.");
            }
        }

        return new PluginPackage(root, manifestPath, manifest, hash, snapshot.Scripts);
    }

    private static void Validate(string root, PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.PluginId) || !PluginIdPattern().IsMatch(manifest.PluginId))
            throw new PluginManifestException("PLUGIN_ID_INVALID", "pluginId must be a lowercase reverse-domain style identifier.");
        if (string.IsNullOrWhiteSpace(manifest.Version) || !SemanticVersionPattern().IsMatch(manifest.Version))
            throw new PluginManifestException("PLUGIN_VERSION_INVALID", "version must be a semantic version.");
        if (manifest.ApiVersion != PluginProtocolVersion.Current)
            throw new PluginManifestException("API_VERSION_UNSUPPORTED", $"Unsupported plugin API version {manifest.ApiVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 100)
            throw new PluginManifestException("DISPLAY_NAME_INVALID", "displayName is required and must not exceed 100 characters.");

        _ = ResolveContainedPath(root, manifest.Entry, requireJavaScript: true);
        foreach (var script in manifest.PreloadScripts ?? [])
            _ = ResolveContainedPath(root, script, requireJavaScript: true);

        EnsureUnique(manifest.PreloadScripts, "preloadScripts");
        if (manifest.PreloadScripts!.Contains(manifest.Entry, StringComparer.OrdinalIgnoreCase))
            throw new PluginManifestException("MANIFEST_DUPLICATE_VALUE", "entry must not also appear in preloadScripts.");
        EnsureUnique(manifest.Capabilities, "capabilities");
        foreach (var capability in manifest.Capabilities ?? [])
        {
            if (!PluginManifestRules.SupportedCapabilities.Contains(capability))
                throw new PluginManifestException("CAPABILITY_UNSUPPORTED", $"Unsupported capability: {capability}");
        }

        ValidateHosts(manifest.Network?.AllowedHosts, "network.allowedHosts");
        ValidateHosts(manifest.Network?.AllowRedirectHosts, "network.allowRedirectHosts");

        if (manifest.Auth is not null)
        {
            if (!PluginManifestRules.SupportedAuthKinds.Contains(manifest.Auth.Kind))
                throw new PluginManifestException("AUTH_KIND_UNSUPPORTED", $"Unsupported auth kind: {manifest.Auth.Kind}");
            ValidateHosts(manifest.Auth.CookieDomains, "auth.cookieDomains");
            EnsureUnique(manifest.Auth.AuthSignalCookies, "auth.authSignalCookies");
            EnsureUnique(manifest.Auth.ExposedCookieNames, "auth.exposedCookieNames");
            if (manifest.Auth.LoginFlow is not null)
            {
                ValidateHttpsUrl(manifest.Auth.LoginFlow.LoginUrl, "auth.loginFlow.loginUrl");
                EnsureUnique(manifest.Auth.LoginFlow.SuccessUrlPrefixes, "auth.loginFlow.successUrlPrefixes");
                EnsureUnique(manifest.Auth.LoginFlow.SuccessCookieNames, "auth.loginFlow.successCookieNames");
                foreach (var prefix in manifest.Auth.LoginFlow.SuccessUrlPrefixes)
                    ValidateHttpsUrl(prefix, "auth.loginFlow.successUrlPrefixes");
            }
        }

        if (manifest.Views is null)
            throw new PluginManifestException("MANIFEST_FIELD_MISSING", "views is required.");
        var viewIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in manifest.Views)
        {
            if (string.IsNullOrWhiteSpace(view.Id) || !viewIds.Add(view.Id))
                throw new PluginManifestException("VIEW_ID_INVALID", "View ids must be non-empty and unique.");
            if (!PluginManifestRules.SupportedViewKinds.Contains(view.Kind))
                throw new PluginManifestException("VIEW_KIND_UNSUPPORTED", $"Unsupported view kind: {view.Kind}");
            if (string.IsNullOrWhiteSpace(view.Title))
                throw new PluginManifestException("VIEW_TITLE_INVALID", $"View {view.Id} has no title.");
        }

        foreach (var file in EnumeratePackageFiles(root))
        {
            if (ForbiddenExtensions.Contains(Path.GetExtension(file)))
                throw new PluginManifestException("EXECUTABLE_CONTENT_FORBIDDEN", $"Executable content is not allowed: {Path.GetFileName(file)}");
        }
    }

    private static string ResolveContainedPath(string root, string relativePath, bool requireJavaScript)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new PluginManifestException("PLUGIN_PATH_INVALID", "Plugin paths must be non-empty relative paths.");

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new PluginManifestException("PLUGIN_PATH_TRAVERSAL", $"Plugin path escapes its root: {relativePath}");
        if (requireJavaScript && !string.Equals(Path.GetExtension(fullPath), ".js", StringComparison.OrdinalIgnoreCase))
            throw new PluginManifestException("SCRIPT_EXTENSION_INVALID", $"Plugin script must use the .js extension: {relativePath}");
        if (!File.Exists(fullPath))
            throw new PluginManifestException("PLUGIN_FILE_MISSING", $"Plugin file does not exist: {relativePath}");

        RejectReparsePoint(fullPath);
        RejectReparsePointAncestors(root, fullPath);
        return fullPath;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new PluginManifestException("PLUGIN_REPARSE_POINT_FORBIDDEN", $"Plugin paths may not use links or reparse points: {path}");
    }

    private static void RejectReparsePointAncestors(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static IEnumerable<string> EnumeratePackageFiles(string root)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                RejectReparsePoint(childDirectory);
                pending.Enqueue(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                RejectReparsePoint(file);
                yield return file;
            }
        }
    }

    private static void ValidateHosts(IReadOnlyList<string>? hosts, string field)
    {
        if (hosts is null)
            throw new PluginManifestException("HOST_LIST_MISSING", $"{field} is required.");
        EnsureUnique(hosts, field);
        foreach (var host in hosts)
        {
            if (string.IsNullOrWhiteSpace(host)
                || host.Contains('*', StringComparison.Ordinal)
                || host.Contains('/', StringComparison.Ordinal)
                || host.Contains(':', StringComparison.Ordinal)
                || Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                throw new PluginManifestException("HOST_INVALID", $"{field} contains an invalid or wildcard host: {host}");
            }
        }
    }

    private static void ValidateHttpsUrl(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new PluginManifestException("URL_INVALID", $"{field} must contain an absolute HTTPS URL.");
    }

    private static void EnsureUnique(IReadOnlyList<string>? values, string field)
    {
        if (values is null)
            throw new PluginManifestException("MANIFEST_FIELD_MISSING", $"{field} is required.");
        if (values.Count != values.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new PluginManifestException("MANIFEST_DUPLICATE_VALUE", $"{field} contains duplicate values.");
    }

    private static async Task<PackageSnapshot> CreatePackageSnapshotAsync(
        string root,
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var scriptPaths = (manifest.PreloadScripts ?? []).Append(manifest.Entry)
            .ToDictionary(
                relativePath => ResolveContainedPath(root, relativePath, requireJavaScript: true),
                relativePath => relativePath,
                StringComparer.OrdinalIgnoreCase);
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalScriptBytes = 0;
        foreach (var file in EnumeratePackageFiles(root)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));

            if (scriptPaths.TryGetValue(file, out var declaredPath))
            {
                var info = new FileInfo(file);
                if (info.Length > MaxScriptBytes || totalScriptBytes + info.Length > MaxTotalScriptBytes)
                    throw new PluginManifestException("PLUGIN_SCRIPT_TOO_LARGE", "Plugin scripts exceed the package size limit.");
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                hash.AppendData(bytes);
                totalScriptBytes += bytes.LongLength;
                try
                {
                    scripts.Add(declaredPath, new UTF8Encoding(false, true).GetString(bytes));
                }
                catch (DecoderFallbackException)
                {
                    throw new PluginManifestException("PLUGIN_SCRIPT_ENCODING_INVALID", "Plugin scripts must be valid UTF-8.");
                }
                continue;
            }

            await using var stream = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }

        return new PackageSnapshot(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            scripts);
    }

    private sealed record PackageSnapshot(string PackageHash, IReadOnlyDictionary<string, string> Scripts);

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}
