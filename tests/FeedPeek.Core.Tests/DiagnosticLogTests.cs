using FeedPeek.Services;

namespace FeedPeek.Core.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FeedPeek-Diagnostics-" + Guid.NewGuid().ToString("N"));

    public DiagnosticLogTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void RotationRetainsLastFileAndPersistsTheNewEventImmediately()
    {
        var path = Path.Combine(_directory, "runtime-test.log");
        var previous = Path.ChangeExtension(path, ".previous.log");
        File.WriteAllText(previous, "older rotation");
        var oldLog = new string('x', DiagnosticLog.MaximumBytes);
        File.WriteAllText(path, oldLog);

        new DiagnosticLog(path).Write("Exception.Test", "failure detail");

        Assert.Equal(oldLog, File.ReadAllText(previous));
        var current = File.ReadAllText(path);
        Assert.Contains("Exception.Test: failure detail", current);
        Assert.Contains($"pid={Environment.ProcessId}", current);
        Assert.Contains("tid=", current);
    }

    [Fact]
    public void StartupRetainsOnlyNineOlderLogGroupsAlongsideCurrentProcess()
    {
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(_directory, $"runtime-{1_000_000 + index}.log");
            File.WriteAllText(path, "previous process");
            File.WriteAllText(Path.ChangeExtension(path, ".previous.log"), "previous rotation");
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMinutes(index));
        }

        AppDiagnostics.Initialize(_directory);

        Assert.Equal(10, Directory.GetFiles(_directory, "runtime-*.log")
            .Count(path => !path.EndsWith(".previous.log", StringComparison.Ordinal)));
        Assert.Equal(9, Directory.GetFiles(_directory, "*.previous.log").Length);
        Assert.False(File.Exists(Path.Combine(_directory, "runtime-1000000.log")));
        var current = File.ReadAllText(Path.Combine(_directory, $"runtime-{Environment.ProcessId}.log"));
        Assert.Contains("Process.Start", current);
        Assert.Contains("module=", current);
    }

    [Fact]
    public void UnwritableDestinationDoesNotReplaceTheOriginalFailure()
    {
        // A file used as a parent directory deterministically rejects writes.
        var blockedDirectory = Path.Combine(_directory, "blocked");
        File.WriteAllText(blockedDirectory, "file");
        var log = new DiagnosticLog(Path.Combine(blockedDirectory, "runtime.log"));

        var failure = Record.Exception(() => log.Write("Exception.Test", "original failure"));

        Assert.Null(failure);
        Assert.Equal("file", File.ReadAllText(blockedDirectory));
    }

    [Fact]
    public void ExceptionDetailsAreRecordedAndOversizedMessagesAreBounded()
    {
        var path = Path.Combine(_directory, "runtime-test.log");
        var log = new DiagnosticLog(path);
        log.Write("Exception.Test", new InvalidOperationException("diagnostic detail").ToString());
        log.Write("Exception.Oversized", new string('x', 100_000));

        var content = File.ReadAllText(path);
        Assert.Contains("System.InvalidOperationException: diagnostic detail", content);
        Assert.Contains("[truncated]", content);
        Assert.True(new FileInfo(path).Length < 40_000);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
