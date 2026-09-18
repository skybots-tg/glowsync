using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using GlowSync.Audio;
using GlowSync.Capture;
using GlowSync.Config;
using GlowSync.Device;
using GlowSync.Platform;

namespace GlowSync.Core;

/// <summary>
/// The render loop: picks the active source (screen, music, effect or test pattern), runs the color
/// pipeline and hands frames to the device. Owns all GPU/audio resources on a single thread and
/// releases them whenever the LEDs are off.
/// </summary>
internal sealed class Engine : IDisposable
{
    private const double IdleInterval = 0.5;

    /// <summary>
    /// Some monitors (and virtual display drivers) flap on/off many times within a second while going to sleep;
    /// the strip only follows an "off" state that has lasted this long. "On" is followed immediately.
    /// </summary>
    private const long DisplayOffDebounceMs = 2000;

    private readonly ConfigStore _store;
    private readonly DeviceManager _device;
    private readonly Thread _thread;
    private readonly FrameTimer _timer = new();
    private readonly DesktopCapture _capture = new();
    private readonly ScreenSampler _sampler = new();
    private readonly AudioAnalyzer _audio = new();
    private readonly Effects _effects = new();
    private readonly MonitorPowerWatcher _monitor = new();
    private readonly float[] _bands = new float[AudioAnalyzer.BandCount];
    private readonly object _previewLock = new();

    private volatile bool _stop;
    private readonly bool?[] _displaySignals = new bool?[3];
    private bool _rawDisplayOff;
    private long _rawChangedAt;
    private bool _displayOff;
    private volatile bool _locked;
    private volatile bool _suspended;
    private volatile bool _captureResetRequested;
    private volatile TestPattern _test;
    private long _testUntil;

    private int _layoutRevision = -1;
    private IReadOnlyList<LedPoint> _leds = Array.Empty<LedPoint>();
    private int _gridW = 1, _gridH = 1;
    private Vector3[] _frame = Array.Empty<Vector3>();
    private Vector3[] _scratch = Array.Empty<Vector3>();
    private Vector3[] _smoothed = Array.Empty<Vector3>();
    private byte[] _out = Array.Empty<byte>();
    private byte[] _preview = Array.Empty<byte>();

    private float _fade;
    private float _brightness = -1;
    private bool _sampled;
    private long _captureLostSince;
    private bool _active;
    private int _frames;
    private long _fpsWindowStart;
    private long _lastErrorLog;

    public Engine(ConfigStore store, DeviceManager device)
    {
        _store = store;
        _device = device;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlowSync.Engine" };
    }

    /// <summary>True while LEDs are (or are fading) on. Raised from the engine thread.</summary>
    public event Action<bool>? ActiveChanged;

    public double Fps { get; private set; }

    public bool IsActive => _active;

    /// <summary>Debounced monitor state.</summary>
    public bool DisplayOff => _displayOff;

    public void Start()
    {
        _monitor.Start();
        _thread.Start();
    }

    /// <summary>Monitor state as reported over DDC/CI; shown in the settings window.</summary>
    public MonitorPowerWatcher Monitor => _monitor;

    public void Wake() => _timer.Wake();

    /// <summary>
    /// Called when Windows reports the display power state. Machines can send several of these
    /// signals and they briefly disagree while a monitor wakes, so the most trustworthy one that
    /// has ever arrived wins (console display state first, the legacy monitor-power one last).
    /// </summary>
    public void SetDisplayOff(DisplaySignal signal, bool off)
    {
        lock (_displaySignals)
        {
            if (_displaySignals[(int)signal] == off) return;
            _displaySignals[(int)signal] = off;
        }
        Log.Info($"Power signal: {signal} display {(off ? "off" : "on")}");
        Wake();
    }

    private bool WindowsReportsOff()
    {
        lock (_displaySignals)
        {
            foreach (var state in _displaySignals)
            {
                if (state is { } off) return off;
            }
        }
        return false;
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        Wake();
    }

    public void Suspend()
    {
        _suspended = true;
        _monitor.SetPaused(true);
        Wake();
    }

    public void Resume()
    {
        _suspended = false;
        _monitor.SetPaused(false);
        _captureResetRequested = true;
        Wake();
    }

    public void RequestCaptureReset()
    {
        _captureResetRequested = true;
        Wake();
    }

    public void StartTest(TestPattern pattern, double seconds)
    {
        Volatile.Write(ref _testUntil, Environment.TickCount64 + (long)(seconds * 1000));
        _test = pattern;
        Wake();
    }

