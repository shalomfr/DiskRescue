using System;
using System.Text;

namespace DiskRescue.Core.Ntfs
{
    /// <summary>Parsed NTFS boot-sector parameters needed to walk the MFT.</summary>
    public sealed class NtfsBoot
    {
        public int BytesPerSector;
        public int SectorsPerCluster;
        public long MftCluster;
        public long MftMirrCluster;
        public int MftRecordSize;

        public long ClusterSize => (long)BytesPerSector * SectorsPerCluster;

        public static NtfsBoot Parse(byte[] s)
        {
            if (Encoding.ASCII.GetString(s, 3, 4) != "NTFS")
                throw new InvalidOperationException("לא נמצאה חתימת NTFS בסקטור האתחול.");

            var b = new NtfsBoot
            {
                BytesPerSector = BitConverter.ToUInt16(s, 0x0B),
                SectorsPerCluster = s[0x0D],
                MftCluster = BitConverter.ToInt64(s, 0x30),
                MftMirrCluster = BitConverter.ToInt64(s, 0x38),
            };

            sbyte clustersPerRec = (sbyte)s[0x40];
            b.MftRecordSize = clustersPerRec >= 0
                ? clustersPerRec * (int)b.ClusterSize
                : 1 << (-clustersPerRec);   // negative => 2^(-value) bytes (usually -10 => 1024)

            if (b.BytesPerSector <= 0 || b.SectorsPerCluster <= 0 || b.MftRecordSize < 512)
                throw new InvalidOperationException("פרמטרי NTFS לא תקינים בסקטור האתחול.");
            return b;
        }
    }
}
