using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GlowSync.Config;

namespace GlowSync.UI;

/// <summary>Live picture of the strip around a stylized monitor, colored with what is actually sent to the LEDs.</summary>
internal sealed class LedPreview : FrameworkElement
{
    private static readonly byte[] InverseGamma = BuildInverseGamma();

    private readonly DrawingVisual _glow = new() { Effect = new BlurEffect { Radius = 26, RenderingBias = RenderingBias.Performance } };
    private readonly DrawingVisual _body = new();
    private readonly VisualCollection _visuals;

    private IReadOnlyList<LedPoint> _leds = Array.Empty<LedPoint>();
    private int _gridW = 31, _gridH = 17;
    private double _aspect = 16.0 / 9.0;
    private byte[] _rgb = Array.Empty<byte>();
    private int _count;

    public LedPreview()
    {
        _visuals = new VisualCollection(this) { _glow, _body };
        IsHitTestVisible = false;
    }

    public void SetLayout(IReadOnlyList<LedPoint> leds, int gridW, int gridH, double aspect)
    {
        _leds = leds;
        _gridW = Math.Max(1, gridW);
        _gridH = Math.Max(1, gridH);
        _aspect = aspect > 0.2 ? aspect : 16.0 / 9.0;
        Redraw();
    }

    public void SetColors(byte[] rgb, int count)
    {
        _rgb = rgb;
        _count = count;
        Redraw();
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        const double margin = 34;
        double sw = w - margin * 2, sh = sw / _aspect;
        if (sh > h - margin * 2)
        {
            sh = h - margin * 2;
            sw = sh * _aspect;
        }
        var screen = new Rect((w - sw) / 2, (h - sh) / 2, sw, sh);

        using (var glow = _glow.RenderOpen())
        using (var body = _body.RenderOpen())
        {
            var screenBrush = new LinearGradientBrush(Color.FromRgb(24, 25, 30), Color.FromRgb(14, 15, 18), 90);
            body.DrawRoundedRectangle(screenBrush, new Pen(new SolidColorBrush(Color.FromRgb(60, 62, 70)), 1.5), screen, 8, 8);

            double dot = Math.Clamp(Math.Min(sw / Math.Max(1, _gridW), sh / Math.Max(1, _gridH)) * 0.42, 2.5, 7);
            for (int i = 0; i < _leds.Count; i++)
            {
                var p = _leds[i];
                double cx = screen.Left + (p.X + 0.5) / _gridW * sw;
                double cy = screen.Top + (p.Y + 0.5) / _gridH * sh;
                const double outside = 12;
                if (p.X <= 0) cx = screen.Left - outside;
                else if (p.X >= _gridW - 1) cx = screen.Right + outside;
                if (p.Y <= 0) cy = screen.Top - outside;
                else if (p.Y >= _gridH - 1) cy = screen.Bottom + outside;

                Color c = Color.FromRgb(45, 45, 50);
                if (i < _count && (i + 1) * 3 <= _rgb.Length)
                {
                    c = Color.FromRgb(InverseGamma[_rgb[i * 3]], InverseGamma[_rgb[i * 3 + 1]], InverseGamma[_rgb[i * 3 + 2]]);
                }
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                glow.DrawEllipse(brush, null, new Point(cx, cy), dot * 2.6, dot * 2.6);
                body.DrawEllipse(brush, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 0.6), new Point(cx, cy), dot, dot);
            }
        }
    }

    /// <summary>LED bytes are linear light; convert back so the preview looks like the real strip.</summary>
    private static byte[] BuildInverseGamma()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++) table[i] = (byte)Math.Round(Math.Pow(i / 255.0, 1 / 2.2) * 255);
        return table;
    }
}
