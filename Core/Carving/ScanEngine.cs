using System;
using System.Collections.Generic;
using System.IO;

namespace DiskRescue.Core.Carving
{
    /// <summary>
    /// Signature-based file carving (PhotoRec-style). Reads the device sequentially in aligned chunks,
    /// detects known headers, and writes recovered files grouped by type into the output folder.
    ///
    /// Supports: pause/stop (via shouldStop), resume (via ScanProject.NextOffset + checkpoint),
    /// progress reporting, and bad-sector resilience (a failed read is skipped, not fatal).
    /// </summary>
    public sealed class ScanEngine
    {
        private const int ChunkSize = 4 * 1024 * 1024;          // 4 MB scan window
        private const long CheckpointEvery = 256L * 1024 * 1024; // save project every 256 MB
        private const int Sector = 512;

        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>();

        /// <param name="shouldStop">Polled frequently; return true to pause. Progress is checkpointed before returning.</param>
        /// <param name="checkpoint">Called to persist the project (on checkpoint interval, on stop, and at completion).</param>
        public void Scan(ScanProject p, IProgress<ScanProgress> progress, Func<bool> shouldStop, Action<ScanProject> checkpoint)
        {
            Directory.CreateDirectory(p.OutputFolder);
            var sigs = FileSignatures.All;
            int overlap = Math.Max(FileSignatures.MaxHeaderSpan, 16);

            long pos = Math.Max(p.NextOffset, p.PartitionOffset);
            if (pos % Sector != 0) pos -= pos % Sector;
            long end = p.DeviceEnd;
            long lastCheckpoint = pos;
            var prog = new ScanProgress { TotalBytes = p.PartitionSize, FilesFound = p.FilesFound };

            using var dev = RawDevice.Open(p.DevicePath, write: false);

            while (pos < end)
            {
                if (shouldStop())
                {
                    p.NextOffset = pos; checkpoint(p);
                    return;
                }

                int want = (int)Math.Min(ChunkSize, end - pos);
                want -= want % Sector;
                if (want < Sector) break;

                byte[] buf;
                try { buf = dev.ReadAt(pos, want); }
                catch (IOException)
                {
                    // bad sector / unreadable region — skip this chunk and keep going (ddrescue-lite)
                    prog.BadSectorSkips++;
                    pos += want;
                    continue;
                }

                int searchLimit = buf.Length - overlap;
                if (searchLimit < 0) searchLimit = 0;
                for (int i = 0; i <= searchLimit; i++)
                {
                    foreach (var sig in sigs)
                    {
                        if (!Matches(buf, i, sig.Header)) continue;
                        long fileStart = pos + i - sig.PreOffset;
                        if (fileStart < p.PartitionOffset) continue;
                        try
                        {
                            var carved = Carve(dev, p, sig, fileStart, end);
                            if (carved != null)
                            {
                                p.Found.Add(carved);
                                p.FilesFound++;
                                prog.FilesFound = p.FilesFound;
                            }
                        }
                        catch (IOException) { /* unreadable while carving — skip */ }
                        break; // one signature per position
                    }
                }

                // advance with overlap so a header split across the boundary is still caught
                long advance = (pos + buf.Length >= end) ? buf.Length : buf.Length - overlap;
                pos += advance;

                prog.BytesScanned = pos - p.PartitionOffset;
                prog.CurrentOffset = pos;
                progress?.Report(prog);

                if (pos - lastCheckpoint >= CheckpointEvery)
                {
                    p.NextOffset = pos; checkpoint(p); lastCheckpoint = pos;
                }
            }

            p.NextOffset = end;
            checkpoint(p);
            prog.BytesScanned = p.PartitionSize;
            progress?.Report(prog);
        }

        private CarvedFile Carve(RawDevice dev, ScanProject p, FileSignature sig, long fileStart, long deviceEnd)
        {
            // Reads must be sector-aligned, so read an aligned window and slice the file out of it.
            long alignedStart = fileStart - (fileStart % Sector);
            int pre = (int)(fileStart - alignedStart);
            long maxWindow = Math.Min(sig.MaxSize + pre, deviceEnd - alignedStart);
            int windowLen = (int)(((maxWindow + Sector - 1) / Sector) * Sector);
            if (alignedStart + windowLen > deviceEnd) windowLen = (int)(deviceEnd - alignedStart);
            windowLen -= windowLen % Sector;
            if (windowLen <= pre) return null;

            byte[] w = dev.ReadAt(alignedStart, windowLen);

            int size;
            if (sig.Footer != null)
            {
                int fp = IndexOf(w, sig.Footer, pre);
                size = fp >= 0 ? (fp + sig.Footer.Length - pre) : (int)Math.Min(sig.MaxSize, w.Length - pre);
            }
            else size = (int)Math.Min(sig.MaxSize, w.Length - pre);

            if (size <= 0) return null;

            int n = NextCounter(sig.Ext);
            string dir = Path.Combine(p.OutputFolder, sig.Ext);
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, $"{sig.Ext}_{n:D6}.{sig.Ext}");
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                fs.Write(w, pre, size);

            return new CarvedFile { Ext = sig.Ext, Offset = fileStart, Size = size, Path = outPath };
        }

        private int NextCounter(string ext)
        {
            _counters.TryGetValue(ext, out int c);
            c++; _counters[ext] = c; return c;
        }

        private static bool Matches(byte[] buf, int at, byte[] pat)
        {
            if (at + pat.Length > buf.Length) return false;
            for (int k = 0; k < pat.Length; k++) if (buf[at + k] != pat[k]) return false;
            return true;
        }

        private static int IndexOf(byte[] buf, byte[] pat, int start)
        {
            int last = buf.Length - pat.Length;
            for (int i = start; i <= last; i++)
            {
                bool ok = true;
                for (int k = 0; k < pat.Length; k++) if (buf[i + k] != pat[k]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
