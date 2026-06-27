using System;
using System.Collections.Generic;
using System.Text;

namespace DiskRescue.Core.Ntfs
{
    /// <summary>
    /// Builds a tiny but structurally valid NTFS image in memory, for safely testing the MFT parser and
    /// recovery end-to-end without admin rights or any real disk. Layout (cluster = 1024B, record = 1024B):
    ///   cluster 0..3  : boot + slack
    ///   cluster 4..33 : $MFT (30 records)
    ///   cluster 34..35: data of the non-resident test file
    /// </summary>
    public static class SyntheticNtfs
    {
        public const int BytesPerSector = 512;
        public const int SectorsPerCluster = 2;       // cluster = 1024
        public const int ClusterSize = 1024;
        public const int RecordSize = 1024;
        public const int MftCluster = 4;
        public const int MftRecords = 30;
        public const int DataCluster = MftCluster + MftRecords;  // 34
        public const int TotalClusters = 40;

        public const string ResidentText = "Hello NTFS recovery!";
        public const int PhotoSize = 2000;

        public static byte[] Build()
        {
            var img = new byte[TotalClusters * ClusterSize];

            // ---- boot sector ----
            Ascii(img, 3, "NTFS    ");
            U16(img, 0x0B, BytesPerSector);
            img[0x0D] = SectorsPerCluster;
            U64(img, 0x30, MftCluster);
            U64(img, 0x38, 2);
            img[0x40] = 1;                 // clusters per MFT record (positive => 1 cluster = 1024B)
            img[510] = 0x55; img[511] = 0xAA;

            // ---- MFT records ----
            // 0: $MFT (non-resident DATA whose runs map the MFT clusters)
            WriteRecord(img, 0, dir: false, name: "$MFT", parent: 5,
                data: NonResidentData(realSize: MftRecords * ClusterSize,
                                      runs: Runs((MftRecords, MftCluster))));
            // 5: root directory
            WriteRecord(img, 5, dir: true, name: ".", parent: 5, data: null);
            // 24: resident file at root
            WriteRecord(img, 24, dir: false, name: "hello.txt", parent: 5,
                data: ResidentData(Encoding.ASCII.GetBytes(ResidentText)));
            // 25: a folder at root
            WriteRecord(img, 25, dir: true, name: "MyFolder", parent: 5, data: null);
            // 26: non-resident file inside MyFolder
            WriteRecord(img, 26, dir: false, name: "photo.bin", parent: 25,
                data: NonResidentData(realSize: PhotoSize, runs: Runs((2, DataCluster))));

            // ---- file content for the non-resident file ----
            int contentOff = DataCluster * ClusterSize;
            for (int i = 0; i < PhotoSize; i++) img[contentOff + i] = (byte)(i % 251);

            return img;
        }

        public static byte[] ExpectedPhoto()
        {
            var b = new byte[PhotoSize];
            for (int i = 0; i < PhotoSize; i++) b[i] = (byte)(i % 251);
            return b;
        }

        // ---------- record / attribute assembly ----------

        private sealed class Attr { public byte[] Bytes; }

        private static void WriteRecord(byte[] img, int recNo, bool dir, string name, long parent, Attr data)
        {
            int baseOff = MftCluster * ClusterSize + recNo * RecordSize;
            var rec = new byte[RecordSize];
            Ascii(rec, 0, "FILE");
            U16(rec, 0x04, 0x30);     // USA offset
            U16(rec, 0x06, 3);        // USA count (1 sig + 2 entries for 2 sectors)
            U16(rec, 0x10, 1);        // sequence
            U16(rec, 0x12, 1);        // link count
            U16(rec, 0x14, 0x38);     // first attribute offset
            U16(rec, 0x16, (ushort)(0x01 | (dir ? 0x02 : 0x00))); // in-use (+dir)
            U32(rec, 0x1C, RecordSize);

            int off = 0x38;
            off = AppendAttr(rec, off, FileNameAttr(parent, name));
            if (data != null) off = AppendAttr(rec, off, data);
            U32(rec, off, 0xFFFFFFFF); // end marker
            U32(rec, 0x18, (uint)(off + 8)); // used size

            // reverse-fixup: put signature at each sector end; USA entries hold the real (zero) bytes
            U16(rec, 0x30, 0x0001);   // signature
            U16(rec, 0x32, 0x0000);   // entry for sector 0 end
            U16(rec, 0x34, 0x0000);   // entry for sector 1 end
            rec[510] = 0x01; rec[511] = 0x00;
            rec[1022] = 0x01; rec[1023] = 0x00;

            Array.Copy(rec, 0, img, baseOff, RecordSize);
        }

