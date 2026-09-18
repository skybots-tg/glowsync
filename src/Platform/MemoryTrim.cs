using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlowSync.Platform;

/// <summary>
/// After the settings window closes the app goes back to living in the tray; hand the UI's memory back to Windows.
/// </summary>
internal static class MemoryTrim
{
    private static Timer? _timer;

    public static void Schedule(int delayMs = 3000)
    {
        _timer?.Dispose();
        _timer = new Timer(_ => Trim(), null, delayMs, Timeout.Infinite);
    }

    private static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        SetProcessWorkingSetSizeEx(process.Handle, -1, -1, 0);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nint min, nint max, int flags);
}
