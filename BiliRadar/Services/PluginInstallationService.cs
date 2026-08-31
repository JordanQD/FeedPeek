using FeedPeek.PluginContracts.Manifest;
using FeedPeek.PluginRuntime.Discovery;
using FeedPeek.PluginRuntime.Manifest;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace BiliRadar.Services;

internal sealed record PluginInstallationRecord(
    string PluginId,
    string Version,
    string PackageHash,
    string DirectoryName,
    bool IsEnabled,
    DateTimeOffset InstalledAt);

internal sealed record PluginManagementEntry(
    string PluginId,
    string DisplayName,
    string Version,
    string DirectoryPath,
    bool IsBuiltIn,
    bool IsEnabled,
    PluginManifest? Manifest,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record PluginLoadConfiguration(
    IReadOnlyList<string> Roots,
    IReadOnlySet<string> DisabledPluginIds,
    IReadOnlyDictionary<string, string> ExpectedPackageHashes);

internal sealed class PluginInstallationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly PluginManifestLoader _loader = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string BuiltInRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Plugins");

    public string UserRoot { get; } = Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "FeedPeek",
        "Plugins");

    private string StatePath => Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "FeedPeek",
        "plugin-installations.v1.json");

    public async Task<PluginLoadConfiguration> GetLoadConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        var recordedIds = records.Select(record => record.PluginId).ToHashSet(StringComparer.Ordinal);
        var disabledIds = records.Where(record => !record.IsEnabled)
            .Select(record => record.PluginId)
            .ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(UserRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(UserRoot)
                .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
            {
                try
                {
                    var package = await _loader.LoadAsync(directory, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (!recordedIds.Contains(package.Manifest.PluginId))
                        disabledIds.Add(package.Manifest.PluginId);
                }
                catch (PluginManifestException)
                {
                    // Discovery reports the validation error; unrecorded packages never become active.
                }
            }
        }
        return new PluginLoadConfiguration(
            [BuiltInRoot, UserRoot],
            disabledIds,
            records.ToDictionary(
                record => record.PluginId,
                record => record.PackageHash,
                StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<PluginManagementEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        var recordMap = records.ToDictionary(record => record.PluginId, StringComparer.Ordinal);
        var discovery = new PluginDiscoveryService(_loader);
        var results = await discovery.DiscoverAsync(
            [BuiltInRoot, UserRoot],
            records.Where(record => !record.IsEnabled)
                .Select(record => record.PluginId)
                .ToHashSet(StringComparer.Ordinal),
            records.ToDictionary(record => record.PluginId, record => record.PackageHash, StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);

        return results.Select(result =>
        {
            var package = result.Package;
            var pluginId = package?.Manifest.PluginId
                ?? Path.GetFileName(result.DirectoryPath);
            recordMap.TryGetValue(pluginId, out var record);
            var isBuiltIn = IsContainedBy(BuiltInRoot, result.DirectoryPath);
            return new PluginManagementEntry(
                pluginId,
                package?.Manifest.DisplayName ?? pluginId,
                package?.Manifest.Version ?? record?.Version ?? string.Empty,
                result.DirectoryPath,
                isBuiltIn,
                isBuiltIn || record?.IsEnabled == true,
                package?.Manifest,
                result.ErrorCode,
                result.ErrorMessage);
        }).ToArray();
    }

    public async Task<PluginManagementEntry> InstallAsync(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var sourcePackage = await _loader.LoadAsync(sourceDirectory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var builtInIds = await GetBuiltInPluginIdsAsync(cancellationToken).ConfigureAwait(false);
        if (builtInIds.Contains(sourcePackage.Manifest.PluginId))
            throw new InvalidOperationException("不能用用户插件覆盖随应用发布的内置插件。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(UserRoot);
            var stagingName = $".staging-{Guid.NewGuid():N}";
            var stagingPath = ResolveUserPath(stagingName);
            var targetPath = ResolveUserPath(sourcePackage.Manifest.PluginId);
            var backupPath = ResolveUserPath($".backup-{Guid.NewGuid():N}");
            try
            {
                CopyDirectory(sourcePackage.RootPath, stagingPath);
                var stagedPackage = await _loader.LoadAsync(stagingPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                    stagedPackage.Manifest.PluginId,
                    sourcePackage.Manifest.PluginId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("复制后的插件标识发生变化。");
                }

                if (Directory.Exists(targetPath))
                    Directory.Move(targetPath, backupPath);
                try
                {
                    Directory.Move(stagingPath, targetPath);
                    var records = await ReadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
                    records.RemoveAll(record => string.Equals(
                        record.PluginId,
                        stagedPackage.Manifest.PluginId,
                        StringComparison.Ordinal));
                    records.Add(new PluginInstallationRecord(
                        stagedPackage.Manifest.PluginId,
                        stagedPackage.Manifest.Version,
                        stagedPackage.PackageHash,
                        stagedPackage.Manifest.PluginId,
                        IsEnabled: true,
                        DateTimeOffset.UtcNow));
                    await WriteRecordsCoreAsync(records, cancellationToken).ConfigureAwait(false);
                    if (Directory.Exists(backupPath))
                        Directory.Delete(backupPath, recursive: true);
                }
                catch
                {
                    if (Directory.Exists(targetPath))
                        Directory.Delete(targetPath, recursive: true);
                    if (Directory.Exists(backupPath))
                        Directory.Move(backupPath, targetPath);
                    throw;
                }
                return new PluginManagementEntry(
                    stagedPackage.Manifest.PluginId,
                    stagedPackage.Manifest.DisplayName,
                    stagedPackage.Manifest.Version,
                    targetPath,
                    IsBuiltIn: false,
                    IsEnabled: true,
                    stagedPackage.Manifest,
                    null,
                    null);
            }
            finally
            {
                if (Directory.Exists(stagingPath))
                    Directory.Delete(stagingPath, recursive: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(
        string pluginId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(record => string.Equals(record.PluginId, pluginId, StringComparison.Ordinal));
            if (index < 0)
                throw new InvalidOperationException("只能启用或停用用户安装的插件。");
            records[index] = records[index] with { IsEnabled = isEnabled };
            await WriteRecordsCoreAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UninstallAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
            var record = records.FirstOrDefault(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("找不到该用户插件的安装记录。");
            var targetPath = ResolveUserPath(record.DirectoryName);
            if (Directory.Exists(targetPath))
                Directory.Delete(targetPath, recursive: true);
            records.Remove(record);
            await WriteRecordsCoreAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HashSet<string>> GetBuiltInPluginIdsAsync(CancellationToken cancellationToken)
    {
        var results = await new PluginDiscoveryService(_loader)
            .DiscoverAsync(BuiltInRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return results.Where(result => result.Package is not null)
            .Select(result => result.Package!.Manifest.PluginId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<PluginInstallationRecord>> ReadRecordsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PluginInstallationRecord>> ReadRecordsCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
            return [];
        try
        {
            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<List<PluginInstallationRecord>>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteRecordsCoreAsync(
        IReadOnlyList<PluginInstallationRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = StatePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temporaryPath, StatePath, overwrite: true);
    }

    private string ResolveUserPath(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)
            || Path.IsPathRooted(directoryName)
            || directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("插件目录名称无效。");
        }
        var root = Path.GetFullPath(UserRoot);
        var path = Path.GetFullPath(Path.Combine(root, directoryName));
        if (!IsContainedBy(root, path))
            throw new InvalidOperationException("插件目录超出用户插件根目录。");
        return path;
    }

    private static bool IsContainedBy(string root, string path)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
