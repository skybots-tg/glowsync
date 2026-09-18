using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlowSync.Platform;

/// <summary>
/// Asks the monitor itself whether it is switched on, over the DDC/CI channel in the video cable
/// (VCP feature 0xD6). Windows does not report a monitor that the user switches off by its own
/// button — the desktop keeps rendering — so this is the only way to notice that case.
/// </summary>
internal sealed class MonitorPowerWatcher : IDisposable
{
    private const byte VcpPowerMode = 0xD6;
    private const int FailuresBeforeOff = 3;

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Tracker _tracker = new();
    private volatile bool _stop;
    private volatile bool _enabled;
    private volatile bool _paused;
    private TimeSpan _interval = TimeSpan.FromSeconds(2);

    public MonitorPowerWatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "GlowSync.MonitorPower" };
    }

    /// <summary>True when the monitor reports that it is off; null while unknown or DDC/CI is unavailable.</summary>
    public bool? IsOff => _tracker.IsOff;

    /// <summary>Last raw VCP value, for the settings window ("монитор отвечает: включён").</summary>
    public int LastValue { get; private set; } = -1;

    public bool Supported => _tracker.EverAnswered;

    public void Start() => _thread.Start();

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) _tracker.Reset();
        _wake.Set();
    }

    /// <summary>Stops polling while the machine sleeps (DDC calls would block on a dead link).</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused) _tracker.Reset();
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            if (!_enabled || _paused)
            {
                _wake.WaitOne(1000);
                continue;
            }

            int? value = null;
            try
            {
                value = QueryPowerMode();
            }
            catch (Exception ex)
            {
                Log.Warn("DDC/CI query failed: " + ex.Message);
            }

            bool wasOff = _tracker.IsOff == true;
            _tracker.Report(value);
            if (value is { } v) LastValue = v;
            if (_tracker.IsOff == true != wasOff)
            {
                Log.Info(_tracker.IsOff == true
                    ? $"Monitor reports power off (DDC value {(value?.ToString() ?? "no answer")})"
                    : $"Monitor reports power on (DDC value {value})");
            }

            _wake.WaitOne(_interval);
        }
    }

    /// <summary>1 = on, 2 = standby, 3 = suspend, 4 = off, 5 = hard off; null = no answer.</summary>
    private static int? QueryPowerMode()
    {
        int? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) return true;
            var monitors = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return true;
            try
            {
                foreach (var physical in monitors)
                {
                    if (GetVCPFeatureAndVCPFeatureReply(physical.Handle, VcpPowerMode, out _, out uint current, out _))
                    {
                        // With several monitors, any monitor that is on keeps the strip running.
                        int value = (int)current;
                        result = result == null || value == 1 ? value : result;
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(3000);
        _wake.Dispose();
    }

    /// <summary>
    /// Turns a stream of answers into an on/off state: only a monitor that answered at least once
    /// can ever be considered "off", so a display without DDC/CI never blanks the strip by accident,
    /// and a single dropped reply does not either.
    /// </summary>
    internal sealed class Tracker
    {
        private int _failures;
        private bool _off;

        public bool EverAnswered { get; private set; }

        public bool? IsOff => EverAnswered ? _off : null;

        public void Report(int? vcpValue)
        {
            if (vcpValue is { } value)
            {
                EverAnswered = true;
                _failures = 0;
                _off = value != 1;
            }
            else if (EverAnswered && ++_failures >= FailuresBeforeOff)
            {
                // A monitor switched off by its button usually stops answering altogether.
                _off = true;
            }
        }

        public void Reset()
        {
            _failures = 0;
            _off = false;
            EverAnswered = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
}
