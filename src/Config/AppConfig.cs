using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GlowSync.Config;

public enum EffectMode { Screen, Music, Rainbow, ColorCycle, Breathing, Static, Comet }

public enum MusicStyle { Spectrum, Pulse, Meter }

public enum StartCorner { BottomLeft, TopLeft, TopRight, BottomRight }

public enum DeviceProtocol { Skydimo, Adalight }

public enum HotkeyAction { Toggle, BrightnessUp, BrightnessDown, NextMode, PrevMode }

public sealed class LedPoint
{
    public int X { get; set; }
    public int Y { get; set; }

    public LedPoint() { }

    public LedPoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

public sealed partial class AppConfig : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private EffectMode _mode = EffectMode.Screen;
    [ObservableProperty] private int _brightness = 100;

    public int Version { get; set; } = 1;
    public string? ImportedFrom { get; set; }

    public DeviceConfig Device { get; set; } = new();
    public LayoutConfig Layout { get; set; } = new();
    public ScreenConfig Screen { get; set; } = new();
    public ColorConfig Color { get; set; } = new();
    public EffectsConfig Effects { get; set; } = new();
    public BehaviorConfig Behavior { get; set; } = new();
    public NightConfig Night { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = HotkeyBinding.CreateDefaults();

    /// <summary>Fixes values that are missing or out of range after loading a (possibly hand-edited) file.</summary>
    public void Normalize()
    {
        Device ??= new DeviceConfig();
        Layout ??= new LayoutConfig();
        Screen ??= new ScreenConfig();
        Color ??= new ColorConfig();
        Effects ??= new EffectsConfig();
        Behavior ??= new BehaviorConfig();
        Night ??= new NightConfig();

        Brightness = Math.Clamp(Brightness, 0, 100);
        if (!Enum.IsDefined(Mode)) Mode = EffectMode.Screen;

        Device.LedCount = Math.Clamp(Device.LedCount, 1, 1000);
        Device.BaudRate = Device.BaudRate <= 0 ? 115200 : Device.BaudRate;
        if (string.IsNullOrWhiteSpace(Device.Port)) Device.Port = DeviceConfig.AutoPort;

        Screen.Fps = Math.Clamp(Screen.Fps, 10, 60);
        Screen.SmoothingMs = Math.Clamp(Screen.SmoothingMs, 0, 1000);
        Screen.Saturation = Math.Clamp(Screen.Saturation, -100, 100);
        Screen.Gamma = Math.Clamp(Screen.Gamma, 1.0, 3.0);
        Screen.Depth = Math.Clamp(Screen.Depth, 0.5, 6.0);
        Screen.SpatialBlur = Math.Clamp(Screen.SpatialBlur, 0, 100);
        Screen.BlackThreshold = Math.Clamp(Screen.BlackThreshold, 0, 30);

        Color.RedGain = Math.Clamp(Color.RedGain, 0, 100);
        Color.GreenGain = Math.Clamp(Color.GreenGain, 0, 100);
        Color.BlueGain = Math.Clamp(Color.BlueGain, 0, 100);

        Night.Brightness = Math.Clamp(Night.Brightness, 0, 100);
        Behavior.FadeMs = Math.Clamp(Behavior.FadeMs, 0, 3000);

        Hotkeys ??= HotkeyBinding.CreateDefaults();
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            if (!Hotkeys.Exists(h => h.Action == action)) Hotkeys.Add(new HotkeyBinding { Action = action });
        }

        Layout.Normalize();
    }
}

public sealed partial class DeviceConfig : ObservableObject
{
    public const string AutoPort = "auto";

    [ObservableProperty] private string _port = AutoPort;
    [ObservableProperty] private DeviceProtocol _protocol = DeviceProtocol.Skydimo;
    [ObservableProperty] private int _baudRate = 115200;

    /// <summary>LED count used when the device does not report its model (plain Adalight).</summary>
    [ObservableProperty] private int _ledCount = 65;

    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? LastPort { get; set; }
}

public sealed partial class LayoutConfig : ObservableObject
{
    [ObservableProperty] private int _left = 17;
    [ObservableProperty] private int _top = 31;
    [ObservableProperty] private int _right = 17;
    [ObservableProperty] private int _bottom;
    [ObservableProperty] private StartCorner _start = StartCorner.BottomLeft;
    [ObservableProperty] private bool _clockwise = true;
    [ObservableProperty] private int _offset;

    /// <summary>Capture grid the LED coordinates live in.</summary>
    [ObservableProperty] private int _gridWidth = 31;
    [ObservableProperty] private int _gridHeight = 17;

    /// <summary>LED positions in strip order. Replaced as a whole, never mutated in place.</summary>
    [ObservableProperty] private List<LedPoint> _leds = new();

