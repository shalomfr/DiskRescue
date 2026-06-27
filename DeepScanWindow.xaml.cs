using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using DiskRescue.Core;
using DiskRescue.Core.Carving;

namespace DiskRescue
{
    public partial class DeepScanWindow : Window
    {
        private readonly VolumeInfo _vol;
        private ScanProject _project;
        private volatile bool _stopRequested;
        private bool _running;
        private string _autoSavePath;
        private readonly ObservableCollection<CarvedFile> _files = new ObservableCollection<CarvedFile>();

        public DeepScanWindow(VolumeInfo v)
        {
            InitializeComponent();
            ThemeHelper.UseDarkTitleBar(this);
            _vol = v;
            TxtHeader.Text = $"שחזור עמוק — כונן {v.Letter}:  ({v.FsDisplay}, {v.SizeDisplay})";
            TxtOut.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"DiskRescue_{v.Letter}");
            GridFiles.ItemsSource = _files;
            UpdateButtons();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "בחר תיקיית יעד לשחזור" };
            if (dlg.ShowDialog() == true) TxtOut.Text = dlg.FolderName;
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtOut.Text)) { TxtStats.Text = "בחר תיקיית יעד."; return; }
            if (IsUnderDevice(TxtOut.Text))
            {
                MessageBox.Show("אסור לשחזר אל אותו כונן שמשוחזר — בחר כונן יעד אחר.", "תיקיית יעד",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_project == null)
            {
                _project = new ScanProject
                {
                    VolumeLetter = _vol.Letter,
                    DevicePath = _vol.PhysicalPath,
                    PartitionOffset = _vol.PartitionOffset,
                    PartitionSize = _vol.PartitionSize,
                    NextOffset = _vol.PartitionOffset,
                    OutputFolder = TxtOut.Text,
                    CreatedUtc = DateTime.UtcNow.ToString("u")
                };
                _autoSavePath = Path.Combine(TxtOut.Text, "scan.drproj");
            }
            StartScan();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { _stopRequested = true; TxtStats.Text = "משהה ושומר נקודת המשך..."; }
            else if (_project != null && !_project.IsComplete) StartScan(); // resume
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _stopRequested = true;
            TxtStats.Text = "עוצר ושומר...";
        }

        private void StartScan()
        {
            _stopRequested = false; _running = true; UpdateButtons();
            var engine = new ScanEngine();
            var progress = new Progress<ScanProgress>(OnProgress);

            Task.Run(() =>
            {
                try
                {
                    engine.Scan(_project, progress,
                        shouldStop: () => _stopRequested,
                        checkpoint: p => Dispatcher.Invoke(() => OnCheckpoint(p)));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => TxtStats.Text = "שגיאה: " + ex.Message);
                }
                Dispatcher.Invoke(OnScanReturned);
            });
        }

        private void OnProgress(ScanProgress p)
        {
            Bar.Value = p.Percent;
            TxtStats.Text =
                $"נסרק {Format.Bytes(p.BytesScanned)} מתוך {Format.Bytes(p.TotalBytes)}  ·  {p.Percent:0.0}%  ·  " +
                $"נמצאו {p.FilesFound} קבצים" + (p.BadSectorSkips > 0 ? $"  ·  דילוגי סקטור פגום: {p.BadSectorSkips}" : "");
        }

        private void OnCheckpoint(ScanProject p)
        {
            // Background thread is blocked inside Dispatcher.Invoke here, so reading Found is safe.
            for (int i = _files.Count; i < p.Found.Count; i++) _files.Add(p.Found[i]);
            try { ProjectStore.Save(p, _autoSavePath); } catch { /* non-fatal */ }
        }

        private void OnScanReturned()
        {
            _running = false;
            // sync any stragglers
            for (int i = _files.Count; i < _project.Found.Count; i++) _files.Add(_project.Found[i]);
            if (_project.IsComplete)
            {
                Bar.Value = 100;
                TxtStats.Text = $"הסתיים. שוחזרו {_project.FilesFound} קבצים אל {_project.OutputFolder}";
            }
            else
            {
                TxtStats.Text = $"מושהה ב-{Format.Bytes(_project.NextOffset - _project.PartitionOffset)}. " +
                                $"נמצאו {_project.FilesFound} קבצים. לחץ 'המשך' כדי להמשיך.";
            }
            UpdateButtons();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) { TxtStats.Text = "אין פרויקט לשמירה."; return; }
            var dlg = new SaveFileDialog { Filter = "DiskRescue project|*.drproj", FileName = $"scan_{_vol.Letter}.drproj" };
            if (dlg.ShowDialog() == true)
            {
                ProjectStore.Save(_project, dlg.FileName);
                TxtStats.Text = "הפרויקט נשמר: " + dlg.FileName;
            }
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "DiskRescue project|*.drproj" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _project = ProjectStore.Load(dlg.FileName);
                _autoSavePath = dlg.FileName;
                TxtOut.Text = _project.OutputFolder;
                _files.Clear();
                foreach (var f in _project.Found) _files.Add(f);
                Bar.Value = _project.PartitionSize > 0
                    ? (double)(_project.NextOffset - _project.PartitionOffset) / _project.PartitionSize * 100 : 0;
                TxtStats.Text = _project.IsComplete
                    ? $"פרויקט שהושלם. {_project.FilesFound} קבצים."
                    : $"פרויקט נטען. נסרקו {Bar.Value:0.0}%, {_project.FilesFound} קבצים. לחץ 'המשך'.";
                UpdateButtons();
            }
            catch (Exception ex) { TxtStats.Text = "טעינת פרויקט נכשלה: " + ex.Message; }
        }

        private void UpdateButtons()
        {
            bool hasResumable = _project != null && !_project.IsComplete;
            BtnStart.IsEnabled = !_running && _project == null;
            BtnPause.IsEnabled = _running || (hasResumable && !_running);
            BtnPause.Content = _running ? "⏸ השהה" : "▶ המשך";
            BtnStop.IsEnabled = _running;
            BtnBrowse.IsEnabled = !_running && _project == null;
            BtnLoad.IsEnabled = !_running;
            BtnSave.IsEnabled = !_running && _project != null;
        }

        private bool IsUnderDevice(string path)
        {
            try { return Path.GetPathRoot(Path.GetFullPath(path))
                .TrimEnd('\\').Equals($"{_vol.Letter}:", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_running)
            {
                var r = MessageBox.Show("סריקה פעילה. לעצור ולצאת?", "יציאה",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                _stopRequested = true;
            }
            base.OnClosing(e);
        }
    }
}
