using System;

namespace DiskRescue.Core.Imaging
{
    /// <summary>A readable block source. Read throws IOException on an unreadable (bad) region.</summary>
    public interface IBlockReader
    {
        long Length { get; }
        byte[] Read(long offset, int length);
    }

    /// <summary>Reads a partition (or whole disk) from a physical device, offsets relative to the partition start.</summary>
    public sealed class RawDeviceReader : IBlockReader, IDisposable
    {
        private readonly RawDevice _dev;
        private readonly long _base;
        public long Length { get; }

        public RawDeviceReader(string physicalPath, long partitionOffset, long size)
        {
            _dev = RawDevice.Open(physicalPath, write: false);
            _base = partitionOffset;
            Length = size;
        }

        public byte[] Read(long offset, int length) => _dev.ReadAt(_base + offset, length);
        public void Dispose() => _dev.Dispose();
    }
}