    public void Normalize()
    {
        Left = Math.Clamp(Left, 0, 300);
        Top = Math.Clamp(Top, 0, 300);
        Right = Math.Clamp(Right, 0, 300);
        Bottom = Math.Clamp(Bottom, 0, 300);
        Leds ??= new List<LedPoint>();
        if (Leds.Count == 0 || GridWidth < 1 || GridHeight < 1)
        {
            Core.LayoutBuilder.Apply(this);
        }
    }
}

public sealed partial class ScreenConfig : ObservableObject
{
    /// <summary>DXGI device name like \\.\DISPLAY1; empty = primary monitor.</summary>
    [ObservableProperty] private string _monitor = "";
    [ObservableProperty] private int _fps = 30;
    [ObservableProperty] private int _smoothingMs = 60;
    [ObservableProperty] private int _saturation;
    [ObservableProperty] private double _gamma = 2.2;
    [ObservableProperty] private double _depth = 1.0;
    [ObservableProperty] private int _spatialBlur;
    [ObservableProperty] private bool _removeBlackBars;

    /// <summary>Percent; zones darker than this are sent as fully off (kills dim flicker in dark scenes).</summary>
    [ObservableProperty] private int _blackThreshold = 2;
}

public sealed partial class ColorConfig : ObservableObject
{
    [ObservableProperty] private int _redGain = 100;
    [ObservableProperty] private int _greenGain = 100;
    [ObservableProperty] private int _blueGain = 100;
}

public sealed partial class EffectsConfig : ObservableObject
{
    [ObservableProperty] private string _staticColor = "#FF7A30";

    [ObservableProperty] private int _rainbowSpeed = 35;
    [ObservableProperty] private double _rainbowScale = 1.0;
    [ObservableProperty] private bool _rainbowReverse;

    [ObservableProperty] private int _cycleSpeed = 25;

    [ObservableProperty] private string _breathingColor = "#3A7BFF";
    [ObservableProperty] private int _breathingSpeed = 40;
    [ObservableProperty] private bool _breathingRainbow;

    [ObservableProperty] private string _cometColor = "#00E5FF";
    [ObservableProperty] private int _cometSpeed = 45;
    [ObservableProperty] private int _cometLength = 25;
    [ObservableProperty] private bool _cometRainbow = true;

    [ObservableProperty] private MusicStyle _musicStyle = MusicStyle.Spectrum;
    [ObservableProperty] private int _musicSensitivity = 60;
    [ObservableProperty] private int _musicSmoothing = 40;
    [ObservableProperty] private bool _musicRainbow = true;
    [ObservableProperty] private string _musicColor = "#FF2E88";
}

public sealed partial class BehaviorConfig : ObservableObject
{
    [ObservableProperty] private bool _autostart = true;
    [ObservableProperty] private bool _offWhenDisplayOff = true;
    [ObservableProperty] private bool _offWhenLocked;

    /// <summary>Ask the monitor over DDC/CI whether it is switched on (catches the monitor's own power button).</summary>
    [ObservableProperty] private bool _detectMonitorViaDdc = true;
    [ObservableProperty] private int _fadeMs = 400;
    [ObservableProperty] private bool _showOsd = true;
}

public sealed partial class NightConfig : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _from = "23:00";
    [ObservableProperty] private string _to = "07:00";
    [ObservableProperty] private int _brightness = 30;

    public bool IsActive(DateTime now)
    {
        if (!Enabled || !TimeSpan.TryParse(From, out var from) || !TimeSpan.TryParse(To, out var to)) return false;
        var t = now.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }
}

public sealed partial class HotkeyBinding : ObservableObject
{
    public const int ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    [ObservableProperty] private HotkeyAction _action;

    /// <summary>MOD_* flags as used by RegisterHotKey.</summary>
    [ObservableProperty] private int _modifiers;

    /// <summary>Windows virtual-key code; 0 means not assigned.</summary>
    [ObservableProperty] private int _key;

    public static List<HotkeyBinding> CreateDefaults()
    {
        const int mods = ModControl | ModAlt | ModShift;
        return new List<HotkeyBinding>
        {
            new() { Action = HotkeyAction.Toggle, Modifiers = mods, Key = 0x79 },         // F10
            new() { Action = HotkeyAction.BrightnessUp, Modifiers = mods, Key = 0x26 },   // Up
            new() { Action = HotkeyAction.BrightnessDown, Modifiers = mods, Key = 0x28 }, // Down
            new() { Action = HotkeyAction.NextMode, Modifiers = mods, Key = 0x27 },       // Right
            new() { Action = HotkeyAction.PrevMode, Modifiers = mods, Key = 0x25 },       // Left
        };
    }
}
