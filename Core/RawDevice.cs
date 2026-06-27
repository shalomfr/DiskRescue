using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.Core
{
    /// <summary>
    /// Raw access to a volume/disk device (e.g. "\\.\E:").
    /// All reads/writes are sector-aligned. Opens read-only by default; write access is explicit.
    /// </summary>
    public sealed class RawDevice : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public string DevicePath { get; }
        public bool Writable { get; }

        private RawDevice(string path, SafeFileHandle handle, bool writable)
        {
            DevicePath = path; _handle = handle; Writable = writable;
        }

        public static RawDevice Open(string devicePath, bool write)
        {
            uint access = write ? (Native.GENERIC_READ | Native.GENERIC_WRITE) : Native.GENERIC_READ;
            uint flags = write ? Native.FILE_FLAG_WRITE_THROUGH : 0;
            var h = Native.CreateFile(devicePath, access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, flags, IntPtr.Zero);
            if (h.IsInvalid)
                throw new IOException($"CreateFile('{devicePath}') failed. Win32 error {Marshal.GetLastWin32Error()}.");
            return new RawDevice(devicePath, h, write);
        }

        public byte[] ReadAt(long offset, int length)
        {
            if (!Native.SetFilePointerEx(_handle, offset, out _, 0))
                throw new IOException($"Seek to {offset} failed. Win32 error {Marshal.GetLastWin32Error()}.");
            var buf = new byte[length];
            if (!Native.ReadFile(_handle, buf, (uint)length, out uint read, IntPtr.Zero))
                throw new IOException($"Read at {offset} failed. Win32 error {Marshal.GetLastWin32Error()}.");
            if (read != length)
                throw new IOException($"Short read at {offset}: got {read}/{length} bytes.");
            return buf;
        }

        public void WriteAt(long offset, byte[] data)
        {
            if (!Writable) throw new InvalidOperationException("Device opened read-only.");
            if (!Native.SetFilePointerEx(_handle, offset, out _, 0))
                throw new IOException($"Seek(w) to {offset} failed. Win32 error {Marshal.GetLastWin32Error()}.");
            if (!Native.WriteFile(_handle, data, (uint)data.Length, out uint written, IntPtr.Zero))
                throw new IOException($"Write at {offset} failed. Win32 error {Marshal.GetLastWin32Error()}.");
            if (written != data.Length)
                throw new IOException($"Short write at {offset}: wrote {written}/{data.Length} bytes.");
            Native.FlushFileBuffers(_handle);
        }

        public void Dispose()
        {
            if (!_handle.IsClosed) _handle.Dispose();
        }
    }
}
