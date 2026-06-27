using System;
using System.Collections.Generic;

namespace DiskRescue.Core
{
    public enum Severity { Ok, Info, Warning, Critical }

    public enum FixKind
    {
        None,
        RestoreBootSector,   // copy valid backup boot sector -> sector 0 (the "RAW drive" case)
        RunChkdsk,           // clear dirty flag / fix minor inconsistency on a mounted volume
        DeepScanNeeded,      // structure too damaged for a quick fix -> real recovery
        MechanicalSuspect    // SMART / read errors suggest hardware failure -> stop, go to lab
    }

    /// <summary>One recommended action presented to the user after triage.</summary>
    public sealed class FixAction
    {
        public FixKind Kind;
        public string Label;
        public string Description;
        public bool WritesToDisk;
        public FixAction(FixKind kind, string label, string description, bool writes)
        { Kind = kind; Label = label; Description = description; WritesToDisk = writes; }
    }

    /// <summary>Everything we learned about one volume from inventory (read-only WMI + OS).</summary>
    public sealed class VolumeInfo
    {
        public char Letter;
        public string Label = "";
        public string FileSystem = "";       // "NTFS" / "exFAT" / "FAT32" / "" (RAW/unknown)
        public ulong SizeBytes;
        public ulong FreeBytes;
        public int HealthStatus;             // 0 Healthy, 1 Warning, 2 Unhealthy
        public uint DiskNumber;
        public string DiskModel = "";
        public ushort BusType;               // 7 = USB (external)
        public long PartitionOffset;
        public long PartitionSize;
        public string VolumePath = "";       // \\?\Volume{guid}\

        public string DevicePath => $"\\\\.\\{Letter}:";
        public string PhysicalPath => $"\\\\.\\PhysicalDrive{DiskNumber}";
        public bool IsExternal => BusType == 7;
        public bool IsRaw => string.IsNullOrWhiteSpace(FileSystem) || FileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase);

        // Display helpers for the grid
        public string LetterDisplay => $"{Letter}:";
        public string FsDisplay => IsRaw ? "RAW / לא מזוהה" : FileSystem;
        public string SizeDisplay => Format.Bytes(SizeBytes > 0 ? SizeBytes : (ulong)PartitionSize);
        public string HealthDisplay => HealthStatus switch { 0 => "תקין", 1 => "אזהרה", 2 => "תקול", _ => "לא ידוע" };
        public string BusDisplay => IsExternal ? "חיצוני (USB)" : "פנימי";
    }

    /// <summary>Raw findings from reading the boot region of a volume.</summary>
    public sealed class BootAnalysis
    {
        public bool Opened;
        public string OpenError = "";
        public string MainFsName = "";       // OEM/FS id at offset 3 of sector 0
        public bool MainHasSignature;        // 0x55AA at 510-511
        public bool MainAllZero;
        public string DetectedFs = "";       // best guess of the real filesystem
        public bool BackupValid;             // a usable backup boot sector exists
        public string BackupFsName = "";
        public long BackupOffset;            // where the backup lives
        public bool? Dirty;                  // exFAT VolumeFlags bit1 / NTFS dirty bit (null = unknown)
        public bool MftLooksOk;              // NTFS: $MFT starts with 'FILE'
        public bool BackupMatchesPartition;  // NTFS total-sectors == partition-sectors-1
        public List<string> Notes = new List<string>();
    }

    /// <summary>The user-facing verdict for one volume.</summary>
    public sealed class TriageResult
    {
        public char Letter;
        public Severity Severity = Severity.Ok;
        public string Title = "";
        public List<string> Findings = new List<string>();
        public List<FixAction> Actions = new List<FixAction>();
        public BootAnalysis Boot;
    }

    public static class Format
    {
        public static string Bytes(ulong b)
        {
            if (b == 0) return "0";
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }
        public static string Bytes(long b) => Bytes(b < 0 ? 0UL : (ulong)b);
    }
}
