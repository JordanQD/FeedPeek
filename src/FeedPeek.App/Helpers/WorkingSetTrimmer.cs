using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace FeedPeek.Helpers;

/// <summary>
/// Post-teardown memory reclaim: managed GC + working-set trim.
/// Call only on infrequent paths (flyout close / page dispose), never per-frame.
/// </summary>
internal static class WorkingSetTrimmer
{
    private static int _scheduled;

    public static void RequestTrim()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            RunTrim();
            Interlocked.Exchange(ref _scheduled, 0);
            return;
        }

        dispatcher.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                try
                {
                    RunTrim();
                }
                finally
                {
                    Interlocked.Exchange(ref _scheduled, 0);
                }
            });
    }

    private static void RunTrim()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        try
        {
            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, (nint)(-1), (nint)(-1));
        }
        catch
        {
            // Best-effort only.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        nint process,
        nint minimumWorkingSetSize,
        nint maximumWorkingSetSize);
}
