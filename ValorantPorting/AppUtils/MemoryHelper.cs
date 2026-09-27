using System;
using System.Runtime;

namespace ValorantPorting.AppUtils;

public static class MemoryHelper
{
    // Loading a tab (or exporting) decodes large temporary buffers. .NET frees them but keeps the memory
    // reserved, so afterwards we compact once and hand unused memory back to Windows.
    public static void ReleaseAfterLoading(string what)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
