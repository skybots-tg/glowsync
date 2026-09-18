using System;
using System.Globalization;
using System.Numerics;

namespace GlowSync.Core;

internal static class ColorMath
{
    /// <summary>HSV → RGB, all components 0..1, hue wraps.</summary>
    public static Vector3 Hsv(float h, float s, float v)
    {
        h -= MathF.Floor(h);
        float h6 = h * 6f;
        int sector = (int)h6 % 6;
        float f = h6 - MathF.Floor(h6);
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return sector switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q),
        };
    }

    public static (float H, float S, float V) ToHsv(Vector3 c)
    {
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        float min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float d = max - min;
        float h = 0;
        if (d > 1e-6f)
        {
            if (max == c.X) h = (c.Y - c.Z) / d / 6f;
            else if (max == c.Y) h = ((c.Z - c.X) / d + 2) / 6f;
            else h = ((c.X - c.Y) / d + 4) / 6f;
            if (h < 0) h += 1;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static Vector3 ParseHex(string? hex, Vector3 fallback = default)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return fallback;
        return new Vector3(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    public static string ToHex(Vector3 c) =>
        $"#{ToByte(c.X):X2}{ToByte(c.Y):X2}{ToByte(c.Z):X2}";

    public static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
