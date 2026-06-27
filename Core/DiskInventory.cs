using System;
using System.Collections.Generic;
using System.Management;

namespace DiskRescue.Core
{
    /// <summary>Enumerates volumes by joining MSFT_Partition + MSFT_Volume + MSFT_Disk (read-only).</summary>
    public static class DiskInventory
    {
        private const string Scope = "\\\\.\\root\\Microsoft\\Windows\\Storage";

        public static List<VolumeInfo> GetVolumes()
        {
            var result = new List<VolumeInfo>();
            var disks = QueryDisks();      // diskNumber -> (model, busType, health)
            var volumes = QueryVolumes();  // letter -> (label, fs, size, free, health, path)

            foreach (var p in Query("SELECT DiskNumber,Offset,Size,DriveLetter,PartitionNumber FROM MSFT_Partition"))
            {
                char letter = ToLetter(p["DriveLetter"]);
                if (letter == '\0') continue; // skip hidden/system partitions without a letter

                var vi = new VolumeInfo
                {
                    Letter = letter,
                    DiskNumber = ToUInt(p["DiskNumber"]),
                    PartitionOffset = (long)ToULong(p["Offset"]),
                    PartitionSize = (long)ToULong(p["Size"]),
                };

                if (disks.TryGetValue(vi.DiskNumber, out var d))
                {
                    vi.DiskModel = d.model; vi.BusType = d.bus; vi.HealthStatus = d.health;
                }
                if (volumes.TryGetValue(letter, out var v))
                {
                    vi.Label = v.label; vi.FileSystem = v.fs; vi.SizeBytes = v.size;
                    vi.FreeBytes = v.free; vi.HealthStatus = v.health; vi.VolumePath = v.path;
                }
                result.Add(vi);
            }
            result.Sort((a, b) => a.Letter.CompareTo(b.Letter));
            return result;
        }

        private static Dictionary<uint, (string model, ushort bus, int health)> QueryDisks()
        {
            var map = new Dictionary<uint, (string, ushort, int)>();
            foreach (var d in Query("SELECT Number,FriendlyName,Model,BusType,HealthStatus FROM MSFT_Disk"))
            {
                uint num = ToUInt(d["Number"]);
                string model = (d["FriendlyName"] ?? d["Model"])?.ToString() ?? "";
                ushort bus = (ushort)ToUInt(d["BusType"]);
                int health = (int)ToUInt(d["HealthStatus"]);
                map[num] = (model, bus, health);
            }
            return map;
        }

        private static Dictionary<char, (string label, string fs, ulong size, ulong free, int health, string path)> QueryVolumes()
        {
            var map = new Dictionary<char, (string, string, ulong, ulong, int, string)>();
            foreach (var v in Query("SELECT DriveLetter,FileSystemLabel,FileSystem,Size,SizeRemaining,HealthStatus,Path FROM MSFT_Volume"))
            {
                char letter = ToLetter(v["DriveLetter"]);
                if (letter == '\0') continue;
                map[letter] = (
                    v["FileSystemLabel"]?.ToString() ?? "",
                    v["FileSystem"]?.ToString() ?? "",
                    ToULong(v["Size"]),
                    ToULong(v["SizeRemaining"]),
                    (int)ToUInt(v["HealthStatus"]),
                    v["Path"]?.ToString() ?? "");
            }
            return map;
        }

        private static IEnumerable<ManagementBaseObject> Query(string wql)
        {
            using var searcher = new ManagementObjectSearcher(Scope, wql);
            foreach (ManagementBaseObject o in searcher.Get()) yield return o;
        }

        private static char ToLetter(object o)
        {
            if (o == null) return '\0';
            try
            {
                if (o is char c) return c == 0 ? '\0' : c;
                ushort u = Convert.ToUInt16(o);
                return u == 0 ? '\0' : (char)u;
            }
            catch { return '\0'; }
        }

        private static uint ToUInt(object o) { try { return o == null ? 0u : Convert.ToUInt32(o); } catch { return 0u; } }
        private static ulong ToULong(object o) { try { return o == null ? 0ul : Convert.ToUInt64(o); } catch { return 0ul; } }
    }
}
