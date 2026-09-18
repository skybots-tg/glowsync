using System;
using System.Collections.Generic;
using System.Numerics;
using GlowSync.Config;
using Vortice.DXGI;

namespace GlowSync.Capture;

/// <summary>
/// Turns the reduced desktop image into one average color per LED zone.
/// Zone geometry matches Skydimo: each LED owns one cell of its capture grid,
/// optionally extended <c>depth</c> cells towards the screen center.
/// </summary>
internal sealed class ScreenSampler
{
    private const int BarThreshold = 18;
    private const int BarStableFrames = 30;

    private int[] _sumR = Array.Empty<int>(), _sumG = Array.Empty<int>(), _sumB = Array.Empty<int>();
    private int _w, _h;

    private (int T, int B, int L, int R) _bars;
    private (int T, int B, int L, int R) _candidate;
    private int _candidateFrames;

    public (int T, int B, int L, int R) Bars => _bars;

    /// <summary>Call once per new captured frame.</summary>
    public void Ingest(DesktopCapture capture, bool removeBars)
    {
        _w = capture.Width;
        _h = capture.Height;
        var px = capture.Pixels;
        int stride = _w + 1;
        int size = stride * (_h + 1);
        if (_sumR.Length != size)
        {
            _sumR = new int[size];
            _sumG = new int[size];
            _sumB = new int[size];
            _bars = default;
        }

        for (int y = 0; y < _h; y++)
        {
            int rowR = 0, rowG = 0, rowB = 0;
            int src = y * _w * 4;
            int up = y * stride + 1;
            int dst = up + stride;
            for (int x = 0; x < _w; x++, src += 4)
            {
                rowB += px[src];
                rowG += px[src + 1];
                rowR += px[src + 2];
                _sumR[dst + x] = _sumR[up + x] + rowR;
                _sumG[dst + x] = _sumG[up + x] + rowG;
                _sumB[dst + x] = _sumB[up + x] + rowB;
            }
        }

        if (removeBars) DetectBars(px);
        else _bars = default;
    }

    public void Sample(IReadOnlyList<LedPoint> leds, int gridW, int gridH, double depth, ModeRotation rotation, Span<Vector3> output)
    {
        if (_w == 0 || _h == 0)
        {
            output.Clear();
            return;
        }

        double cx0 = _bars.L, cx1 = _w - _bars.R, cy0 = _bars.T, cy1 = _h - _bars.B;
        int n = Math.Min(leds.Count, output.Length);
        for (int i = 0; i < n; i++)
        {
            var p = leds[i];
            double u0 = (double)p.X / gridW, u1 = (p.X + 1.0) / gridW;
            double v0 = (double)p.Y / gridH, v1 = (p.Y + 1.0) / gridH;
            if (p.X <= 0) u1 = depth / gridW;
            if (p.X >= gridW - 1) u0 = 1 - depth / gridW;
            if (p.Y <= 0) v1 = depth / gridH;
            if (p.Y >= gridH - 1) v0 = 1 - depth / gridH;

            var (ta, tb) = Rotate(u0, v0, rotation);
            var (tc, td) = Rotate(u1, v1, rotation);
            double tu0 = Math.Clamp(Math.Min(ta, tc), 0, 1), tu1 = Math.Clamp(Math.Max(ta, tc), 0, 1);
            double tv0 = Math.Clamp(Math.Min(tb, td), 0, 1), tv1 = Math.Clamp(Math.Max(tb, td), 0, 1);

            int x0 = (int)Math.Floor(cx0 + tu0 * (cx1 - cx0));
            int x1 = (int)Math.Ceiling(cx0 + tu1 * (cx1 - cx0));
            int y0 = (int)Math.Floor(cy0 + tv0 * (cy1 - cy0));
            int y1 = (int)Math.Ceiling(cy0 + tv1 * (cy1 - cy0));
            x0 = Math.Clamp(x0, 0, _w - 1);
            y0 = Math.Clamp(y0, 0, _h - 1);
            x1 = Math.Clamp(Math.Max(x1, x0 + 1), 1, _w);
            y1 = Math.Clamp(Math.Max(y1, y0 + 1), 1, _h);

            output[i] = Average(x0, y0, x1, y1);
        }
        output[n..].Clear();
    }

