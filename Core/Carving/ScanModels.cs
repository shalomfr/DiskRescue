using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DiskRescue.Core.Carving
{
    public sealed class CarvedFile
    {
        public string Ext { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// A resumable scan. Holds where to read next (NextOffset) and everything found so far,
    /// so a scan can be paused/stopped and continued later from a saved file.
    /// </summary>
    public sealed class ScanProject
    {
        public char VolumeLetter { get; set; }
        public string DevicePath { get; set; }      // physical disk or a file path (for tests)
        public long PartitionOffset { get; set; }
        public long PartitionSize { get; set; }
        public long NextOffset { get; set; }         // absolute offset to resume from
        public string OutputFolder { get; set; }
        public int FilesFound { get; set; }
        public List<CarvedFile> Found { get; set; } = new List<CarvedFile>();
        public string CreatedUtc { get; set; }

        public long DeviceEnd => PartitionOffset + PartitionSize;
        public bool IsComplete => NextOffset >= DeviceEnd;
    }

    public static class ProjectStore
    {
        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions { WriteIndented = true };

        public static void Save(ScanProject p, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, JsonSerializer.Serialize(p, Opt));
        }

        public static ScanProject Load(string path)
            => JsonSerializer.Deserialize<ScanProject>(File.ReadAllText(path));
    }

    public sealed class ScanProgress
    {
        public long BytesScanned;
        public long TotalBytes;
        public int FilesFound;
        public long CurrentOffset;
        public double Percent => TotalBytes > 0 ? (double)BytesScanned / TotalBytes * 100.0 : 0;
        public long BadSectorSkips;
    }
}
