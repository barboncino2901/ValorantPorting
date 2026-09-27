using System;
using System.Diagnostics;
using System.Runtime;

namespace ValorantPorting.AppUtils;

public static class MemoryHelper
{
    // Loading a tab decodes thousands of icons into large temporary buffers. .NET frees them but keeps the memory
    // reserved, so after a tab finishes loading we compact once and hand unused memory back to Windows.
    public static void ReleaseAfterLoading(string what)
    {
        var before = Process.GetCurrentProcess().WorkingSet64;

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var process = Process.GetCurrentProcess();
        AppLog.Information($"[Memory] {what}: {ToMb(process.WorkingSet64)} MB in use " +
                           $"({ToMb(GC.GetTotalMemory(false))} MB app data, was {ToMb(before)} MB before cleanup)");
    }

    private static long ToMb(long bytes) => bytes / (1024 * 1024);
}
