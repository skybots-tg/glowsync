using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using GlowSync.Config;

namespace GlowSync.Platform;

/// <summary>
/// Hidden top-level window that receives what Windows only broadcasts to windows:
/// display on/off, sleep/resume, lock/unlock, device arrival, display mode changes, logoff and global hotkeys.
/// </summary>
/// <summary>Display-state signals Windows can send, in the order we trust them.</summary>
internal enum DisplaySignal { Console, Session, MonitorPower }

internal sealed class SystemEvents : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218, PBT_APMSUSPEND = 0x4, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12, PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WM_WTSSESSION_CHANGE = 0x02B1, WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    private const int WM_DEVICECHANGE = 0x0219, DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int WM_DISPLAYCHANGE = 0x007E, WM_HOTKEY = 0x0312, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
    private const int MOD_NOREPEAT = 0x4000;

    private static Guid _consoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static Guid _sessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private static Guid _monitorPowerOn = new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    private readonly System.Windows.Interop.HwndSource _source;
    private readonly List<IntPtr> _powerNotifications = new();
    private readonly DispatcherTimer _deviceDebounce;
    private readonly List<int> _hotkeyIds = new();

    /// <summary>Signal source and whether the display is on.</summary>
    public event Action<DisplaySignal, bool>? DisplayPowerChanged;
    public event Action? Suspending;
    public event Action? Resumed;
    public event Action<bool>? SessionLockChanged;   // true = locked
    public event Action? DevicesChanged;
    public event Action? DisplayModeChanged;
    public event Action? SessionEnding;
    public event Action<HotkeyAction>? HotkeyPressed;

    public SystemEvents()
    {
        var p = new System.Windows.Interop.HwndSourceParameters("GlowSync.SystemEvents")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // WS_OVERLAPPED, never shown; not HWND_MESSAGE so broadcasts arrive
        };
        _source = new System.Windows.Interop.HwndSource(p);
        _source.AddHook(WndProc);

        // Different machines report the display state through different settings, so listen to all three.
        Register(ref _consoleDisplayState);
        Register(ref _sessionDisplayStatus);
        Register(ref _monitorPowerOn);
        WTSRegisterSessionNotification(_source.Handle, 0);

        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _deviceDebounce.Tick += (_, _) =>
        {
            _deviceDebounce.Stop();
            DevicesChanged?.Invoke();
        };
    }

    private void Register(ref Guid setting)
    {
        var handle = RegisterPowerSettingNotification(_source.Handle, ref setting, 0);
        if (handle != IntPtr.Zero) _powerNotifications.Add(handle);
        else Log.Warn($"RegisterPowerSettingNotification failed for {setting}");
    }

    /// <summary>Registers global hotkeys. Returns the actions whose key combination is taken by another program.</summary>
    public List<HotkeyAction> RegisterHotkeys(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterHotkeys();
        var failed = new List<HotkeyAction>();
        foreach (var b in bindings)
        {
            if (b.Key == 0) continue;
            int id = 0x100 + (int)b.Action;
            if (RegisterHotKey(_source.Handle, id, b.Modifiers | MOD_NOREPEAT, b.Key)) _hotkeyIds.Add(id);
            else failed.Add(b.Action);
        }
        if (failed.Count > 0) Log.Warn("Hotkeys already in use: " + string.Join(", ", failed));
        return failed;
    }

    public void UnregisterHotkeys()
    {
        foreach (var id in _hotkeyIds) UnregisterHotKey(_source.Handle, id);
        _hotkeyIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_POWERBROADCAST:
                switch (wParam.ToInt32())
                {
                    case PBT_POWERSETTINGCHANGE:
                        OnPowerSettingChange(lParam);
                        break;
                    case PBT_APMSUSPEND:
                        Suspending?.Invoke();
                        break;
                    case PBT_APMRESUMEAUTOMATIC:
                    case PBT_APMRESUMESUSPEND:
                        Resumed?.Invoke();
                        break;
                }
                handled = true;
                return new IntPtr(1);

            case WM_WTSSESSION_CHANGE:
                if (wParam.ToInt32() == WTS_SESSION_LOCK) SessionLockChanged?.Invoke(true);
                else if (wParam.ToInt32() == WTS_SESSION_UNLOCK) SessionLockChanged?.Invoke(false);
                break;

            case WM_DEVICECHANGE:
                int evt = wParam.ToInt32();
                if (evt is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
                {
                    _deviceDebounce.Stop();
                    _deviceDebounce.Start();
                }
                break;

            case WM_DISPLAYCHANGE:
                DisplayModeChanged?.Invoke();
                break;

            case WM_HOTKEY:
                int hotkey = wParam.ToInt32() - 0x100;
                if (Enum.IsDefined(typeof(HotkeyAction), hotkey)) HotkeyPressed?.Invoke((HotkeyAction)hotkey);
                handled = true;
                break;

            case WM_QUERYENDSESSION:
                handled = true;
                return new IntPtr(1);

            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero) SessionEnding?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// POWERBROADCAST_SETTING carries a single data byte after the 16-byte GUID and the 4-byte length.
    /// Reading it as a 32-bit value pulls in whatever follows the buffer, which used to make an "off"
    /// look like "on" — hence the explicit byte read.
    /// </summary>
    private void OnPowerSettingChange(IntPtr lParam)
    {
        var header = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
        if (header.DataLength < 1) return;
        int value = Marshal.ReadByte(lParam, 20);

        DisplaySignal signal;
        if (header.PowerSetting == _consoleDisplayState) signal = DisplaySignal.Console;
        else if (header.PowerSetting == _sessionDisplayStatus) signal = DisplaySignal.Session;
        else if (header.PowerSetting == _monitorPowerOn) signal = DisplaySignal.MonitorPower;
        else return;

        // 0 = off, 1 = on, 2 = dimmed (still visible).
        DisplayPowerChanged?.Invoke(signal, value != 0);
    }

    public void Dispose()
    {
        UnregisterHotkeys();
        _deviceDebounce.Stop();
        foreach (var handle in _powerNotifications) UnregisterPowerSettingNotification(handle);
        _powerNotifications.Clear();
        WTSUnRegisterSessionNotification(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
