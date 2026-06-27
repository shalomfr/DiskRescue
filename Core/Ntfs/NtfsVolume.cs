using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DiskRescue.Core.Ntfs
{
    /// <summary>
    /// Filesystem-based NTFS recovery: reads the $MFT, lists all files/folders (including deleted ones
    /// whose records still exist), reconstructs original paths, and recovers file data (resident or via data runs).
    /// </summary>
    public sealed class NtfsVolume : IDisposable
    {
        private RawDevice _dev;
        private long _base;
        public NtfsBoot Boot { get; private set; }
        private List<Run> _mftRuns;
        private readonly Dictionary<long, (string name, long parent, bool dir)> _meta
            = new Dictionary<long, (string, long, bool)>();

        public static NtfsVolume Open(string physicalPath, long partitionOffset)
        {
            var v = new NtfsVolume { _dev = RawDevice.Open(physicalPath, false), _base = partitionOffset };
            byte[] s0 = v._dev.ReadAt(partitionOffset, 512);
            v.Boot = NtfsBoot.Parse(s0);

            long mftOff = partitionOffset + v.Boot.MftCluster * v.Boot.ClusterSize;
            byte[] rec0 = v._dev.ReadAt(mftOff, v.Boot.MftRecordSize);
            var e0 = MftRecord.Parse(rec0, v.Boot.BytesPerSector, 0);
            if (e0 == null || e0.Resident || e0.Runs == null || e0.Runs.Count == 0)
                throw new InvalidOperationException("לא ניתן לקרוא את רצפי ה-$MFT (ייתכן MFT מקוטע עם ATTRIBUTE_LIST).");
            v._mftRuns = e0.Runs;
            return v;
        }

        public long EstimatedRecordCount
        {
            get
            {
                long clusters = 0;
                foreach (var r in _mftRuns) clusters += r.Length;
                return clusters * Boot.ClusterSize / Boot.MftRecordSize;
            }
        }

        public byte[] ReadRecord(long n)
        {
            long virt = n * Boot.MftRecordSize;
            long vcn = virt / Boot.ClusterSize;
            long inCluster = virt % Boot.ClusterSize;
            long lcn = DataRuns.MapVcnToLcn(_mftRuns, vcn, out bool sparse);
            if (lcn < 0) return null;
            long phys = _base + lcn * Boot.ClusterSize + inCluster;
            return _dev.ReadAt(phys, Boot.MftRecordSize);
        }

        /// <summary>Read up to maxRecords MFT entries and build the name/parent map for path resolution.</summary>
        public List<NtfsFileEntry> ReadAll(long maxRecords, Action<long, long> progress = null, Func<bool> shouldStop = null)
        {
            var list = new List<NtfsFileEntry>();
            long total = Math.Min(maxRecords, EstimatedRecordCount);
            for (long n = 0; n < total; n++)
            {
                if (shouldStop != null && (n & 0x3FF) == 0 && shouldStop()) break;
                byte[] rec;
                try { rec = ReadRecord(n); } catch (IOException) { continue; }
                if (rec == null) continue;
                NtfsFileEntry e;
                try { e = MftRecord.Parse(rec, Boot.BytesPerSector, n); } catch { continue; }
                if (e == null) continue;
                if (!string.IsNullOrEmpty(e.Name))
                    _meta[n] = (e.Name, e.ParentRecord, e.IsDirectory);
                list.Add(e);
                if (progress != null && (n & 0x3FF) == 0) progress(n, total);
            }
            return list;
        }

        /// <summary>Sanitize a reconstructed NTFS path into a safe relative path under an output folder.</summary>
        public static string SafeRelativePath(string ntfsPath)
        {
            var parts = ntfsPath.Split('\\');
            var clean = new List<string>();
            foreach (var raw in parts)
            {
                string p = raw;
                foreach (var c in Path.GetInvalidFileNameChars()) p = p.Replace(c, '_');
                p = p.Trim();
                if (p.Length == 0) continue;
                clean.Add(p);
            }
            return clean.Count == 0 ? "recovered.bin" : string.Join("\\", clean);
        }

        public string ResolvePath(NtfsFileEntry e)
        {
            if (string.IsNullOrEmpty(e.Name)) return $"[record {e.RecordNumber}]";
            var parts = new List<string> { e.Name };
            long p = e.ParentRecord;
            int guard = 0;
            while (p != 5 && p != 0 && guard++ < 256)
            {
                if (_meta.TryGetValue(p, out var m)) { parts.Add(m.name); p = m.parent; }
                else { parts.Add($"[?{p}]"); break; }
            }
            parts.Reverse();
            return string.Join("\\", parts);
        }

        public void Recover(NtfsFileEntry e, string outPath)
        {
            if (!e.HasData) throw new InvalidOperationException("לרשומה אין זרם נתונים ראשי.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);

            if (e.Resident)
            {
                int n = (int)Math.Min(e.DataSize, e.ResidentData.Length);
                fs.Write(e.ResidentData, 0, n);
                return;
            }
            if (e.Runs == null) return;

            long remaining = e.DataSize;
            foreach (var r in e.Runs)
            {
                if (remaining <= 0) break;
                long runBytes = r.Length * Boot.ClusterSize;
                if (r.Sparse) { WriteZeros(fs, Math.Min(runBytes, remaining)); remaining -= Math.Min(runBytes, remaining); continue; }

                long runStart = _base + r.Lcn * Boot.ClusterSize;
                long done = 0;
                while (done < runBytes && remaining > 0)
                {
                    int chunk = (int)Math.Min(1 << 20, runBytes - done);
                    chunk -= chunk % Boot.BytesPerSector;
                    if (chunk <= 0) break;
                    byte[] data;
                    try { data = _dev.ReadAt(runStart + done, chunk); }
                    catch (IOException) { break; } // bad sector inside file — stop this file gracefully
                    int w = (int)Math.Min(chunk, remaining);
                    fs.Write(data, 0, w);
                    done += chunk;
                    remaining -= w;
                }
            }
        }

        private static void WriteZeros(FileStream fs, long count)
        {
            var z = new byte[Math.Min(count, 1 << 20)];
            while (count > 0) { int w = (int)Math.Min(z.Length, count); fs.Write(z, 0, w); count -= w; }
        }

        public void Dispose() => _dev?.Dispose();
    }
}
