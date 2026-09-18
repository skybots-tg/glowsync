using System;
using GlowSync.Platform;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GlowSync.Audio;

/// <summary>
/// Captures whatever the default playback device outputs (WASAPI loopback) and turns it into
/// log-spaced spectrum bands plus an overall level, both auto-gain normalized to 0..1.
/// Follows default-device switches (headphones ↔ speakers) automatically.
/// </summary>
internal sealed class AudioAnalyzer : IDisposable
{
    public const int BandCount = 24;
    private const int FftSize = 2048;
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _lock = new();
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private long _lastSampleTicks;

    private WasapiLoopbackCapture? _capture;
    private string? _deviceId;
    private int _sampleRate = 48000;
    private long _nextDeviceCheck;
    private long _restartAt;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _raw = new float[BandCount];
    private readonly float[] _smoothed = new float[BandCount];
    private float _peak = 1e-3f;
    private float _level;

    public AudioAnalyzer()
    {
        for (int i = 0; i < FftSize; i++) _window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));
    }

    public bool IsRunning => _capture != null;

    /// <summary>Starts capture if needed and restarts it after device changes. Call from the engine loop.</summary>
    public void Maintain()
    {
        long now = Environment.TickCount64;
        if (_capture == null)
        {
            if (now >= _restartAt) Start();
            return;
        }
        if (now >= _nextDeviceCheck)
        {
            _nextDeviceCheck = now + 2000;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device.ID != _deviceId)
                {
                    Log.Info("Default audio device changed, restarting loopback capture");
                    Stop();
                    Start();
                }
            }
            catch
            {
                // No playback device right now; keep the current state.
            }
        }
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            capture.StopRecording();
        }
        catch
        {
            // already stopped
        }
        capture.Dispose();
    }

    private void Start()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _deviceId = device.ID;
            var capture = new WasapiLoopbackCapture(device);
            _sampleRate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            _nextDeviceCheck = Environment.TickCount64 + 2000;
        }
        catch (Exception ex)
        {
            Log.Warn("Audio loopback start failed: " + ex.Message);
            _capture = null;
            _restartAt = Environment.TickCount64 + 5000;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Log.Warn("Audio capture stopped: " + e.Exception.Message);
        if (ReferenceEquals(sender, _capture))
        {
            _capture = null;
            _restartAt = Environment.TickCount64 + 2000;
            (sender as IDisposable)?.Dispose();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture) return;
        var format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = bytesPerSample * channels;
        if (frameSize == 0) return;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

        lock (_lock)
        {
            for (int offset = 0; offset + frameSize <= e.BytesRecorded; offset += frameSize)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = offset + c * bytesPerSample;
                    sum += (isFloat, bytesPerSample) switch
                    {
                        (true, 4) => BitConverter.ToSingle(e.Buffer, p),
                        (false, 2) => BitConverter.ToInt16(e.Buffer, p) / 32768f,
                        (false, 3) => ((e.Buffer[p] | (e.Buffer[p + 1] << 8) | ((sbyte)e.Buffer[p + 2] << 16)) / 8388608f),
                        (false, 4) => BitConverter.ToInt32(e.Buffer, p) / 2147483648f,
                        _ => 0f,
                    };
                }
                _ring[_ringPos] = sum / channels;
                _ringPos = (_ringPos + 1) % FftSize;
            }
            _lastSampleTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Fills <paramref name="bands"/> (BandCount values, 0..1) and returns the overall level (0..1).
    /// <paramref name="sensitivity"/> 0..100, <paramref name="smoothing"/> 0..100 (release speed).
    /// </summary>
    public float Analyze(Span<float> bands, float dt, int sensitivity, int smoothing)
    {
        bool silent;
        lock (_lock)
        {
            // WASAPI loopback delivers no packets at all while nothing is playing.
            silent = Environment.TickCount64 - _lastSampleTicks > 250;
            for (int i = 0; i < FftSize; i++)
            {
                float s = silent ? 0 : _ring[(_ringPos + i) % FftSize];
                _re[i] = s * _window[i];
                _im[i] = 0;
            }
        }

        Fft(_re, _im);

        float binHz = (float)_sampleRate / FftSize;
        const float minHz = 35f, maxHz = 16000f;
        float framePeak = 0;
        for (int b = 0; b < BandCount; b++)
        {
            float f0 = minHz * MathF.Pow(maxHz / minHz, (float)b / BandCount);
            float f1 = minHz * MathF.Pow(maxHz / minHz, (float)(b + 1) / BandCount);
            int i0 = Math.Clamp((int)(f0 / binHz), 1, FftSize / 2 - 1);
            int i1 = Math.Clamp((int)MathF.Ceiling(f1 / binHz), i0 + 1, FftSize / 2);
            float energy = 0;
            for (int i = i0; i < i1; i++) energy = MathF.Max(energy, _re[i] * _re[i] + _im[i] * _im[i]);
            // Tilt: high bands carry far less energy in music; compensate so the spectrum looks even.
            float value = MathF.Sqrt(energy) * (1f + b * 0.18f);
            _raw[b] = value;
            framePeak = MathF.Max(framePeak, value);
        }

        // Automatic gain: follow peaks fast, forget them slowly (~4 s), never amplify silence.
        _peak = MathF.Max(framePeak, _peak * MathF.Exp(-dt / 4f));
        float gain = 0.35f + sensitivity / 100f * 1.3f;
        float floor = MathF.Max(_peak, 0.5f);
        float release = MathF.Exp(-dt / (0.04f + smoothing / 100f * 0.5f));

        float levelSum = 0;
        for (int b = 0; b < BandCount; b++)
        {
            float v = Math.Clamp(_raw[b] / floor * gain, 0f, 1f);
            v = MathF.Pow(v, 1.6f); // expand: quiet parts dark, hits bright
            _smoothed[b] = v >= _smoothed[b] ? v : _smoothed[b] * release + v * (1 - release);
            bands[b] = _smoothed[b];
            levelSum += b < BandCount / 3 ? v * 2 : v; // bass-weighted
        }
        float level = Math.Clamp(levelSum / (BandCount * 1.33f) * 1.8f, 0f, 1f);
        _level = level >= _level ? level : _level * release + level * (1 - release);
        return _level;
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci;
                    float ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