    private Vector3 Average(int x0, int y0, int x1, int y1)
    {
        int s = _w + 1;
        int a = y0 * s + x0, b = y0 * s + x1, c = y1 * s + x0, d = y1 * s + x1;
        float inv = 1f / ((x1 - x0) * (y1 - y0) * 255f);
        return new Vector3(
            (_sumR[d] - _sumR[b] - _sumR[c] + _sumR[a]) * inv,
            (_sumG[d] - _sumG[b] - _sumG[c] + _sumG[a]) * inv,
            (_sumB[d] - _sumB[b] - _sumB[c] + _sumB[a]) * inv);
    }

    /// <summary>Maps desktop-space normalized coordinates into the duplicated (unrotated) texture.</summary>
    private static (double U, double V) Rotate(double u, double v, ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => (v, 1 - u),
        ModeRotation.Rotate180 => (1 - u, 1 - v),
        ModeRotation.Rotate270 => (1 - v, u),
        _ => (u, v),
    };

    private void DetectBars(byte[] px)
    {
        int top = 0;
        while (top < _h && RowDark(px, top, 0, _w)) top++;
        if (top >= _h) return; // fully dark frame says nothing about bars

        int bottom = 0;
        while (bottom < _h - top && RowDark(px, _h - 1 - bottom, 0, _w)) bottom++;

        int left = 0;
        while (left < _w && ColumnDark(px, left, top, _h - bottom)) left++;
        int right = 0;
        while (right < _w - left && ColumnDark(px, _w - 1 - right, top, _h - bottom)) right++;

        if (!Symmetric(ref top, ref bottom, _h) || !Symmetric(ref left, ref right, _w)) return;

        var cand = (top, bottom, left, right);
        if (Near(cand, _candidate))
        {
            if (++_candidateFrames >= BarStableFrames) _bars = _candidate;
        }
        else
        {
            _candidate = cand;
            _candidateFrames = 0;
        }
    }

    /// <summary>Bars must appear on both opposite edges (subtitles in one bar must not move the zones).</summary>
    private static bool Symmetric(ref int a, ref int b, int extent)
    {
        int tolerance = Math.Max(2, extent / 50);
        int max = extent * 3 / 10;
        if (a <= 1 && b <= 1)
        {
            a = b = 0;
            return true;
        }
        if (a > 1 && b > 1 && Math.Abs(a - b) <= tolerance && a <= max && b <= max)
        {
            a = b = Math.Min(a, b);
            return true;
        }
        return false;
    }

    private static bool Near((int T, int B, int L, int R) x, (int T, int B, int L, int R) y) =>
        Math.Abs(x.T - y.T) <= 1 && Math.Abs(x.B - y.B) <= 1 && Math.Abs(x.L - y.L) <= 1 && Math.Abs(x.R - y.R) <= 1;

    private bool RowDark(byte[] px, int y, int x0, int x1)
    {
        int row = y * _w * 4;
        for (int x = x0; x < x1; x += 2)
        {
            int i = row + x * 4;
            if (px[i] > BarThreshold || px[i + 1] > BarThreshold || px[i + 2] > BarThreshold) return false;
        }
        return true;
    }

    private bool ColumnDark(byte[] px, int x, int y0, int y1)
    {
        for (int y = y0; y < y1; y += 2)
        {
            int i = (y * _w + x) * 4;
            if (px[i] > BarThreshold || px[i + 1] > BarThreshold || px[i + 2] > BarThreshold) return false;
        }
        return true;
    }
}