        private static int AppendAttr(byte[] rec, int off, Attr a)
        {
            Array.Copy(a.Bytes, 0, rec, off, a.Bytes.Length);
            return off + a.Bytes.Length;
        }

        private static Attr FileNameAttr(long parent, string name)
        {
            byte[] nm = Encoding.Unicode.GetBytes(name);
            int contentLen = 0x42 + nm.Length;
            int attrLen = Align8(0x18 + contentLen);
            var b = new byte[attrLen];
            U32(b, 0x00, 0x30);            // type FILE_NAME
            U32(b, 0x04, (uint)attrLen);
            b[0x08] = 0;                   // resident
            U16(b, 0x14, 0x18);            // content offset
            U32(b, 0x10, (uint)contentLen);
            b[0x16] = 1;                   // indexed
            int c = 0x18;
            U64(b, c + 0x00, parent & 0x0000FFFFFFFFFFFFL); // parent ref (low 48 bits)
            b[c + 0x40] = (byte)name.Length;
            b[c + 0x41] = 1;               // Win32 namespace
            Array.Copy(nm, 0, b, c + 0x42, nm.Length);
            return new Attr { Bytes = b };
        }

        private static Attr ResidentData(byte[] content)
        {
            int attrLen = Align8(0x18 + content.Length);
            var b = new byte[attrLen];
            U32(b, 0x00, 0x80);            // type DATA
            U32(b, 0x04, (uint)attrLen);
            b[0x08] = 0;                   // resident
            U32(b, 0x10, (uint)content.Length);
            U16(b, 0x14, 0x18);            // content offset
            Array.Copy(content, 0, b, 0x18, content.Length);
            return new Attr { Bytes = b };
        }

        private static Attr NonResidentData(long realSize, byte[] runs)
        {
            int runOff = 0x40;
            int attrLen = Align8(runOff + runs.Length);
            var b = new byte[attrLen];
            U32(b, 0x00, 0x80);            // type DATA
            U32(b, 0x04, (uint)attrLen);
            b[0x08] = 1;                   // non-resident
            U16(b, 0x20, (ushort)runOff);  // run list offset
            U64(b, 0x28, AlignUp(realSize, ClusterSize)); // allocated
            U64(b, 0x30, realSize);        // real size
            U64(b, 0x38, realSize);        // initialized
            Array.Copy(runs, 0, b, runOff, runs.Length);
            return new Attr { Bytes = b };
        }

        // one or more (lengthClusters, startCluster) runs, absolute LCN encoded as delta from previous
        private static byte[] Runs(params (int len, int lcn)[] items)
        {
            var bytes = new List<byte>();
            long prev = 0;
            foreach (var (len, lcn) in items)
            {
                long delta = lcn - prev;
                byte[] lenB = VarBytes(len);
                byte[] offB = VarBytes(delta);
                bytes.Add((byte)((offB.Length << 4) | lenB.Length));
                bytes.AddRange(lenB);
                bytes.AddRange(offB);
                prev = lcn;
            }
            bytes.Add(0x00);
            return bytes.ToArray();
        }

        private static byte[] VarBytes(long v)
        {
            // minimal signed little-endian encoding
            var list = new List<byte>();
            bool neg = v < 0;
            while (true)
            {
                byte b = (byte)(v & 0xFF);
                list.Add(b);
                v >>= 8;
                bool done = (!neg && v == 0 && (b & 0x80) == 0) || (neg && v == -1 && (b & 0x80) != 0);
                if (done) break;
                if (list.Count >= 8) break;
            }
            return list.ToArray();
        }

        private static int Align8(int n) => (n + 7) & ~7;
        private static long AlignUp(long n, long a) => ((n + a - 1) / a) * a;
        private static void Ascii(byte[] b, int off, string s) { var x = Encoding.ASCII.GetBytes(s); Array.Copy(x, 0, b, off, x.Length); }
        private static void U16(byte[] b, int off, int v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
        private static void U32(byte[] b, int off, uint v) { for (int i = 0; i < 4; i++) b[off + i] = (byte)(v >> (8 * i)); }
        private static void U64(byte[] b, int off, long v) { for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (8 * i)); }
    }
}
