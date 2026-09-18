using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GlowSync.Platform;

/// <summary>
/// Sub-millisecond sleep via a high-resolution waitable timer (Windows 10 1803+),
/// interruptible by <see cref="Wake"/>. Avoids the 15.6 ms granularity of Thread.Sleep.
/// </summary>
internal sealed class FrameTimer : IDisposable
{
    private const uint HighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly TimerHandle? _timer;
    private readonly AutoResetEvent _wake = new(false);
    private readonly WaitHandle[]? _handles;

    public FrameTimer()
    {
        var h = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess);
        if (h.IsInvalid) h = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        if (!h.IsInvalid)
        {
            _timer = new TimerHandle { SafeWaitHandle = h };
            _handles = new WaitHandle[] { _timer, _wake };
        }
    }

    public void Wake() => _wake.Set();

    /// <summary>Sleeps for the given time or until woken. Returns true when woken early.</summary>
    public bool Sleep(double seconds)
    {
        if (seconds <= 0) return false;
        if (_timer != null)
        {
            long due = -(long)(seconds * 10_000_000);
            if (SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                return WaitHandle.WaitAny(_handles!) == 1;
            }
        }
        return _wake.WaitOne(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _wake.Dispose();
    }

    private sealed class TimerHandle : WaitHandle
    {
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
}
