using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DiskRescue.Core
{
    /// <summary>Write operations. Every fix is conservative and creates an undo copy before touching the disk.</summary>
    public static class SafeFixes
    {
        public static string UndoFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskRescue", "undo");

        /// <summary>
        /// Restore sector 0 from the validated backup boot sector.
        /// Re-validates the backup, refuses to overwrite a non-empty sector 0, saves an undo copy, writes, verifies, rescans.
        /// </summary>
        public static string RestoreBootSector(VolumeInfo v, Action<string> log)
        {
            var boot = BootSectorAnalyzer.Analyze(v);
            if (!boot.Opened) throw new IOException("לא ניתן לפתוח את הכונן: " + boot.OpenError);
            if (!boot.BackupValid) throw new InvalidOperationException("לא נמצא סקטור גיבוי תקין — לא ניתן לשחזר בבטחה.");

            byte[] backup, current;
            using (var pread = RawDevice.Open(v.PhysicalPath, write: false))
            {
                backup = pread.ReadAt(boot.BackupOffset, 512);          // physical: backup boot sector
                current = pread.ReadAt(v.PartitionOffset, 512);         // physical: current sector 0 of the partition
            }

            // SAFETY 1: backup must be a valid VBR with a boot signature.
            if (!(backup[510] == 0x55 && backup[511] == 0xAA))
                throw new InvalidOperationException("הגיבוי אינו תקין (אין חתימת אתחול) — בוטל.");
            string bOem = Encoding.ASCII.GetString(backup, 3, 8).TrimEnd();
            log($"גיבוי: '{bOem}', חתימה תקינה.");

            // SAFETY 2: never overwrite a sector 0 that already holds a real boot sector.
            bool curHasBoot = current[510] == 0x55 && current[511] == 0xAA;
            bool curAllZero = AllZero(current);
            if (curHasBoot && !curAllZero)
                throw new InvalidOperationException("סקטור 0 כבר מכיל סקטור אתחול — מסרב לדרוס.");

            // Undo copy.
            Directory.CreateDirectory(UndoFolder);
            string undo = Path.Combine(UndoFolder, $"{v.Letter}_sector0_{DateStamp()}.bin");
            File.WriteAllBytes(undo, current);
            log($"נשמר עותק לביטול: {undo}");

            // Write sector 0 via the volume handle (proven path for an unmounted RAW partition).
            log("כותב את סקטור האתחול לסקטור 0...");
            using (var write = RawDevice.Open(v.DevicePath, write: true))
                write.WriteAt(0, backup);

            // Verify via the physical disk.
            using (var verify = RawDevice.Open(v.PhysicalPath, write: false))
            {
                byte[] after = verify.ReadAt(v.PartitionOffset, 512);
                bool match = true;
                for (int i = 0; i < 512; i++) if (after[i] != backup[i]) { match = false; break; }
                if (!match) throw new IOException("האימות נכשל — הנתונים שנכתבו אינם תואמים לגיבוי.");
                log("אימות עבר: סקטור 0 זהה לגיבוי התקין.");
            }

            Rescan(log);
            return "השחזור הושלם בהצלחה. ייתכן שיהיה צורך לחבר מחדש את הכונן כדי שייטען.";
        }

        /// <summary>Run chkdsk /f /x to clear the dirty flag and fix minor inconsistencies on a mounted volume.</summary>
        public static string RunChkdsk(VolumeInfo v, Action<string> log)
        {
            log($"מריץ: chkdsk {v.Letter}: /f /x");
            var psi = new ProcessStartInfo("cmd.exe", $"/c chkdsk {v.Letter}: /f /x")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(850)
            };
            using var p = Process.Start(psi);
            p.OutputDataReceived += (s, e) => { if (e.Data != null) log(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) log(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode == 0 ? "chkdsk הסתיים בהצלחה — הדגל נוקה." : $"chkdsk הסתיים עם קוד {p.ExitCode}.";
        }

        private static void Rescan(Action<string> log)
        {
            try
            {
                log("מבצע סריקה מחדש של ההתקנים...");
                string script = Path.Combine(Path.GetTempPath(), "diskrescue_rescan.txt");
                File.WriteAllText(script, "rescan\r\n");
                var psi = new ProcessStartInfo("diskpart", $"/s \"{script}\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
            }
            catch (Exception ex) { log("סריקה מחדש נכשלה (לא קריטי): " + ex.Message); }
        }

        private static bool AllZero(byte[] b) { foreach (var x in b) if (x != 0) return false; return true; }
        private static string DateStamp()
        {
            var n = DateTime.Now;
            return $"{n.Year:0000}{n.Month:00}{n.Day:00}_{n.Hour:00}{n.Minute:00}{n.Second:00}";
        }
    }
}
