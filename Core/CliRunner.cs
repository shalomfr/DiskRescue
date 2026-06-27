using System;
using System.IO;
using System.Linq;
using System.Text;
using DiskRescue.Core.Carving;
using DiskRescue.Core.Ntfs;

namespace DiskRescue.Core
{
    /// <summary>Headless verification: runs full inventory + triage and writes a readable report to a file.</summary>
    public static class CliRunner
    {
        /// <summary>
        /// Full NTFS pipeline test on a synthetic image (no admin, no real disk):
        /// build -> open -> enumerate -> resolve paths -> recover (resident + non-resident) -> verify bytes.
        /// </summary>
        public static void NtfsSelfTest(string outDir, string reportPath)
        {
            var sb = new StringBuilder();
            try
            {
                string img = Path.Combine(Path.GetTempPath(), "diskrescue_ntfs.img");
                File.WriteAllBytes(img, SyntheticNtfs.Build());
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                Directory.CreateDirectory(outDir);

                using var vol = NtfsVolume.Open(img, 0);
                sb.AppendLine("=== NTFS synthetic self-test ===");
                sb.AppendLine($"cluster={vol.Boot.ClusterSize} mftRecord={vol.Boot.MftRecordSize} mftCluster={vol.Boot.MftCluster}");

                var all = vol.ReadAll(100);
                NtfsFileEntry hello = null, photo = null, folder = null;
                foreach (var e in all)
                {
                    if (e.Name == "hello.txt") hello = e;
                    else if (e.Name == "photo.bin") photo = e;
                    else if (e.Name == "MyFolder") folder = e;
                }

                sb.AppendLine($"records with a name: {all.FindAll(x => !string.IsNullOrEmpty(x.Name)).Count}");
                sb.AppendLine();

                // hello.txt — resident
                sb.AppendLine($"hello.txt: found={hello != null} resident={hello?.Resident} path={(hello != null ? vol.ResolvePath(hello) : "-")}");
                bool helloOk = false;
                if (hello != null)
                {
                    string p = Path.Combine(outDir, "hello.txt");
                    vol.Recover(hello, p);
                    helloOk = File.ReadAllText(p) == SyntheticNtfs.ResidentText;
                    sb.AppendLine($"   recovered text = \"{File.ReadAllText(p)}\"   MATCH={helloOk}");
                }

                // photo.bin — non-resident, inside MyFolder
                sb.AppendLine($"photo.bin: found={photo != null} resident={photo?.Resident} path={(photo != null ? vol.ResolvePath(photo) : "-")}");
                bool photoOk = false, pathOk = false;
                if (photo != null)
                {
                    pathOk = vol.ResolvePath(photo) == "MyFolder\\photo.bin";
                    string p = Path.Combine(outDir, "photo.bin");
                    vol.Recover(photo, p);
                    byte[] got = File.ReadAllBytes(p);
                    byte[] exp = SyntheticNtfs.ExpectedPhoto();
                    photoOk = got.Length == exp.Length;
                    if (photoOk) for (int i = 0; i < exp.Length; i++) if (got[i] != exp[i]) { photoOk = false; break; }
                    sb.AppendLine($"   size={got.Length} (expected {exp.Length})  CONTENT_MATCH={photoOk}  PATH_MATCH={pathOk}");
                }

                sb.AppendLine($"folder MyFolder: found={folder != null} isDir={folder?.IsDirectory}");
                sb.AppendLine();
                bool allOk = helloOk && photoOk && pathOk && folder != null && folder.IsDirectory;
                sb.AppendLine(allOk ? ">>> ALL CHECKS PASSED" : ">>> SOME CHECKS FAILED");
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Parse the MFT of an NTFS volume and report: total entries scanned, deleted-file count,
        /// and a sample of live and deleted files with their reconstructed paths.
        /// </summary>
        public static void MftTest(string physicalPath, long partitionOffset, long maxRecords, string reportPath)
        {
            var sb = new StringBuilder();
            try
            {
                using var vol = NtfsVolume.Open(physicalPath, partitionOffset);
                sb.AppendLine("=== NTFS MFT test ===");
                sb.AppendLine($"device={physicalPath} offset={partitionOffset}");
                sb.AppendLine($"cluster={vol.Boot.ClusterSize}B  mftRecord={vol.Boot.MftRecordSize}B  mftCluster={vol.Boot.MftCluster}");
                sb.AppendLine($"estimated MFT records: {vol.EstimatedRecordCount}");
                sb.AppendLine();

                var all = vol.ReadAll(maxRecords);
                var named = all.Where(e => !string.IsNullOrEmpty(e.Name) && e.HasData).ToList();
                var deleted = named.Where(e => e.IsDeleted).ToList();
                sb.AppendLine($"records read: {all.Count}");
                sb.AppendLine($"named files with data: {named.Count}");
                sb.AppendLine($"deleted (recoverable) files: {deleted.Count}");
                sb.AppendLine();

                sb.AppendLine("--- sample LIVE files ---");
                foreach (var e in named.Where(x => !x.IsDeleted).Take(15))
                    sb.AppendLine($"   #{e.RecordNumber}  {(e.Resident ? "R" : "N")}  {e.DataSize,12}  {vol.ResolvePath(e)}");

                sb.AppendLine();
                sb.AppendLine("--- sample DELETED files ---");
                foreach (var e in deleted.Take(15))
                    sb.AppendLine($"   #{e.RecordNumber}  {(e.Resident ? "R" : "N")}  {e.DataSize,12}  {vol.ResolvePath(e)}");
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Self-contained carving test: builds a synthetic "disk" file with a JPG/PNG/PDF embedded in noise,
        /// runs the scan engine to completion, and reports what was recovered. No admin required.
        /// </summary>
        public static void CarveSelfTest(string outDir, string reportPath)
        {
            var sb = new StringBuilder();
            try
            {
                string img = Path.Combine(Path.GetTempPath(), "diskrescue_synth.img");
                int size = 2 * 1024 * 1024;
                var data = new byte[size];
                for (int i = 0; i < size; i++) data[i] = 0xAB; // noise that matches no header

                // JPG: FF D8 FF E0 ... FF D9
                Place(data, 1000, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22, 0x33, 0x44, 0xFF, 0xD9 });
                // PNG: header ... IEND footer
                Place(data, 50000, new byte[] { 0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, 0x01,0x02,0x03,
                                                0x49,0x45,0x4E,0x44,0xAE,0x42,0x60,0x82 });
                // PDF: %PDF ... %%EOF  (placed so it spans a 4MB-irrelevant boundary but within file)
                Place(data, 800000, AsciiPdf());

                File.WriteAllBytes(img, data);

                string projPath = Path.Combine(Path.GetTempPath(), "diskrescue_synth.drproj");
                var proj = new ScanProject
                {
                    VolumeLetter = '?',
                    DevicePath = img,
                    PartitionOffset = 0,
                    PartitionSize = size,
                    NextOffset = 0,
                    OutputFolder = outDir,
                    CreatedUtc = "test"
                };

                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                var engine = new ScanEngine();
                engine.Scan(proj,
                    progress: null,
                    shouldStop: () => false,
                    checkpoint: pp => ProjectStore.Save(pp, projPath));

                sb.AppendLine("=== Carve self-test ===");
                sb.AppendLine($"synthetic image: {img} ({size} bytes)");
                sb.AppendLine($"files recovered: {proj.FilesFound}");
                foreach (var f in proj.Found)
                    sb.AppendLine($"   {f.Ext}  offset={f.Offset}  size={f.Size}  -> {Path.GetFileName(f.Path)}");
                sb.AppendLine();
                sb.AppendLine("expected: jpg@1000 (~10B), png@50000 (~20B), pdf@800000");
                sb.AppendLine($"project saved+resumable at: {projPath} (NextOffset={proj.NextOffset}, complete={proj.IsComplete})");
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void Place(byte[] buf, int at, byte[] bytes) => Array.Copy(bytes, 0, buf, at, bytes.Length);
        private static byte[] AsciiPdf()
        {
            string s = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF";
            return Encoding.ASCII.GetBytes(s);
        }

        public static void RunReport(string logPath)
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("=== DiskRescue Triage Report ===");
                var volumes = DiskInventory.GetVolumes();
                sb.AppendLine($"נמצאו {volumes.Count} כוננים עם אות.\n");

                foreach (var v in volumes)
                {
                    sb.AppendLine($"---- כונן {v.Letter}:  [{v.DiskModel}]  {v.BusDisplay} ----");
                    sb.AppendLine($"   תווית='{v.Label}'  מערכת קבצים={v.FsDisplay}  גודל={v.SizeDisplay}  בריאות={v.HealthDisplay}");
                    sb.AppendLine($"   diskNo={v.DiskNumber} offset={v.PartitionOffset} partSize={v.PartitionSize}");

                    var t = TriageEngine.Triage(v);
                    sb.AppendLine($"   >> [{t.Severity}] {t.Title}");
                    foreach (var f in t.Findings) sb.AppendLine($"      - {f}");
                    foreach (var act in t.Actions)
                        sb.AppendLine($"      ACTION: {act.Label}  (writes={act.WritesToDisk})");
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
            }
            File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