    /// <summary>Copies the last output frame (RGB bytes as sent to the strip). Returns LED count.</summary>
    public int CopyPreview(ref byte[] target)
    {
        lock (_previewLock)
        {
            if (target.Length != _preview.Length) target = new byte[_preview.Length];
            Buffer.BlockCopy(_preview, 0, target, 0, _preview.Length);
            return _preview.Length / 3;
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds;
        _fpsWindowStart = Environment.TickCount64;
        while (!_stop)
        {
            double start = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(start - last, 0, 0.25);
            last = start;

            double interval = IdleInterval;
            try
            {
                interval = Tick(_store.Config, dt);
            }
            catch (Exception ex)
            {
                if (Environment.TickCount64 - _lastErrorLog > 10_000)
                {
                    _lastErrorLog = Environment.TickCount64;
                    Log.Error("Engine tick failed", ex);
                }
                _capture.Reset();
            }

            double remaining = interval - (clock.Elapsed.TotalSeconds - start);
            if (remaining > 0.0005) _timer.Sleep(remaining);
        }
        _capture.Dispose();
        _audio.Dispose();
        _monitor.Dispose();
    }

    /// <summary>Renders one frame. Returns the desired interval until the next one.</summary>
    private double Tick(AppConfig cfg, double dt)
    {
        if (_captureResetRequested)
        {
            _captureResetRequested = false;
            _capture.Reset();
        }
        RebuildLayout(cfg);
        int n = _leds.Count;

        // Two independent sources: the Windows power notification, and the monitor's own answer over
        // DDC/CI (the only signal when the monitor is switched off by its button).
        _monitor.SetEnabled(cfg.Behavior.OffWhenDisplayOff && cfg.Behavior.DetectMonitorViaDdc);
        bool rawOff = WindowsReportsOff() || (cfg.Behavior.DetectMonitorViaDdc && _monitor.IsOff == true);
        if (rawOff != _rawDisplayOff)
        {
            _rawDisplayOff = rawOff;
            _rawChangedAt = Environment.TickCount64;
        }
        bool displayOff = rawOff && Environment.TickCount64 - _rawChangedAt >= DisplayOffDebounceMs;
        if (displayOff != _displayOff)
        {
            _displayOff = displayOff;
            Log.Info(displayOff ? "Display off" : "Display on");
            if (!displayOff) _capture.Reset(); // duplication can go stale across a long monitor sleep
        }

        bool testing = _test != TestPattern.None && Environment.TickCount64 < Volatile.Read(ref _testUntil);
        if (!testing) _test = TestPattern.None;

        bool active = !_suspended && (testing || (cfg.Enabled
                                                  && !(cfg.Behavior.OffWhenDisplayOff && _displayOff)
                                                  && !(cfg.Behavior.OffWhenLocked && _locked)));
        if (active != _active)
        {
            _active = active;
            Log.Info(active ? "Output on" : "Output off");
            ActiveChanged?.Invoke(active);
        }

        float fadeStep = cfg.Behavior.FadeMs <= 0 || _suspended ? 1f : (float)(dt * 1000.0 / cfg.Behavior.FadeMs);
        _fade = Math.Clamp(_fade + (active ? fadeStep : -fadeStep), 0f, 1f);

        if (!active && _fade <= 0f)
        {
            // Fully off: free the GPU duplication and audio capture, keep the strip explicitly black.
            _capture.Reset();
            _audio.Stop();
            _sampled = false;
            Array.Clear(_smoothed);
            Array.Clear(_out);
            _device.Submit(_out.AsSpan(0, n * 3));
            PublishPreview(n);
            Fps = 0;
            return IdleInterval;
        }

        var mode = testing ? (EffectMode)(-1) : cfg.Mode;
        if (mode != EffectMode.Screen) _capture.Reset();
        if (mode != EffectMode.Music) _audio.Stop();

        _effects.Advance(dt, cfg.Effects);
        var frame = _frame.AsSpan(0, n);
        switch (mode)
        {
            case EffectMode.Screen: RenderScreen(cfg, frame); break;
            case EffectMode.Music:
                _audio.Maintain();
                float level = _audio.Analyze(_bands, (float)dt, cfg.Effects.MusicSensitivity, cfg.Effects.MusicSmoothing);
                _effects.Music(frame, cfg.Effects, _bands, level);
                break;
            case EffectMode.Rainbow: _effects.Rainbow(frame, cfg.Effects); break;
            case EffectMode.ColorCycle: _effects.ColorCycle(frame); break;
            case EffectMode.Breathing: _effects.Breathing(frame, cfg.Effects); break;
            case EffectMode.Static: _effects.Static(frame, cfg.Effects); break;
            case EffectMode.Comet: _effects.Comet(frame, cfg.Effects); break;
            default: Effects.Test(frame, _test, _leds, _gridW, _gridH); break;
        }

        double target = testing ? 0.6 : cfg.Brightness / 100.0;
        if (!testing && cfg.Night.IsActive(DateTime.Now)) target = Math.Min(target, cfg.Night.Brightness / 100.0);
        _brightness = _brightness < 0 ? (float)target : _brightness + ((float)target - _brightness) * (1f - MathF.Exp((float)(-dt / 0.15)));

        Process(cfg, mode, frame, dt);
        _device.Submit(_out.AsSpan(0, n * 3));
        PublishPreview(n);
        CountFrame();
        return 1.0 / Math.Clamp(cfg.Screen.Fps, 10, 60);
    }

    private void RenderScreen(AppConfig cfg, Span<Vector3> frame)
    {
        var result = _capture.Update(cfg.Screen.Monitor);
        if (result == CaptureResult.NewFrame)
        {
            _sampler.Ingest(_capture, cfg.Screen.RemoveBlackBars);
            _sampled = true;
            _captureLostSince = 0;
        }
        else if (result == CaptureResult.Unavailable)
        {
            if (_captureLostSince == 0) _captureLostSince = Environment.TickCount64;
            // Secure desktop (UAC, lock screen) or mode switch: hold the last colors briefly, then go dark.
            if (Environment.TickCount64 - _captureLostSince > 1500) _sampled = false;
        }
        else
        {
            _captureLostSince = 0;
        }

        if (!_sampled)
        {
            frame.Clear();
            return;
        }
        _sampler.Sample(_leds, _gridW, _gridH, cfg.Screen.Depth, _capture.Rotation, frame);

        if (cfg.Screen.SpatialBlur > 0) Blur(frame, cfg.Screen.SpatialBlur / 100.0 * 4.0);
    }

    private void Process(AppConfig cfg, EffectMode mode, Span<Vector3> frame, double dt)
    {
        var screen = cfg.Screen;
        bool isScreen = mode == EffectMode.Screen;
        float gamma = (float)screen.Gamma;
        float saturation = 1f + screen.Saturation / 100f;
        float threshold = screen.BlackThreshold / 100f;
        var gains = new Vector3(cfg.Color.RedGain, cfg.Color.GreenGain, cfg.Color.BlueGain) / 100f;
        double tauMs = mode switch
        {
            EffectMode.Screen => screen.SmoothingMs,
            EffectMode.Music => 15,
            (EffectMode)(-1) => 0,
            _ => 80,
        };
        float alpha = tauMs <= 0 ? 1f : 1f - MathF.Exp((float)(-dt * 1000.0 / tauMs));
        float scale = _brightness * _fade * _fade;
        var luma = new Vector3(0.299f, 0.587f, 0.114f);

        for (int i = 0; i < frame.Length; i++)
        {
            var c = Vector3.Clamp(frame[i], Vector3.Zero, Vector3.One);
            if (isScreen)
            {
                if (saturation != 1f)
                {
                    float y = Vector3.Dot(c, luma);
                    c = Vector3.Clamp(new Vector3(y) + (c - new Vector3(y)) * saturation, Vector3.Zero, Vector3.One);
                }
                if (MathF.Max(c.X, MathF.Max(c.Y, c.Z)) < threshold) c = Vector3.Zero;
            }
            var linear = new Vector3(MathF.Pow(c.X, gamma), MathF.Pow(c.Y, gamma), MathF.Pow(c.Z, gamma)) * gains;
            _smoothed[i] += (linear - _smoothed[i]) * alpha;
            var o = _smoothed[i] * scale;
            int k = i * 3;
            _out[k] = ColorMath.ToByte(o.X);
            _out[k + 1] = ColorMath.ToByte(o.Y);
            _out[k + 2] = ColorMath.ToByte(o.Z);
        }
    }

    private void Blur(Span<Vector3> frame, double radius)
    {
        int r = (int)Math.Ceiling(radius);
        if (r < 1) return;
        double sigma = Math.Max(0.5, radius / 2);
        frame.CopyTo(_scratch);
        int n = frame.Length;
        for (int i = 0; i < n; i++)
        {
            Vector3 sum = Vector3.Zero;
            float wsum = 0;
            for (int k = -r; k <= r; k++)
            {
                float w = (float)Math.Exp(-(k * k) / (2 * sigma * sigma));
                sum += _scratch[Math.Clamp(i + k, 0, n - 1)] * w;
                wsum += w;
            }
            frame[i] = sum / wsum;
        }
    }

    private void RebuildLayout(AppConfig cfg)
    {
        int revision = _store.Revision;
        if (revision == _layoutRevision) return;
        _layoutRevision = revision;

        var layout = cfg.Layout;
        var leds = layout.Leds;
        if (leds.Count == 0) return;
        _leds = leds;
        _gridW = Math.Max(1, layout.GridWidth);
        _gridH = Math.Max(1, layout.GridHeight);
        int n = leds.Count;
        if (_frame.Length != n)
        {
            _frame = new Vector3[n];
            _scratch = new Vector3[n];
            _smoothed = new Vector3[n];
            _out = new byte[n * 3];
        }
    }

    private void PublishPreview(int n)
    {
        lock (_previewLock)
        {
            if (_preview.Length != n * 3) _preview = new byte[n * 3];
            Buffer.BlockCopy(_out, 0, _preview, 0, n * 3);
        }
    }

    private void CountFrame()
    {
        _frames++;
        long now = Environment.TickCount64;
        if (now - _fpsWindowStart >= 1000)
        {
            Fps = _frames * 1000.0 / (now - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = now;
        }
    }

    public void Dispose()
    {
        _stop = true;
        Wake();
        if (_thread.IsAlive) _thread.Join(2000);
        _timer.Dispose();
    }
}
