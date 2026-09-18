using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GlowSync.Device;

/// <summary>
/// Minimal synchronous serial port over CreateFile/WriteFile.
/// Deliberately avoids System.IO.Ports: its background event thread is known to crash
/// the process when a USB-serial adapter is unplugged while open.
/// </summary>
internal sealed class NativeSerialPort : IDisposable
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PurgeTxClear = 0x0004, PurgeRxClear = 0x0008;
    public const int ErrorAccessDenied = 5, ErrorFileNotFound = 2;

    private readonly SafeFileHandle _handle;

    public string Name { get; }

    private NativeSerialPort(string name, SafeFileHandle handle)
    {
        Name = name;
        _handle = handle;
    }

    /// <summary>Opens the port 8N1. Throws <see cref="Win32Exception"/> with NativeErrorCode on failure.</summary>
    public static NativeSerialPort Open(string name, int baudRate, bool dtr)
    {
        var handle = CreateFileW(@"\\.\" + name, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            if (!GetCommState(handle, ref dcb)) throw new Win32Exception(Marshal.GetLastWin32Error());
            dcb.BaudRate = (uint)baudRate;
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;
            // fBinary=1, DTR control enable/disable, RTS disabled, no flow control, fAbortOnError=0.
            dcb.Flags = 0x1u | ((dtr ? 1u : 0u) << 4);
            if (!SetCommState(handle, ref dcb)) throw new Win32Exception(Marshal.GetLastWin32Error());

            // Reads return immediately with whatever is buffered; writes give up after 500 ms.
            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 0,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 500,
            };
            if (!SetCommTimeouts(handle, ref timeouts)) throw new Win32Exception(Marshal.GetLastWin32Error());
            PurgeComm(handle, PurgeRxClear | PurgeTxClear);
            return new NativeSerialPort(name, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public unsafe void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            if (!WriteFile(_handle, p, data.Length, out int written, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (written != data.Length)
                throw new TimeoutException($"Serial write timeout ({written}/{data.Length})");
        }
    }

    /// <summary>Non-blocking read of buffered bytes.</summary>
    public unsafe int Read(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            if (!ReadFile(_handle, p, buffer.Length, out int read, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return read;
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle h, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle h, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle h, ref CommTimeouts t);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle h, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(SafeFileHandle h, byte* buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(SafeFileHandle h, byte* buffer, int count, out int read, IntPtr overlapped);
}
