using System.Collections.Generic;

namespace GlowSync.Device;

/// <summary>
/// Known Skydimo controllers: LED count and strip segments (left, top, right[, bottom]),
/// taken from Skydimo's controler_config/Controller.json.
/// </summary>
internal static class SkydimoModels
{
    public sealed record Model(string Name, int LedCount, int[] Lines);

    private static readonly Dictionary<string, Model> Table = new()
    {
        ["SK0121"] = new("SK0121", 51, new[] { 13, 25, 13 }),
        ["SK0124"] = new("SK0124", 54, new[] { 14, 26, 14 }),
        ["SK0127"] = new("SK0127", 65, new[] { 17, 31, 17 }),
        ["SK0132"] = new("SK0132", 77, new[] { 20, 37, 20 }),
        ["SK0134"] = new("SK0134", 71, new[] { 15, 41, 15 }),
        ["SK0149"] = new("SK0149", 107, new[] { 19, 69, 19 }),
        ["SK0L21"] = new("SK0L21", 76, new[] { 13, 25, 13, 25 }),
        ["SK0L24"] = new("SK0L24", 80, new[] { 14, 26, 14, 26 }),
        ["SK0L27"] = new("SK0L27", 96, new[] { 17, 31, 17, 31 }),
        ["SK0L32"] = new("SK0L32", 114, new[] { 20, 37, 20, 37 }),
        ["SK0L34"] = new("SK0L34", 112, new[] { 15, 41, 15, 41 }),
        ["SK0201"] = new("SK0201", 40, new[] { 20, 20 }),
        ["SK0202"] = new("SK0202", 60, new[] { 30, 30 }),
        ["SK0204"] = new("SK0204", 50, new[] { 25, 25 }),
        ["SK0301"] = new("SK0301", 16, new[] { 16 }),
        ["SK0410"] = new("SK0410", 290, new[] { 290 }),
        ["SK0801"] = new("SK0801", 2, new[] { 2 }),
        ["SK0802"] = new("SK0802", 18, new[] { 18 }),
        ["SK0901"] = new("SK0901", 14, new[] { 14 }),
        ["SK0E01"] = new("SK0E01", 16, new[] { 16 }),
        ["SK0F01"] = new("SK0F01", 58, new[] { 58 }),
        ["SK0H01"] = new("SK0H01", 2, new[] { 2 }),
        ["SK0I01"] = new("SK0I01", 32, new[] { 32 }),
        ["SK0J01"] = new("SK0J01", 120, new[] { 120 }),
        ["SK0J02"] = new("SK0J02", 114, new[] { 114 }),
        ["SK0K01"] = new("SK0K01", 120, new[] { 120 }),
        ["SK0N03"] = new("SK0N03", 253, new[] { 253 }),
        ["SKA124"] = new("SKA124", 70, new[] { 70 }),
        ["SKA127"] = new("SKA127", 81, new[] { 81 }),
        ["SKA132"] = new("SKA132", 95, new[] { 95 }),
        ["SKA134"] = new("SKA134", 95, new[] { 95 }),
    };

    public static Model? Find(string? name) =>
        name != null && Table.TryGetValue(name.ToUpperInvariant(), out var m) ? m : null;

    /// <summary>Maps segment list to (left, top, right, bottom) counts for the layout editor.</summary>
    public static (int Left, int Top, int Right, int Bottom) Sides(Model m) => m.Lines.Length switch
    {
        1 => (0, m.Lines[0], 0, 0),
        2 => (m.Lines[0], 0, m.Lines[1], 0),
        3 => (m.Lines[0], m.Lines[1], m.Lines[2], 0),
        _ => (m.Lines[0], m.Lines[1], m.Lines[2], m.Lines[3]),
    };
}
