using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GlowSync.Platform;

namespace GlowSync.Device;

internal sealed record PortInfo(string Name, string Description, string PnpId)
{
    public bool IsUsb => PnpId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                         || PnpId.StartsWith(@"FTDIBUS\", StringComparison.OrdinalIgnoreCase)
                         || PnpId.Contains("VID_", StringComparison.OrdinalIgnoreCase);

    public bool IsBluetooth => PnpId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);

    /// <summary>WCH CH340/CH341 — the USB-UART chip Skydimo controllers use.</summary>
    public bool IsCh34x => PnpId.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase);

    public int Number => int.TryParse(Name.AsSpan(3), out var n) ? n : int.MaxValue;
}

internal static partial class PortEnumerator
{
    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComName();

    public static List<PortInfo> GetPorts()
    {
        var result = new List<PortInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"] as string ?? "";
                    var match = ComName().Match(name);
                    if (!match.Success) continue;
                    var port = match.Groups[1].Value.ToUpperInvariant();
                    var desc = name.Replace(match.Value, "").Trim();
                    result.Add(new PortInfo(port, desc, obj["PNPDeviceID"] as string ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WMI port enumeration failed, falling back to registry: " + ex.Message);
        }

        if (result.Count == 0)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
                foreach (var valueName in key?.GetValueNames() ?? Array.Empty<string>())
                {
                    if (key!.GetValue(valueName) is not string port) continue;
                    bool bt = valueName.Contains("Bth", StringComparison.OrdinalIgnoreCase);
                    // Unknown bus: treat as probe-able unless it is clearly Bluetooth (probing BT SPP ports can hang).
                    result.Add(new PortInfo(port.ToUpperInvariant(), valueName, bt ? "BTHENUM" : @"USB\UNKNOWN"));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Registry port enumeration failed: " + ex.Message);
            }
        }

        return result.GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Number).ToList();
    }
}
