using System;
using System.IO;

namespace GlowSync.Platform;

/// <summary>
/// Tiny size-capped logger: at most two files of <see cref="MaxBytes"/> each,
/// so the log folder can never grow into gigabytes.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Sync = new();
    private static string? _path;

    public static string Directory => Paths.LogDir;

    public static void Init()
    {
        System.IO.Directory.CreateDirectory(Paths.LogDir);
        _path = Path.Combine(Paths.LogDir, "glowsync.log");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        if (_path == null) return;
        lock (Sync)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}

internal static class Paths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlowSync");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    public static string LogDir => Path.Combine(DataDir, "logs");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GlowSync.exe");
}
