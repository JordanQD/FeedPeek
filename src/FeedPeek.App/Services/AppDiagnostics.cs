using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FeedPeek.Services;

/// <summary>Small, synchronous breadcrumbs that also survive Release builds.</summary>
internal static class AppDiagnostics
{
    private static DiagnosticLog? _log;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Each process owns its files, including secondary activation instances.
            // Keep nine older groups plus the new process log.
            var path = Path.Combine(directory, $"runtime-{Environment.ProcessId}.log");
            foreach (var old in new DirectoryInfo(directory).GetFiles("runtime-*.log")
                         .Where(file => !file.Name.EndsWith(".previous.log", StringComparison.Ordinal)
                             && !string.Equals(file.FullName, path, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(9))
            {
                File.Delete(Path.ChangeExtension(old.FullName, ".previous.log"));
                old.Delete();
            }

            _log = new DiagnosticLog(path);
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            Write("Process.Start", $"build={configuration}; version={typeof(AppDiagnostics).Assembly.GetName().Version}; "
                + $"module={typeof(AppDiagnostics).Module.ModuleVersionId}; "
                + $"runtime={Environment.Version}; os={Environment.OSVersion.VersionString}; "
                + $"base={AppContext.BaseDirectory}; log={path}");
        }
        catch
        {
            // A diagnostic file failure must not prevent startup.
        }
    }

    public static void Write(string name, string? detail = null)
        => _log?.Write(name, detail);

    public static void Exception(string name, Exception exception)
        => Write(name, exception.ToString());
}

internal sealed class DiagnosticLog(string path)
{
    internal const int MaximumBytes = 1024 * 1024;
    private const int MaximumDetailCharacters = 32 * 1024;
    private readonly object _gate = new();

    public void Write(string name, string? detail = null)
    {
        try
        {
            if (detail?.Length > MaximumDetailCharacters)
            {
                detail = detail[..MaximumDetailCharacters] + " [truncated]";
            }

            var line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} "
                + $"{name}{(detail is null ? string.Empty : ": " + detail)}{Environment.NewLine}";
            Debug.WriteLine(line);
            lock (_gate)
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length >= MaximumBytes)
                {
                    File.Move(path, Path.ChangeExtension(path, ".previous.log"), overwrite: true);
                }

                // Close on each write so fatal-exception breadcrumbs do not sit in a queue.
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Best effort: preserve the application's original exception/exit behavior.
        }
    }
}
