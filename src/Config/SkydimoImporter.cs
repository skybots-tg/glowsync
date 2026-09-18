using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GlowSync.Device;

namespace GlowSync.Config;

/// <summary>
/// Reads a Skydimo installation (%LOCALAPPDATA%\SkyDimo) and transfers its settings into an <see cref="AppConfig"/>.
/// </summary>
internal static class SkydimoImporter
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyDimo");

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Skydimo");

    public static bool IsAvailable => File.Exists(Path.Combine(DataDir, "global.json"));

    /// <summary>Applies Skydimo settings on top of <paramref name="cfg"/>. Returns a human-readable report (Russian).</summary>
    public static List<string> Import(AppConfig cfg)
    {
        var report = new List<string>();
        var global = ReadJson(Path.Combine(DataDir, "global.json"));

        // --- Controller (brightness, mode, model) ---
        var controllerFile = new DirectoryInfo(Path.Combine(DataDir, "controllers")) is { Exists: true } dir
            ? dir.GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        string? model = null, serial = null;
        if (controllerFile != null)
        {
            var parts = Path.GetFileNameWithoutExtension(controllerFile.Name).Split('_', 2);
            model = parts[0];
            serial = parts.Length > 1 ? parts[1] : null;

            var ctl = ReadJson(controllerFile.FullName);
            if (ctl is { } c)
            {
                if (Str(c, "kindName") is { Length: > 0 } kind) model = kind;
                if (Num(c, "brightness") is { } b)
                {
                    cfg.Brightness = (int)Math.Round(Math.Clamp(b, 0, 1) * 100);
                    report.Add($"Яркость: {cfg.Brightness}%");
                }
                if (Bool(c, "enable") is { } en) cfg.Enabled = en;
                if (Num(c, "nbLeds") is { } n && n > 0) cfg.Device.LedCount = (int)n;
                if (Str(c, "workMode") is { } mode)
                {
                    cfg.Mode = MapMode(mode, Str(c, "subMode"));
                    report.Add($"Режим: {ModeName(cfg.Mode)}");
                }
            }
        }

        cfg.Device.Model = model;
        cfg.Device.Serial = serial;
        cfg.Device.Protocol = DeviceProtocol.Skydimo;
        cfg.Device.Port = DeviceConfig.AutoPort;
        if (model != null) report.Add($"Устройство: {model}" + (serial != null ? $" (S/N {serial})" : "") + $", {cfg.Device.LedCount} светодиодов");

        string deviceId = model != null && serial != null ? $"{model}:{serial}" : "";

        // --- LED map and screen-sync tuning ---
        var effect = ReadJson(Path.Combine(DataDir, "effects", "id_effect_screenSync_fullScreen.json"));
        JsonElement? devEffect = null;
        if (effect is { ValueKind: JsonValueKind.Object } e)
        {
            devEffect = e.TryGetProperty(deviceId, out var d) ? d
                : e.EnumerateObject().Select(p => (JsonElement?)p.Value).FirstOrDefault();
        }

        JsonElement? map = devEffect is { } de && de.TryGetProperty("map", out var m) ? m : null;
        if (map == null && model != null)
        {
            map = ReadJson(Path.Combine(InstallDir, "controler_config", model + ".json"));
        }
        if (map is { } mp && ReadLedMap(mp) is { } leds)
        {
            var known = SkydimoModels.Find(model);
            if (known != null)
            {
                var (l, t, r, bt) = SkydimoModels.Sides(known);
                cfg.Layout.Left = l;
                cfg.Layout.Top = t;
                cfg.Layout.Right = r;
                cfg.Layout.Bottom = bt;
            }
            cfg.Layout.Start = StartCorner.BottomLeft;
            cfg.Layout.Clockwise = true;
            cfg.Layout.Offset = 0;
            cfg.Layout.GridWidth = leds.Width;
            cfg.Layout.GridHeight = leds.Height;
            cfg.Layout.Leds = leds.Points;
            report.Add($"Раскладка светодиодов: {leds.Points.Count} шт., сетка {leds.Width}×{leds.Height}");
        }
        if (devEffect is { } fx)
        {
            if (Num(fx, "saturationStrength") is { } sat) cfg.Screen.Saturation = (int)Math.Clamp(sat, -100, 100);
            if (Num(fx, "blurStrength") is { } blur) cfg.Screen.SpatialBlur = (int)Math.Clamp(blur, 0, 100);
            report.Add($"Насыщенность: {cfg.Screen.Saturation}, размытие: {cfg.Screen.SpatialBlur}");
        }

        if (global is { } g)
        {
            if (Num(g, "devUpdateInterval") is { } interval && interval > 0)
            {
                cfg.Screen.Fps = (int)Math.Clamp(Math.Round(1000.0 / interval), 10, 60);
                report.Add($"Частота обновления: {cfg.Screen.Fps} кадров/с");
            }

            bool? wipe = null;
            if (g.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("screenSync", out var ss)
                && ss.TryGetProperty("autoWipeDarkSide", out var w) && w.ValueKind == JsonValueKind.Object)
            {
                wipe = w.TryGetProperty(deviceId, out var wv) ? wv.ValueKind == JsonValueKind.True : null;
            }
            wipe ??= Bool(g, "screenSyncAutoWipeDarkSide");
            if (wipe is { } wb)
            {
                cfg.Screen.RemoveBlackBars = wb;
                report.Add("Удаление чёрных полос: " + (wb ? "вкл" : "выкл"));
            }

            if (g.TryGetProperty("hotkeyBinding", out var hb) && hb.TryGetProperty("id", out var hid) && hid.TryGetProperty("hotkey", out var hk))
            {
                int count = 0;
                count += ImportHotkey(cfg, hk, "turnOffOn", HotkeyAction.Toggle);
                count += ImportHotkey(cfg, hk, "increaseBrightness", HotkeyAction.BrightnessUp);
                count += ImportHotkey(cfg, hk, "decreaseBrightness", HotkeyAction.BrightnessDown);
                count += ImportHotkey(cfg, hk, "nextEffect", HotkeyAction.NextMode);
                count += ImportHotkey(cfg, hk, "preEffect", HotkeyAction.PrevMode);
                if (count > 0) report.Add($"Горячие клавиши: {count}");
            }
        }

        cfg.ImportedFrom = $"Skydimo, {DateTime.Now:yyyy-MM-dd HH:mm}";
        return report;
    }

    private static int ImportHotkey(AppConfig cfg, JsonElement hk, string name, HotkeyAction action)
    {
        if (!hk.TryGetProperty(name, out var h) || Num(h, "keyCode") is not { } qtKey || Num(h, "modifiers") is not { } qtMods) return 0;
        int vk = QtKeyToVirtualKey((int)qtKey);
        if (vk == 0) return 0;
        long mods = (long)qtMods;
        int flags = 0;
        if ((mods & 0x02000000) != 0) flags |= HotkeyBinding.ModShift;
        if ((mods & 0x04000000) != 0) flags |= HotkeyBinding.ModControl;
        if ((mods & 0x08000000) != 0) flags |= HotkeyBinding.ModAlt;
        if ((mods & 0x10000000) != 0) flags |= HotkeyBinding.ModWin;
        var binding = cfg.Hotkeys.FirstOrDefault(b => b.Action == action);
        if (binding == null) cfg.Hotkeys.Add(binding = new HotkeyBinding { Action = action });
        binding.Modifiers = flags;
        binding.Key = vk;
        return 1;
    }

    /// <summary>Qt::Key → Windows virtual key for the keys a hotkey can reasonably use.</summary>
    internal static int QtKeyToVirtualKey(int k)
    {
        if (k is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return k; // digits, letters
        if (k is >= 0x01000030 and <= 0x01000047) return 0x70 + (k - 0x01000030); // F1..F24
        return k switch
        {
            0x20 => 0x20,
            0x01000012 => 0x25, // Left
            0x01000013 => 0x26, // Up
            0x01000014 => 0x27, // Right
            0x01000015 => 0x28, // Down
            0x01000010 => 0x24, // Home
            0x01000011 => 0x23, // End
            0x01000016 => 0x21, // PageUp
            0x01000017 => 0x22, // PageDown
            0x01000006 => 0x2D, // Insert
            0x01000007 => 0x2E, // Delete
            0x01000009 => 0x2C, // Print
            0x01000008 => 0x13, // Pause
            _ => 0,
        };
    }

    private static EffectMode MapMode(string workMode, string? subMode) => workMode switch
    {
        "id.effect.screenSync" => EffectMode.Screen,
        "id.effect.dynamicMusic" => EffectMode.Music,
        "id.effect.singleColor" => EffectMode.Static,
        "id.effect.marquee" => EffectMode.Comet,
        "id.effect.colorful" when subMode != null && subMode.Contains("breath", StringComparison.OrdinalIgnoreCase) => EffectMode.Breathing,
        "id.effect.colorful" or "id.effect.graphics" => EffectMode.Rainbow,
        _ => EffectMode.Screen,
    };

    internal static string ModeName(EffectMode mode) => mode switch
    {
        EffectMode.Screen => "Экран",
        EffectMode.Music => "Музыка",
        EffectMode.Rainbow => "Радуга",
        EffectMode.ColorCycle => "Переливание",
        EffectMode.Breathing => "Дыхание",
        EffectMode.Static => "Один цвет",
        EffectMode.Comet => "Комета",
        _ => mode.ToString(),
    };

    private sealed record LedMap(int Width, int Height, List<LedPoint> Points);

    private static LedMap? ReadLedMap(JsonElement map)
    {
        if (!map.TryGetProperty("ledMap", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var items = arr.EnumerateArray()
            .Select(el => (Id: Num(el, "id") ?? 0, X: (int)(Num(el, "x") ?? 0), Y: (int)(Num(el, "y") ?? 0)))
            .OrderBy(t => t.Id)
            .ToList();
        if (items.Count == 0) return null;
        int w = (int)(Num(map, "screenWidth") ?? items.Max(i => i.X) + 1);
        int h = (int)(Num(map, "screenHeight") ?? items.Max(i => i.Y) + 1);
        return new LedMap(Math.Max(1, w), Math.Max(1, h), items.Select(i => new LedPoint(i.X, i.Y)).ToList());
    }

    private static JsonElement? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Skydimo writes some files with a UTF-8 BOM; File.ReadAllText strips it.
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true });
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
