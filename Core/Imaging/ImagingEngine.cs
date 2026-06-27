using System;
using System.Diagnostics;
using System.IO;

namespace DiskRescue.Core.Imaging
{
    public sealed class ImagingProgress
    {
        public int Pass;                 // 1 = copy, 2 = scrape
        public long DoneBytes;
        public long BadBytes;            // currently Bad or BadFinal
        public long TotalBytes;
        public long CurrentOffset;
        public double BytesPerSec;
        public SmartInfo Smart;
        public double Percent => TotalBytes > 0 ? (double)(DoneBytes) / TotalBytes * 100.0 : 0;
    }

    /// <summary>
    /// Copies a device to an image file the safe way: a fast first pass grabs everything readable and skips
    /// bad regions; a second pass retries the bad regions at sector granularity. Resumable via the map.
    /// Optionally polls SMART and can auto-stop if the drive reports imminent failure.
    /// </summary>
    public sealed class ImagingEngine
    {
        private const int Sector = 512;
        private const long BigBlock = 1L * 1024 * 1024;   // pass 1
        private const long SmallBlock = 64L * 1024;       // pass 2 scrape
        private const long CheckpointEvery = 256L * 1024 * 1024;

        public Func<uint, SmartInfo> SmartProvider;       // optional
        public uint DiskNumber;
        public bool AutoStopOnPredictFailure = true;

        public void Run(IBlockReader src, string imagePath, ImagingMap map,
                        IProgress<ImagingProgress> progress, Func<bool> shouldStop, Action<ImagingMap> checkpoint)
        {
            using var img = new FileStream(imagePath, FileMode.OpenOrCreate, FileAccess.Write);
            if (img.Length < src.Length) img.SetLength(src.Length);   // sparse on NTFS

            var sw = Stopwatch.StartNew();
            long lastCheckpoint = 0, lastBytes = 0;
            var prog = new ImagingProgress { TotalBytes = src.Length };
            SmartInfo smart = PollSmart();
            prog.Smart = smart;

            bool stop = false;

            void DoBlock(long off, int len, bool scrape)
            {
                try
                {
                    byte[] data = src.Read(off, len);
                    img.Seek(off, SeekOrigin.Begin);
                    img.Write(data, 0, len);
                    map.Set(off, len, BlockState.Done);
                }
                catch (IOException)
                {
                    map.Set(off, len, scrape ? BlockState.BadFinal : BlockState.Bad);
                }
            }

            // ---- PASS 1: copy ----
            prog.Pass = 1;
            foreach (var region in Snapshot(map, BlockState.Untried))
            {
                for (long p = region.Offset; p < region.End; p += BigBlock)
                {
                    if (shouldStop()) { stop = true; break; }
                    int len = (int)Math.Min(BigBlock, region.End - p);
                    DoBlock(p, len, scrape: false);

                    prog.DoneBytes = map.Bytes(BlockState.Done);
                    prog.BadBytes = map.Bytes(BlockState.Bad) + map.Bytes(BlockState.BadFinal);
                    prog.CurrentOffset = p + len;
                    Report(prog, sw, ref lastBytes);

                    if (prog.DoneBytes + prog.BadBytes - lastCheckpoint >= CheckpointEvery)
                    { checkpoint(map); lastCheckpoint = prog.DoneBytes + prog.BadBytes; smart = PollSmart(); prog.Smart = smart; }

                    if (AutoStopOnPredictFailure && smart != null && smart.PredictFailure) { stop = true; break; }
                }
                if (stop) break;
            }
            checkpoint(map);
            if (stop) { Report(prog, sw, ref lastBytes); return; }

            // ---- PASS 2: scrape bad regions at finer granularity ----
            prog.Pass = 2;
            foreach (var region in Snapshot(map, BlockState.Bad))
            {
                for (long p = region.Offset; p < region.End; p += SmallBlock)
                {
                    if (shouldStop()) { stop = true; break; }
                    int len = (int)Math.Min(SmallBlock, region.End - p);
                    DoBlock(p, len, scrape: true);

                    prog.DoneBytes = map.Bytes(BlockState.Done);
                    prog.BadBytes = map.Bytes(BlockState.Bad) + map.Bytes(BlockState.BadFinal);
                    prog.CurrentOffset = p + len;
                    Report(prog, sw, ref lastBytes);
                }
                if (stop) break;
            }
            checkpoint(map);
            Report(prog, sw, ref lastBytes);
        }

        private System.Collections.Generic.List<Region> Snapshot(ImagingMap map, BlockState s)
        {
            var list = new System.Collections.Generic.List<Region>();
            foreach (var r in map.ByState(s)) list.Add(new Region { Offset = r.Offset, Length = r.Length, State = r.State });
            return list;
        }

        private SmartInfo PollSmart()
        {
            try { return SmartProvider?.Invoke(DiskNumber); } catch { return null; }
        }

        private void Report(ImagingProgress p, Stopwatch sw, ref long lastBytes)
        {
            double sec = sw.Elapsed.TotalSeconds;
            if (sec > 0.5) { p.BytesPerSec = (p.DoneBytes - lastBytes) / sec; }
            // (rate is approximate; reset window each report)
            lastBytes = p.DoneBytes; sw.Restart();
        }
    }
}
