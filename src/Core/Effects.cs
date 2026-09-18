using System;
using System.Collections.Generic;
using System.Numerics;
using GlowSync.Audio;
using GlowSync.Config;

namespace GlowSync.Core;

internal enum TestPattern { None, Sides, White, Red, Green, Blue }

/// <summary>
/// Procedural effects. All outputs are display-referred colors in 0..1 (gamma is applied later).
/// Phases are integrated per frame, so changing a speed slider never makes the animation jump.
/// </summary>
internal sealed class Effects
{
    private double _rainbow, _cycle, _breath, _comet, _hueDrift;

    public void Advance(double dt, EffectsConfig fx)
    {
        _rainbow = Frac(_rainbow + dt * Speed(fx.RainbowSpeed, 0.01, 0.6) * (fx.RainbowReverse ? 1 : -1));
        _cycle = Frac(_cycle + dt * Speed(fx.CycleSpeed, 0.005, 0.3));
        _breath += dt / (9.0 - Math.Clamp(fx.BreathingSpeed, 0, 100) / 100.0 * 8.0);
        if (_breath > 1e6) _breath = Frac(_breath);
        _comet = Frac(_comet + dt * Speed(fx.CometSpeed, 0.05, 1.2));
        _hueDrift = Frac(_hueDrift + dt * 0.03);
    }

    public void Rainbow(Span<Vector3> leds, EffectsConfig fx)
    {
        int n = leds.Length;
        double scale = Math.Clamp(fx.RainbowScale, 0.1, 8);
        for (int i = 0; i < n; i++)
        {
            leds[i] = ColorMath.Hsv((float)(_rainbow + scale * i / Math.Max(1, n)), 1f, 1f);
        }
    }

    public void ColorCycle(Span<Vector3> leds) => leds.Fill(ColorMath.Hsv((float)_cycle, 1f, 1f));

    public void Breathing(Span<Vector3> leds, EffectsConfig fx)
    {
        double phase = _breath;
        float k = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
        k = 0.04f + 0.96f * k * k;
        var color = fx.BreathingRainbow
            ? ColorMath.Hsv((float)Frac(Math.Floor(phase) * 0.137), 1f, 1f)
            : ColorMath.ParseHex(fx.BreathingColor, Vector3.One);
        leds.Fill(color * k);
    }

    public void Static(Span<Vector3> leds, EffectsConfig fx) =>
        leds.Fill(ColorMath.ParseHex(fx.StaticColor, Vector3.One));

    public void Comet(Span<Vector3> leds, EffectsConfig fx)
    {
        int n = leds.Length;
        if (n == 0) return;
        double head = _comet * n;
        double tail = Math.Max(1, Math.Clamp(fx.CometLength, 1, 100) / 100.0 * n);
        var baseColor = ColorMath.ParseHex(fx.CometColor, Vector3.One);
        for (int i = 0; i < n; i++)
        {
            double behind = head - i;
            if (behind < 0) behind += n;
            float k = behind <= tail ? (float)Math.Pow(1 - behind / tail, 2.2) : 0f;
            var c = fx.CometRainbow ? ColorMath.Hsv((float)(_hueDrift + (double)i / n), 1f, 1f) : baseColor;
            leds[i] = c * k;
        }
    }

    public void Music(Span<Vector3> leds, EffectsConfig fx, ReadOnlySpan<float> bands, float level)
    {
        int n = leds.Length;
        if (n == 0) return;
        var tint = ColorMath.ParseHex(fx.MusicColor, Vector3.One);
        for (int i = 0; i < n; i++)
        {
            // 0 at both strip ends, 1 in the middle: bass lives at the ends, treble meets in the center.
            float t = n == 1 ? 0.5f : (float)i / (n - 1);
            float m = 1f - MathF.Abs(2f * t - 1f);
            switch (fx.MusicStyle)
            {
                case MusicStyle.Spectrum:
                {
                    float pos = m * (AudioAnalyzer.BandCount - 1);
                    int b0 = (int)pos;
                    int b1 = Math.Min(b0 + 1, AudioAnalyzer.BandCount - 1);
                    float v = bands[b0] + (bands[b1] - bands[b0]) * (pos - b0);
                    var c = fx.MusicRainbow ? ColorMath.Hsv((float)(m * 0.8 + _hueDrift), 1f, 1f) : tint;
                    leds[i] = c * v;
                    break;
                }
                case MusicStyle.Pulse:
                {
                    var c = fx.MusicRainbow ? ColorMath.Hsv((float)(_hueDrift + m * 0.15), 1f, 1f) : tint;
                    leds[i] = c * level;
                    break;
                }
                default:
                {
                    // VU meter growing from both ends towards the middle: green → yellow → red.
                    float lit = level * 1.05f;
                    float k = Math.Clamp((lit - m) * 12f + 1f, 0f, 1f);
                    var c = fx.MusicRainbow ? ColorMath.Hsv(0.33f - 0.33f * m, 1f, 1f) : tint;
                    leds[i] = c * k;
                    break;
                }
            }
        }
    }

    public static void Test(Span<Vector3> leds, TestPattern pattern, IReadOnlyList<LedPoint> points, int gridW, int gridH)
    {
        switch (pattern)
        {
            case TestPattern.White: leds.Fill(Vector3.One); return;
            case TestPattern.Red: leds.Fill(new Vector3(1, 0, 0)); return;
            case TestPattern.Green: leds.Fill(new Vector3(0, 1, 0)); return;
            case TestPattern.Blue: leds.Fill(new Vector3(0, 0, 1)); return;
        }
        for (int i = 0; i < leds.Length; i++)
        {
            var p = i < points.Count ? points[i] : new LedPoint();
            Vector3 c;
            if (p.X <= 0) c = new Vector3(1, 0, 0);                 // left: red
            else if (p.X >= gridW - 1) c = new Vector3(0, 0, 1);    // right: blue
            else if (p.Y <= 0) c = new Vector3(0, 1, 0);            // top: green
            else if (p.Y >= gridH - 1) c = new Vector3(1, 0.8f, 0); // bottom: yellow
            else c = new Vector3(0.3f, 0.3f, 0.3f);
            leds[i] = c * 0.6f;
        }
        if (leds.Length > 0) leds[0] = Vector3.One;                  // first LED: white
        if (leds.Length > 1) leds[^1] = new Vector3(1, 0, 1);         // last LED: magenta
    }

    /// <summary>Fractional part in double precision, so hues stay smooth after days of uptime.</summary>
    private static double Frac(double v) => v - Math.Floor(v);

    /// <summary>Maps a 0..100 slider to a frequency with a perceptually even feel.</summary>
    private static double Speed(int slider, double min, double max) =>
        min * Math.Pow(max / min, Math.Clamp(slider, 0, 100) / 100.0);
}
