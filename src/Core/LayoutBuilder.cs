using System;
using System.Collections.Generic;
using System.Linq;
using GlowSync.Config;

namespace GlowSync.Core;

/// <summary>
/// Generates LED grid positions from per-side counts. With left=17, top=31, right=17 and a
/// bottom-left clockwise start it reproduces Skydimo's SK0127 ledMap exactly.
/// </summary>
internal static class LayoutBuilder
{
    public static void Apply(LayoutConfig layout)
    {
        var (w, h, leds) = Build(layout.Left, layout.Top, layout.Right, layout.Bottom, layout.Start, layout.Clockwise, layout.Offset);
        layout.GridWidth = w;
        layout.GridHeight = h;
        layout.Leds = leds;
    }

    public static (int Width, int Height, List<LedPoint> Leds) Build(
        int left, int top, int right, int bottom, StartCorner start, bool clockwise, int offset)
    {
        int w = Math.Max(top, bottom);
        int h = Math.Max(left, right);
        if (w == 0 && h == 0) { w = 31; h = 17; }
        else if (w == 0) w = Math.Max(2, (int)Math.Round(h * 16.0 / 9.0));
        else if (h == 0) h = Math.Max(2, (int)Math.Round(w * 9.0 / 16.0));

        // Sides in clockwise order starting at the bottom-left corner.
        var sides = new List<List<LedPoint>>
        {
            Side(left, i => new LedPoint(0, Lerp(h - 1, 0, i, left))),
            Side(top, i => new LedPoint(Lerp(0, w - 1, i, top), 0)),
            Side(right, i => new LedPoint(w - 1, Lerp(0, h - 1, i, right))),
            Side(bottom, i => new LedPoint(Lerp(w - 1, 0, i, bottom), h - 1)),
        };

        int rotate = start switch
        {
            StartCorner.TopLeft => 1,
            StartCorner.TopRight => 2,
            StartCorner.BottomRight => 3,
            _ => 0,
        };
        List<LedPoint> leds;
        if (clockwise)
        {
            leds = Enumerable.Range(0, 4).SelectMany(k => sides[(k + rotate) % 4]).ToList();
        }
        else
        {
            // Counter-clockwise from a corner is the reversed clockwise walk that ends at that corner.
            leds = Enumerable.Range(0, 4).SelectMany(k => sides[(k + rotate) % 4]).Reverse().ToList();
        }

        if (leds.Count > 0 && offset != 0)
        {
            int n = leds.Count;
            int s = ((offset % n) + n) % n;
            leds = leds.Skip(s).Concat(leds.Take(s)).ToList();
        }
        return (w, h, leds);
    }

    private static List<LedPoint> Side(int count, Func<int, LedPoint> at) =>
        Enumerable.Range(0, Math.Max(0, count)).Select(at).ToList();

    private static int Lerp(int from, int to, int i, int count) =>
        count <= 1 ? (from + to) / 2 : (int)Math.Round(from + (to - from) * (double)i / (count - 1));
}
