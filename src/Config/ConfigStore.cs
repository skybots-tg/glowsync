using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using GlowSync.Platform;

namespace GlowSync.Config;

/// <summary>
/// Owns the live <see cref="AppConfig"/>: loads it, tracks every property change,
/// and persists it with a debounced atomic write (temp file + replace, with a .bak copy).
/// </summary>
internal sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Timer _saveTimer;
    private readonly object _saveLock = new();
    private int _revision;
    private AppConfig _config;

    /// <summary>Raised on the thread that made the change. Sender is the object whose property changed.</summary>
    public event Action<object, string>? Changed;

    /// <summary>Raised after the whole config object was swapped (e.g. import).</summary>
    public event Action? Replaced;

    public AppConfig Config => _config;

    /// <summary>Increments on every change; background threads poll it to rebuild caches.</summary>
    public int Revision => Volatile.Read(ref _revision);

    public bool IsFirstRun { get; }

    public ConfigStore()
    {
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        var loaded = TryLoad(Paths.ConfigFile) ?? TryLoad(Paths.ConfigFile + ".bak");
        IsFirstRun = loaded == null && !File.Exists(Paths.ConfigFile);
        _config = loaded ?? new AppConfig();
        _config.Normalize();
        Attach(_config);
    }

    public void Replace(AppConfig config)
    {
        Detach(_config);
        config.Normalize();
        _config = config;
        Attach(config);
        Interlocked.Increment(ref _revision);
        ScheduleSave();
        Replaced?.Invoke();
    }

    public void ScheduleSave() => _saveTimer.Change(800, Timeout.Infinite);

    public void SaveNow()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var json = JsonSerializer.Serialize(_config, JsonOptions);
                var tmp = Paths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(Paths.ConfigFile))
                {
                    File.Replace(tmp, Paths.ConfigFile, Paths.ConfigFile + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, Paths.ConfigFile);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Config save failed", ex);
            }
        }
    }

    public static AppConfig Clone(AppConfig source) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;

    private static AppConfig? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error($"Config load failed ({Path.GetFileName(path)})", ex);
            return null;
        }
    }

    private void Attach(AppConfig c)
    {
        foreach (var node in Nodes(c)) node.PropertyChanged += OnNodeChanged;
    }

    private void Detach(AppConfig c)
    {
        foreach (var node in Nodes(c)) node.PropertyChanged -= OnNodeChanged;
    }

    private static System.Collections.Generic.IEnumerable<INotifyPropertyChanged> Nodes(AppConfig c)
    {
        yield return c;
        yield return c.Device;
        yield return c.Layout;
        yield return c.Screen;
        yield return c.Color;
        yield return c.Effects;
        yield return c.Behavior;
        yield return c.Night;
        foreach (var h in c.Hotkeys) yield return h;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        Interlocked.Increment(ref _revision);
        ScheduleSave();
        Changed?.Invoke(sender!, e.PropertyName ?? "");
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveNow();
    }
}
