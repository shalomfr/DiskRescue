using System;
using System.Text;

namespace DiskRescue.Core
{
    /// <summary>
    /// Reads the boot region of a volume and determines: what filesystem it is/was,
    /// whether the main boot sector is damaged, and whether a valid backup exists to restore from.
    /// Pure read-only.
    ///
    /// All reads go through the PHYSICAL disk at (partitionOffset + relativeOffset). This is robust for
    /// both mounted and RAW volumes — unlike the volume handle (\\.\X:), whose addressable length on a
    /// mounted NTFS volume ends one sector before the partition end, hiding the backup boot sector.
    /// </summary>
    public static class BootSectorAnalyzer
    {
        public static BootAnalysis Analyze(VolumeInfo v)
        {
            var a = new BootAnalysis();
            RawDevice dev = null;
            try
            {
                dev = RawDevice.Open(v.PhysicalPath, write: false);
                a.Opened = true;
                long baseOff = v.PartitionOffset;
                long partBytes = v.PartitionSize > 0 ? v.PartitionSize : (long)v.SizeBytes;

                byte[] s0 = dev.ReadAt(baseOff, 512);
                a.MainFsName = Ascii(s0, 3, 8).TrimEnd();
                a.MainHasSignature = s0[510] == 0x55 && s0[511] == 0xAA;
                a.MainAllZero = AllZero(s0);

                string osFs = v.FileSystem;
                if (!string.IsNullOrWhiteSpace(osFs) && !osFs.Equals("RAW", StringComparison.OrdinalIgnoreCase))
                    a.DetectedFs = osFs;
                else
                    a.DetectedFs = GuessFromName(a.MainFsName);

                // ---- exFAT: dirty flag + backup boot region at sector 12 ----
                if (Eq(a.MainFsName, "EXFAT") || Eq(a.DetectedFs, "exFAT"))
                {
                    a.DetectedFs = "exFAT";
                    if (a.MainHasSignature) a.Dirty = (BitConverter.ToUInt16(s0, 0x6A) & 0x0002) != 0;

                    byte[] bak = dev.ReadAt(baseOff + 12L * 512, 512);
                    a.BackupFsName = Ascii(bak, 3, 8).TrimEnd();
                    if (Eq(a.BackupFsName, "EXFAT") && bak[510] == 0x55 && bak[511] == 0xAA)
                    { a.BackupValid = true; a.BackupOffset = baseOff + 12L * 512; a.Notes.Add("נמצא עותק גיבוי תקין של אזור האתחול (סקטור 12)."); }
                    return a;
                }

                // ---- NTFS: backup boot sector at the last sector of the partition ----
                if (Eq(a.MainFsName, "NTFS") || Eq(a.DetectedFs, "NTFS") || a.MainAllZero)
                {
                    if (partBytes >= 1024)
                    {
                        long bakOff = baseOff + partBytes - 512;
                        byte[] bak = dev.ReadAt(bakOff, 512);
                        a.BackupFsName = Ascii(bak, 3, 8).TrimEnd();
                        bool sig = bak[510] == 0x55 && bak[511] == 0xAA;
                        if (Eq(a.BackupFsName, "NTFS") && sig)
                        {
                            a.BackupValid = true; a.BackupOffset = bakOff; a.DetectedFs = "NTFS";
                            long totalSectors = BitConverter.ToInt64(bak, 0x28);
                            long partSectors = partBytes / 512;
                            a.BackupMatchesPartition = totalSectors == partSectors - 1;
                            a.Notes.Add($"נמצא סקטור אתחול גיבוי תקין של NTFS.");
                            a.Notes.Add(a.BackupMatchesPartition
                                ? "הגיבוי תואם בדיוק לגודל המחיצה."
                                : "אזהרה: גודל בגיבוי לא תואם למחיצה — ייתכן שריד של מחיצה ישנה.");

                            try
                            {
                                int bps = BitConverter.ToUInt16(bak, 0x0B);
                                int spc = bak[0x0D];
                                long mftCluster = BitConverter.ToInt64(bak, 0x30);
                                long mftOff = baseOff + mftCluster * bps * spc;
                                byte[] mft = dev.ReadAt(mftOff, 512);
                                a.MftLooksOk = Ascii(mft, 0, 4) == "FILE";
                                a.Notes.Add(a.MftLooksOk
                                    ? "טבלת הקבצים ($MFT) שלמה."
                                    : "לא נמצאה חתימת 'FILE' ב-$MFT — ייתכן נזק עמוק יותר.");
                            }
                            catch { a.Notes.Add("לא ניתן היה לאמת את $MFT."); }
                        }
                    }
                    if (string.IsNullOrEmpty(a.DetectedFs) && a.MainAllZero) a.DetectedFs = "NTFS?";
                    return a;
                }

                // ---- FAT32: backup boot sector at BPB_BkBootSec (default 6) ----
                if (Eq(a.MainFsName, "FAT32") || a.DetectedFs.StartsWith("FAT"))
                {
                    a.DetectedFs = "FAT32";
                    int bkSec = BitConverter.ToUInt16(s0, 0x32);
                    if (bkSec == 0 || bkSec > 32) bkSec = 6;
                    byte[] bak = dev.ReadAt(baseOff + (long)bkSec * 512, 512);
                    if (bak[510] == 0x55 && bak[511] == 0xAA)
                    { a.BackupValid = true; a.BackupOffset = baseOff + (long)bkSec * 512; a.Notes.Add($"נמצא סקטור אתחול גיבוי של FAT32 (סקטור {bkSec})."); }
                    return a;
                }

                a.Notes.Add("לא זוהתה מערכת קבצים מוכרת בסקטור האתחול.");
                return a;
            }
            catch (Exception ex)
            {
                a.Opened = false;
                a.OpenError = ex.Message;
                return a;
            }
            finally { dev?.Dispose(); }
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string GuessFromName(string n)
        {
            if (Eq(n, "NTFS")) return "NTFS";
            if (Eq(n, "EXFAT")) return "exFAT";
            if (n != null && n.StartsWith("FAT")) return n;
            if (Eq(n, "MSDOS5.0")) return "FAT";
            return "";
        }
        private static string Ascii(byte[] b, int off, int len) => Encoding.ASCII.GetString(b, off, len);
        private static bool AllZero(byte[] b) { foreach (var x in b) if (x != 0) return false; return true; }
    }
}
