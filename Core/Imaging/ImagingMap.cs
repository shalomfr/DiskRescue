using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DiskRescue.Core.Imaging
{
    public enum BlockState { Untried = 0, Done = 1, Bad = 2, BadFinal = 3 }

    public sealed class Region
    {
        public long Offset { get; set; }
        public long Length { get; set; }
        public BlockState State { get; set; }
        public long End => Offset + Length;
    }

    /// <summary>
    /// ddrescue-style coverage map: the device is partitioned into contiguous regions, each in one state.
    /// Pass 1 copies Untried regions in big blocks (bad ones become Bad); pass 2 scrapes Bad regions at sector
    /// granularity (unreadable parts become BadFinal). Serializable so imaging can pause and resume.
    /// </summary>
    public sealed class ImagingMap
    {
        public long DeviceSize { get; set; }
        public List<Region> Regions { get; set; } = new List<Region>();

        public static ImagingMap Create(long size) => new ImagingMap
        {
            DeviceSize = size,
            Regions = new List<Region> { new Region { Offset = 0, Length = size, State = BlockState.Untried } }
        };

        public IEnumerable<Region> ByState(BlockState s)
        {
            foreach (var r in Regions) if (r.State == s) yield return r;
        }

        public long Bytes(BlockState s)
        {
            long t = 0;
            foreach (var r in Regions) if (r.State == s) t += r.Length;
            return t;
        }

        /// <summary>Set the state of [offset, offset+length), splitting and coalescing as needed.</summary>
        public void Set(long offset, long length, BlockState st)
        {
            if (length <= 0) return;
            long end = offset + length;
            var result = new List<Region>();
            foreach (var r in Regions)
            {
                if (r.End <= offset || r.Offset >= end) { result.Add(r); continue; }   // no overlap
                if (r.Offset < offset) result.Add(new Region { Offset = r.Offset, Length = offset - r.Offset, State = r.State });
                if (r.End > end) result.Add(new Region { Offset = end, Length = r.End - end, State = r.State });
            }
            result.Add(new Region { Offset = offset, Length = length, State = st });
            result.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            Regions = Coalesce(result);
        }

        private static List<Region> Coalesce(List<Region> list)
        {
            var outp = new List<Region>();
            foreach (var r in list)
            {
                if (r.Length <= 0) continue;
                if (outp.Count > 0)
                {
                    var last = outp[outp.Count - 1];
                    if (last.State == r.State && last.End == r.Offset) { last.Length += r.Length; continue; }
                }
                outp.Add(new Region { Offset = r.Offset, Length = r.Length, State = r.State });
            }
            return outp;
        }

        // ---- persistence ----
        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { WriteIndented = true };
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, JsonSerializer.Serialize(this, Opt));
        }
        public static ImagingMap Load(string path) => JsonSerializer.Deserialize<ImagingMap>(File.ReadAllText(path));
    }
}
