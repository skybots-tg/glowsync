using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GlowSync.Platform;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlowSync.Capture;

internal enum CaptureResult { NewFrame, NoChange, Unavailable }

internal sealed record MonitorInfo(string DeviceName, int Width, int Height, bool Primary, string Adapter)
{
    public string Label => $"{(Primary ? "Основной" : "Монитор")} {Width}×{Height} ({DeviceName.Replace(@"\\.\", "")}, {Adapter})";
}

/// <summary>
/// DXGI Desktop Duplication. Each frame is copied into a mip-mapped texture, reduced on the GPU,
/// and only a small mip level (~1/8 size) is read back, so CPU cost stays near zero.
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private ID3D11Texture2D? _staging;
    private int _sourceWidth, _sourceHeight;
    private int _mipLevel;
    private string _monitor = "";
    private long _retryAtTicks;
    private bool _failureLogged;

    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ModeRotation Rotation { get; private set; } = ModeRotation.Identity;
    public bool HasFrame { get; private set; }
    public string? CurrentMonitor { get; private set; }

    /// <summary>Minimum reduced-image size; chosen so each LED zone still covers many pixels.</summary>
    public int MinWidth { get; set; } = 256;
    public int MinHeight { get; set; } = 144;

    public static List<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    string adapterName = adapter.Description1.Description;
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var d = output.Description;
                            if (!d.AttachedToDesktop) continue;
                            var r = d.DesktopCoordinates;
                            list.Add(new MonitorInfo(d.DeviceName, r.Right - r.Left, r.Bottom - r.Top, r.Left == 0 && r.Top == 0, adapterName));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Monitor enumeration failed: " + ex.Message);
        }
        return list;
    }

    /// <summary>Grabs the newest desktop image if there is one. Never throws.</summary>
    public CaptureResult Update(string monitor)
    {
        if (!string.Equals(monitor, _monitor, StringComparison.OrdinalIgnoreCase))
        {
            _monitor = monitor;
            Reset();
            _retryAtTicks = 0;
        }

        if (_duplication == null)
        {
            if (Environment.TickCount64 < _retryAtTicks) return CaptureResult.Unavailable;
            if (!Initialize())
            {
                _retryAtTicks = Environment.TickCount64 + 1000;
                return CaptureResult.Unavailable;
            }
        }

        try
        {
            return Acquire();
        }
        catch (Exception ex)
        {
            if (!_failureLogged) Log.Warn("Capture failed, reinitializing: " + ex.Message);
            _failureLogged = true;
            Reset();
            _retryAtTicks = Environment.TickCount64 + 500;
            return CaptureResult.Unavailable;
        }
    }

    /// <summary>Forces re-creation (display mode change, GPU reset).</summary>
    public void Reset()
    {
        _staging?.Dispose();
        _staging = null;
        _mipView?.Dispose();
        _mipView = null;
        _mipTexture?.Dispose();
        _mipTexture = null;
        _duplication?.Dispose();
        _duplication = null;
        _context?.ClearState();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        HasFrame = false;
        CurrentMonitor = null;
    }

    private bool Initialize()
    {
        Reset();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            (uint Adapter, uint Output)? chosen = null, primary = null;
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var d = output.Description;
                            if (!d.AttachedToDesktop) continue;
                            if (chosen == null && _monitor.Length > 0 && string.Equals(d.DeviceName, _monitor, StringComparison.OrdinalIgnoreCase)) chosen = (a, o);
                            if (primary == null && d.DesktopCoordinates.Left == 0 && d.DesktopCoordinates.Top == 0) primary = (a, o);
                        }
                    }
                }
            }

            // A configured monitor that is gone (unplugged, renumbered) falls back to the primary one.
            var target = chosen ?? primary;
            if (target == null) return false;
            factory.EnumAdapters1(target.Value.Adapter, out var chosenAdapter).CheckError();
            using (chosenAdapter)
            {
                chosenAdapter.EnumOutputs(target.Value.Output, out var chosenOutput).CheckError();
                using (chosenOutput)
                {
                    var result = D3D11.D3D11CreateDevice(chosenAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                        out _device, out _context);
                    if (result.Failure || _device == null || _context == null) return false;

                    using (var output5 = chosenOutput.QueryInterfaceOrNull<IDXGIOutput5>())
                    {
                        // Lets Windows tone-map HDR desktops down to 8-bit for us.
                        if (output5 != null) _duplication = DuplicateOutput1(output5, _device);
                    }
                    if (_duplication == null)
                    {
                        using var output1 = chosenOutput.QueryInterface<IDXGIOutput1>();
                        _duplication = output1.DuplicateOutput(_device);
                    }

                    var desc = _duplication.Description;
                    Rotation = desc.Rotation;
                    CurrentMonitor = chosenOutput.Description.DeviceName;
                    _failureLogged = false;
                    Log.Info($"Capture started: {CurrentMonitor} {desc.ModeDescription.Width}x{desc.ModeDescription.Height} rot={desc.Rotation}");
                    return true;
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            // E_ACCESSDENIED on the secure desktop (UAC, lock screen), NOT_CURRENTLY_AVAILABLE, etc. — retried later.
            if (!_failureLogged) Log.Warn("Capture init failed: " + ex.ResultCode);
            _failureLogged = true;
            Reset();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Capture init failed: " + ex.Message);
            Reset();
            return false;
        }
    }

    /// <summary>
    /// IDXGIOutput5::DuplicateOutput1 called through the vtable (slot 26): the Vortice 3.8 wrapper
    /// for this method corrupts the stack and crashes with an access violation.
    /// </summary>
    private static unsafe IDXGIOutputDuplication? DuplicateOutput1(IDXGIOutput5 output, ID3D11Device device)
    {
        var vtable = *(IntPtr**)output.NativePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int*, IntPtr*, int>)vtable[26];
        int format = (int)Format.B8G8R8A8_UNorm;
        IntPtr result = IntPtr.Zero;
        int hr = method(output.NativePointer, device.NativePointer, 0, 1, &format, &result);
        const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
        if (hr == DxgiErrorUnsupported) return null; // old driver: fall back to DuplicateOutput
        new SharpGen.Runtime.Result(hr).CheckError(); // access denied / not available: retried by the caller
        return new IDXGIOutputDuplication(result);
    }

    private CaptureResult Acquire()
    {
        var result = _duplication!.AcquireNextFrame(0, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout) return CaptureResult.NoChange;
        if (result.Failure)
        {
            // ACCESS_LOST: mode change, full-screen app switch, desktop switch.
            Reset();
            _retryAtTicks = Environment.TickCount64 + 200;
            return CaptureResult.Unavailable;
        }

        try
        {
            if (info.LastPresentTime == 0 && HasFrame) return CaptureResult.NoChange; // pointer-only update

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureTextures(texture.Description);

            var ctx = _context!;
            ctx.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, texture, 0);
            ctx.GenerateMips(_mipView!);
            ctx.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel);

            var map = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int stride = Width * 4;
                int pitch = (int)map.RowPitch;
                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(map.DataPointer + y * pitch, Pixels, y * stride, stride);
                }
            }
            finally
            {
                ctx.Unmap(_staging!, 0);
            }

            HasFrame = true;
            return CaptureResult.NewFrame;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private void EnsureTextures(Texture2DDescription source)
    {
        if (_mipTexture != null && source.Width == _sourceWidth && source.Height == _sourceHeight) return;

        _staging?.Dispose();
        _mipView?.Dispose();
        _mipTexture?.Dispose();

        _sourceWidth = (int)source.Width;
        _sourceHeight = (int)source.Height;
        _mipLevel = 0;
        while (_mipLevel < 6 && (_sourceWidth >> (_mipLevel + 1)) >= MinWidth && (_sourceHeight >> (_mipLevel + 1)) >= MinHeight)
        {
            _mipLevel++;
        }
        Width = Math.Max(1, _sourceWidth >> _mipLevel);
        Height = Math.Max(1, _sourceHeight >> _mipLevel);
        Pixels = new byte[Width * Height * 4];

        _mipTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_sourceWidth,
            Height = (uint)_sourceHeight,
            MipLevels = (uint)(_mipLevel + 1),
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipView = _device.CreateShaderResourceView(_mipTexture);
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    public void Dispose() => Reset();
}
