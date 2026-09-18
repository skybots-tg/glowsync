using System;
using System.Collections.Generic;
using System.Windows;
using GlowSync.Config;
using GlowSync.Core;
using GlowSync.Device;
using GlowSync.Platform;
using GlowSync.UI;

namespace GlowSync;

/// <summary>Wires config, device, engine, OS events, hotkeys, tray icon and the settings window together.</summary>
internal sealed class AppController : IDisposable
{
    public static readonly EffectMode[] ModeOrder =
    {
        EffectMode.Screen, EffectMode.Music, EffectMode.Rainbow, EffectMode.ColorCycle,
        EffectMode.Breathing, EffectMode.Static, EffectMode.Comet,
    };

    private readonly ConfigStore _store;
    private readonly DeviceManager _device;
    private readonly Engine _engine;
    private readonly SystemEvents _events;
    private readonly TrayIcon _tray;
    private MainWindow? _window;
    private bool _hotkeysSuspended;
    private bool _disposed;

    public ConfigStore Store => _store;
    public DeviceManager Device => _device;
    public Engine Engine => _engine;
    public List<HotkeyAction> HotkeyConflicts { get; private set; } = new();

    /// <summary>Raised on the UI thread when device status or output state changes.</summary>
    public event Action? StatusChanged;

    public AppController()
    {
        _store = new ConfigStore();
        if (_store.IsFirstRun)
        {
            if (SkydimoImporter.IsAvailable)
            {
                try
                {
                    var report = SkydimoImporter.Import(_store.Config);
                    Log.Info("Imported Skydimo settings: " + string.Join("; ", report));
                }
                catch (Exception ex)
                {
                    Log.Error("Skydimo import failed", ex);
                }
            }
            _store.Config.Normalize();
            Autostart.Set(_store.Config.Behavior.Autostart);
            _store.SaveNow();
        }

        _device = new DeviceManager(_store);
        _engine = new Engine(_store, _device);
        _events = new SystemEvents();
        _tray = new TrayIcon(this);

        _store.Changed += OnConfigChanged;
        _store.Replaced += OnConfigReplaced;
        _device.StatusChanged += _ => Ui(() => StatusChanged?.Invoke());
        _engine.ActiveChanged += _ => Ui(() => StatusChanged?.Invoke());

        _events.DisplayPowerChanged += (signal, on) => _engine.SetDisplayOff(signal, !on);
        _events.SessionLockChanged += locked => _engine.SetLocked(locked);
        _events.Suspending += () =>
        {
            Log.Info("System suspending");
            _engine.Suspend();
            _device.Suspend();
        };
        _events.Resumed += () =>
        {
            Log.Info("System resumed");
            _device.Resume();
            _engine.Resume();
        };
        _events.DevicesChanged += () => _device.RequestRescan();
        _events.DisplayModeChanged += () => _engine.RequestCaptureReset();
        _events.SessionEnding += () =>
        {
            Log.Info("Session ending");
            _engine.Suspend();
            _device.Suspend();
            _store.SaveNow();
        };
        _events.HotkeyPressed += OnHotkey;
        StatusChanged += () => _tray.Refresh();
    }

    public void Start()
    {
        ApplyHotkeys();
        _device.Start();
        _engine.Start();
        _tray.Refresh();
        MemoryTrim.Schedule(15_000);
    }

    public void ShowSettings()
    {
        if (_window == null)
        {
            EnsureTheme();
            _window = new MainWindow(this);
            _window.Closed += (_, _) =>
            {
                _window = null;
                MemoryTrim.Schedule();
            };
            _window.Show();
        }
        else
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Show();
        }
        _window.Activate();
    }

    private static void EnsureTheme()
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > 0) return;
        dictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        dictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
    }

    public void ToggleEnabled() => _store.Config.Enabled = !_store.Config.Enabled;

    public void SetMode(EffectMode mode)
    {
        _store.Config.Mode = mode;
        _store.Config.Enabled = true;
    }

    public void StepMode(int direction)
    {
        int i = Array.IndexOf(ModeOrder, _store.Config.Mode);
        int next = ((i < 0 ? 0 : i) + direction + ModeOrder.Length) % ModeOrder.Length;
        SetMode(ModeOrder[next]);
    }

    public void StepBrightness(int delta)
    {
        var cfg = _store.Config;
        int value = cfg.Brightness;
        // Finer steps at the dark end where the eye is most sensitive.
        int step = (delta > 0 ? value < 10 : value <= 10) ? Math.Sign(delta) * 2 : delta;
        cfg.Brightness = Math.Clamp(value + step, 0, 100);
        if (cfg.Brightness > 0) cfg.Enabled = true;
    }

    public void SuspendHotkeys(bool suspend)
    {
        _hotkeysSuspended = suspend;
        if (suspend) _events.UnregisterHotkeys();
        else ApplyHotkeys();
    }

    public void ApplyHotkeys()
    {
        if (_hotkeysSuspended) return;
        HotkeyConflicts = _events.RegisterHotkeys(_store.Config.Hotkeys);
    }

    public List<string> ImportSkydimo()
    {
        var cfg = ConfigStore.Clone(_store.Config);
        var report = SkydimoImporter.Import(cfg);
        _store.Replace(cfg);
        Log.Info("Imported Skydimo settings: " + string.Join("; ", report));
        return report;
    }

    public void Exit()
    {
        _window?.Close();
        Application.Current.Shutdown();
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Toggle: ToggleEnabled(); break;
            case HotkeyAction.BrightnessUp: StepBrightness(10); break;
            case HotkeyAction.BrightnessDown: StepBrightness(-10); break;
            case HotkeyAction.NextMode: StepMode(1); break;
            case HotkeyAction.PrevMode: StepMode(-1); break;
        }
        if (_store.Config.Behavior.ShowOsd) Osd.Show(action, _store.Config);
    }

    private void OnConfigChanged(object sender, string property)
    {
        _engine.Wake();
        if (sender is HotkeyBinding) ApplyHotkeys();
        else if (sender is BehaviorConfig && property == nameof(BehaviorConfig.Autostart)) Autostart.Set(_store.Config.Behavior.Autostart);
        else if (sender is DeviceConfig) _device.RequestRescan();
        if (sender is AppConfig) Ui(() => _tray.Refresh());
    }

    private void OnConfigReplaced()
    {
        ApplyHotkeys();
        Autostart.Set(_store.Config.Behavior.Autostart);
        _device.RequestRescan();
        _engine.Wake();
        _tray.Refresh();
    }

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.Dispose();
        _events.Dispose();
        _engine.Dispose();
        _device.Dispose(); // sends a final black frame
        _store.Dispose();
    }
}
