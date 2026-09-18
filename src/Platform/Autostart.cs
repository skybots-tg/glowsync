using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace GlowSync.Platform;

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlowSync";
    public const string Argument = "--autostart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Contains(Paths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{Paths.ExecutablePath}\" {Argument}");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart update failed", ex);
        }
    }
}

/// <summary>Helps retire the original Skydimo app, which would otherwise grab the COM port first.</summary>
internal static class SkydimoApp
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string BackupKey = @"Software\GlowSync\SkydimoAutostartBackup";
    private const string ValueName = "Skydimo";

    public static bool IsRunning => Process.GetProcessesByName("SkyDimo").Length > 0;

    public static bool InAutostart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static bool HasBackup
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(BackupKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    /// <summary>Closes Skydimo and moves its autostart entry into a backup key (restorable).</summary>
    public static void Retire()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (run.GetValue(ValueName) is string command)
            {
                using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
                backup.SetValue(ValueName, command);
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        foreach (var p in Process.GetProcessesByName("SkyDimo"))
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not close Skydimo: " + ex.Message);
                }
            }
        }
        Log.Info("Skydimo closed and removed from autostart (backup kept)");
    }

    public static void RestoreAutostart()
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKey, writable: true);
        if (backup?.GetValue(ValueName) is not string command) return;
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(ValueName, command);
        backup.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info("Skydimo autostart restored");
    }
}
