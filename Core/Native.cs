using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.Core
{
    /// <summary>Thin P/Invoke layer for raw block-device access (read/write by sector offset).</summary>
    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurity,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplate);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPtr, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint toWrite, out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushFileBuffers(SafeFileHandle h);
    }
}
