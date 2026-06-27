using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using DiskRescue.Core;

namespace DiskRescue
{
    public partial class App : Application
    {
        private static bool IsElevated()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Register legacy code pages (e.g. cp850 used by chkdsk console output) — not built into .NET by default.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // Headless verification mode:  DiskRescue.exe --cli <reportPath>
            if (e.Args.Length >= 1 && e.Args[0] == "--cli")
            {
                string path = e.Args.Length >= 2 ? e.Args[1] : "diskrescue_report.txt";
                CliRunner.RunReport(path);
                Shutdown(0);
                return;
            }

            // Carving engine self-test (no admin):  DiskRescue.exe --carvetest <outDir> <reportPath>
            if (e.Args.Length >= 1 && e.Args[0] == "--carvetest")
            {
                string outDir = e.Args.Length >= 2 ? e.Args[1] : "carve_out";
                string rep = e.Args.Length >= 3 ? e.Args[2] : "carve_report.txt";
                CliRunner.CarveSelfTest(outDir, rep);
                Shutdown(0);
                return;
            }

            // Imaging engine self-test (no admin):  DiskRescue.exe --imagetest <reportPath>
            if (e.Args.Length >= 1 && e.Args[0] == "--imagetest")
            {
                CliRunner.ImageSelfTest(e.Args.Length >= 2 ? e.Args[1] : "image_report.txt");
                Shutdown(0);
                return;
            }

            // NTFS synthetic self-test (no admin):  DiskRescue.exe --ntfstest <outDir> <reportPath>
            if (e.Args.Length >= 1 && e.Args[0] == "--ntfstest")
            {
                string outDir = e.Args.Length >= 2 ? e.Args[1] : "ntfs_out";
                string rep = e.Args.Length >= 3 ? e.Args[2] : "ntfs_report.txt";
                CliRunner.NtfsSelfTest(outDir, rep);
                Shutdown(0);
                return;
            }

            // NTFS MFT test (admin):  DiskRescue.exe --mfttest <physicalPath> <partOffset> <maxRecords> <reportPath>
            if (e.Args.Length >= 1 && e.Args[0] == "--mfttest")
            {
                string phys = e.Args.Length >= 2 ? e.Args[1] : "\\\\.\\PhysicalDrive0";
                long off = e.Args.Length >= 3 && long.TryParse(e.Args[2], out var o) ? o : 0;
                long max = e.Args.Length >= 4 && long.TryParse(e.Args[3], out var m) ? m : 50000;
                string rep = e.Args.Length >= 5 ? e.Args[4] : "mft_report.txt";
                CliRunner.MftTest(phys, off, max, rep);
                Shutdown(0);
                return;
            }

            // GUI path: guarantee full permissions. If somehow launched without elevation
            // (e.g. via "dotnet DiskRescue.dll"), relaunch the elevated apphost and exit.
            if (!IsElevated())
            {
                try
                {
                    string exe = Path.Combine(AppContext.BaseDirectory, "DiskRescue.exe");
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                }
                catch { /* user declined UAC — nothing to do */ }
                Shutdown(0);
                return;
            }

            base.OnStartup(e);
            new MainWindow().Show();
        }
    }
}
