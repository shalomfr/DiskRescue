using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using DiskRescue.Core;
using DiskRescue.Core.Ntfs;

namespace DiskRescue
{
    public sealed class NtfsFileItem
    {
        public string State { get; set; }
        public string Type { get; set; }       // R = resident, N = non-resident
        public long Size { get; set; }
        public string SizeText => Format.Bytes(Size);
        public string FullPath { get; set; }
        public NtfsFileEntry Entry { get; set; }
    }

    public partial class FileBrowserWindow : Window
    {
        private readonly VolumeInfo _vol;
        private NtfsVolume _ntfs;
        private List<NtfsFileItem> _all = new List<NtfsFileItem>();
        private volatile bool _cancel;
        private bool _scanning;

        public FileBrowserWindow(VolumeInfo v)
        {
            InitializeComponent();
            ThemeHelper.UseDarkTitleBar(this);
            _vol = v;
            TxtHeader.Text = $"דפדפן קבצים — כונן {v.Letter}:  ({v.FsDisplay})";
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;
            if (!_vol.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("דפדפן הקבצים תומך כרגע ב-NTFS בלבד.", "מערכת קבצים",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            long max = long.TryParse(TxtMax.Text, out var m) ? m : 300000;

            _cancel = false; _scanning = true; SetScanning(true);
            _all.Clear(); GridFiles.ItemsSource = null;
            TxtStatus.Text = "פותח את ה-MFT...";

            Task.Run(() =>
            {
                var items = new List<NtfsFileItem>();
                try
                {
                    _ntfs?.Dispose();
                    _ntfs = NtfsVolume.Open(_vol.PhysicalPath, _vol.PartitionOffset);
                    var progress = new Progress<(long, long)>(t =>
                    {
                        Bar.Value = t.Item2 > 0 ? (double)t.Item1 / t.Item2 * 100 : 0;
                        TxtStatus.Text = $"נסרקו {t.Item1:N0} / {t.Item2:N0} רשומות...";
                    });

                    var entries = _ntfs.ReadAll(max,
                        progress: (n, tot) => ((IProgress<(long, long)>)progress).Report((n, tot)),
                        shouldStop: () => _cancel);

                    foreach (var en in entries)
                    {
                        if (string.IsNullOrEmpty(en.Name) || !en.HasData || en.IsDirectory) continue;
                        items.Add(new NtfsFileItem
                        {
                            State = en.IsDeleted ? "נמחק" : "קיים",
                            Type = en.Resident ? "R" : "N",
                            Size = en.DataSize,
                            FullPath = _ntfs.ResolvePath(en),
                            Entry = en
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => TxtStatus.Text = "שגיאה: " + ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    _all = items;
                    _scanning = false; SetScanning(false);
                    Bar.Value = 100;
                    ApplyFilter();
                    int del = _all.Count(x => x.State == "נמחק");
                    TxtStatus.Text = $"נמצאו {_all.Count:N0} קבצים ({del:N0} נמחקו). בחר ולחץ 'שחזר'.";
                });
            });
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) { _cancel = true; TxtStatus.Text = "מבטל..."; }

        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            IEnumerable<NtfsFileItem> view = _all;
            if (ChkDeletedOnly.IsChecked == true) view = view.Where(x => x.State == "נמחק");
            string q = TxtSearch.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
                view = view.Where(x => x.FullPath.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = view.ToList();
            GridFiles.ItemsSource = list;
            TxtCount.Text = $"{list.Count:N0} מוצגים";
        }

        private async void BtnRecover_Click(object sender, RoutedEventArgs e)
        {
            var selected = GridFiles.SelectedItems.Cast<NtfsFileItem>().ToList();
            if (selected.Count == 0) { TxtStatus.Text = "לא נבחרו קבצים."; return; }

            var dlg = new OpenFolderDialog { Title = "בחר תיקיית יעד לשחזור" };
            if (dlg.ShowDialog() != true) return;
            string root = dlg.FolderName;

            if (Path.GetPathRoot(root).TrimEnd('\\').Equals($"{_vol.Letter}:", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("אסור לשחזר אל אותו כונן. בחר כונן יעד אחר.", "תיקיית יעד",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnRecover.IsEnabled = false;
            int ok = 0, fail = 0;
            await Task.Run(() =>
            {
                foreach (var it in selected)
                {
                    try
                    {
                        string rel = NtfsVolume.SafeRelativePath(it.FullPath);
                        string outPath = Path.Combine(root, rel);
                        _ntfs.Recover(it.Entry, outPath);
                        ok++;
                    }
                    catch { fail++; }
                    int o = ok, f = fail;
                    Dispatcher.Invoke(() => TxtStatus.Text = $"משחזר... הצליחו {o}, נכשלו {f}");
                }
            });
            BtnRecover.IsEnabled = true;
            TxtStatus.Text = $"השחזור הסתיים: {ok} הצליחו, {fail} נכשלו → {root}";
            MessageBox.Show($"שוחזרו {ok} קבצים אל:\n{root}" + (fail > 0 ? $"\n({fail} נכשלו)" : ""),
                "שחזור הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetScanning(bool on)
        {
            BtnScan.IsEnabled = !on; BtnCancel.IsEnabled = on; TxtMax.IsEnabled = !on;
            Cursor = on ? System.Windows.Input.Cursors.AppStarting : null;
        }

        protected override void OnClosed(EventArgs e) { _ntfs?.Dispose(); base.OnClosed(e); }
    }
}
