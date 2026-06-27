using System;
using System.Collections.Generic;
using System.Text;

namespace DiskRescue.Core.Ntfs
{
    /// <summary>One parsed MFT entry: name, parent, flags (deleted/dir), and its main data stream.</summary>
    public sealed class NtfsFileEntry
    {
        public long RecordNumber;
        public string Name = "";
        public byte NameNamespace = 255;   // 0 POSIX, 1 Win32, 2 DOS, 3 Win32&DOS
        public long ParentRecord;
        public bool IsDirectory;
        public bool InUse;
        public bool HasData;
        public bool Resident;
        public byte[] ResidentData;
        public List<Run> Runs;
        public long DataSize;
        public string FullPath;

        public bool IsDeleted => !InUse;
    }

    public static class MftRecord
    {
        private const uint ATTR_FILE_NAME = 0x30;
        private const uint ATTR_DATA = 0x80;
        private const uint ATTR_END = 0xFFFFFFFF;

        public static NtfsFileEntry Parse(byte[] rec, int bytesPerSector, long recordNumber)
        {
            if (rec.Length < 0x18 || Encoding.ASCII.GetString(rec, 0, 4) != "FILE") return null;
            ApplyFixup(rec, bytesPerSector);

            var e = new NtfsFileEntry { RecordNumber = recordNumber };
            ushort flags = BitConverter.ToUInt16(rec, 0x16);
            e.InUse = (flags & 0x01) != 0;
            e.IsDirectory = (flags & 0x02) != 0;

            int off = BitConverter.ToUInt16(rec, 0x14);
            while (off + 8 <= rec.Length)
            {
                uint type = BitConverter.ToUInt32(rec, off);
                if (type == ATTR_END) break;
                int totalLen = (int)BitConverter.ToUInt32(rec, off + 4);
                if (totalLen <= 0 || off + totalLen > rec.Length) break;
                byte nonResident = rec[off + 8];

                if (type == ATTR_FILE_NAME)
                {
                    int contentOff = BitConverter.ToUInt16(rec, off + 0x14);
                    int cs = off + contentOff;
                    if (cs + 0x42 <= rec.Length)
                    {
                        long parentRef = BitConverter.ToInt64(rec, cs) & 0x0000FFFFFFFFFFFFL;
                        byte nameLen = rec[cs + 0x40];
                        byte ns = rec[cs + 0x41];
                        if (cs + 0x42 + nameLen * 2 <= rec.Length)
                        {
                            string name = Encoding.Unicode.GetString(rec, cs + 0x42, nameLen * 2);
                            if (BetterName(e.NameNamespace, ns))
                            { e.Name = name; e.NameNamespace = ns; e.ParentRecord = parentRef; }
                        }
                    }
                }
                else if (type == ATTR_DATA && rec[off + 9] == 0) // unnamed main data stream
                {
                    e.HasData = true;
                    if (nonResident == 0)
                    {
                        int contentLen = (int)BitConverter.ToUInt32(rec, off + 0x10);
                        int contentOff = BitConverter.ToUInt16(rec, off + 0x14);
                        e.Resident = true;
                        e.DataSize = contentLen;
                        e.ResidentData = new byte[Math.Max(0, contentLen)];
                        int avail = Math.Min(contentLen, rec.Length - (off + contentOff));
                        if (avail > 0) Array.Copy(rec, off + contentOff, e.ResidentData, 0, avail);
                    }
                    else
                    {
                        e.Resident = false;
                        e.DataSize = BitConverter.ToInt64(rec, off + 0x30); // real size
                        int runOff = BitConverter.ToUInt16(rec, off + 0x20);
                        e.Runs = DataRuns.Parse(rec, off + runOff);
                    }
                }

                off += totalLen;
            }
            return e;
        }

        private static bool BetterName(byte cur, byte cand)
        {
            int Rank(byte ns) => (ns == 1 || ns == 3) ? 3 : ns == 0 ? 2 : ns == 2 ? 1 : 0;
            return Rank(cand) > (cur == 255 ? -1 : Rank(cur));
        }

        /// <summary>Apply the Update Sequence Array: restore the real last 2 bytes of each sector.</summary>
        private static void ApplyFixup(byte[] rec, int bps)
        {
            ushort usaOff = BitConverter.ToUInt16(rec, 0x04);
            ushort usaCount = BitConverter.ToUInt16(rec, 0x06);
            if (usaCount == 0 || bps <= 0) return;
            for (int i = 1; i < usaCount; i++)
            {
                int sectorEnd = i * bps - 2;
                int usaEntry = usaOff + i * 2;
                if (sectorEnd + 2 > rec.Length || usaEntry + 2 > rec.Length) break;
                rec[sectorEnd] = rec[usaEntry];
                rec[sectorEnd + 1] = rec[usaEntry + 1];
            }
        }
    }
}
