using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using DiskRescue.Core;
using DiskRescue.Core.Imaging;

namespace DiskRescue
{
    public partial class ImagingWindow : Window
    {
        private readonly VolumeInfo _vol;
        private ImagingMap _map;
        private string _imagePath;
        private string _mapPath;
        private volatile bool _stopRequested;
        private bool _running;

        public ImagingWindow(VolumeInfo v)
        {
            InitializeComponent();
            ThemeHelper.UseDarkTitleBar(this);
            _vol = v;
            TxtHeader.Text = $"שכפול דיסק — כונן {v.Letter}:  ({v.FsDisplay}, {v.SizeDisplay})";
            TxtImage.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"DiskRescue_{v.Letter}.img");
            Loaded += (s, e) => LoadSmart();
        }

        private void BtnSmart_Click(object sender, RoutedEventArgs e) => LoadSmart();

        private void LoadSmart()
        {
            SmartPanel.Children.Clear();
            SmartPanel.Children.Add(new TextBlock { Text = "קורא SMART…", Foreground = (Brush)FindResource("TextMutedBrush") });
            Task.Run(() => SmartReader.Read(_vol.DiskNumber)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() => RenderSmart(t.Result));
            });
        }

        private void RenderSmart(SmartInfo s)
        {
            SmartPanel.Children.Clear();
            if (s == null || !s.Available)
            {
                SmartPanel.Children.Add(new TextBlock { Text = "נתוני SMART לא זמינים לכונן זה.", Foreground = (Brush)FindResource("TextMutedBrush") });
                return;
            }
            AddPill("בריאות", s.HealthStatus, s.HealthStatus == "תקול");
            AddPill("חיזוי כשל", s.PredictFailure ? "כן ⚠" : "לא", s.PredictFailure);
            if (!string.IsNullOrEmpty(s.MediaType)) AddPill("סוג", s.MediaType, false);
            if (s.TemperatureC.HasValue) AddPill("טמפ׳", $"{s.TemperatureC}°C", s.TemperatureC >= 60);
            if (s.ReadErrorsUncorrected.HasValue) AddPill("שגיאות קריאה", s.ReadErrorsUncorrected.ToString(), s.ReadErrorsUncorrected > 0);
            if (s.WearPercent.HasValue) AddPill("בלאי", $"{s.WearPercent}%", s.WearPercent >= 90);
            if (s.PowerOnHours.HasValue) AddPill("שעות פעולה", s.PowerOnHours.ToString(), false);

            if (s.LooksFailing)
                SmartPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ הכונן מראה סימני כשל — שכפל עכשיו ואל תריץ עליו סריקות.",
                    Foreground = (Brush)FindResource("DangerBrush"),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
        }

        private void AddPill(string label, string value, bool bad)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource(bad ? "DangerBrush" : "Bg2Brush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 8)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = label + ": ", Foreground = (Brush)FindResource(bad ? "TextPrimaryBrush" : "TextMutedBrush"), FontSize = 12.5 });
            sp.Children.Add(new TextBlock { Text = value, Foreground = bad ? Brushes.White : (Brush)FindResource("TextPrimaryBrush"), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
            border.Child = sp;
            SmartPanel.Children.Add(border);
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Disk image|*.img", FileName = Path.GetFileName(TxtImage.Text) };
            if (dlg.ShowDialog() == true) TxtImage.Text = dlg.FileName;
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            _imagePath = TxtImage.Text?.Trim();
            if (string.IsNullOrEmpty(_imagePath)) { TxtStats.Text = "בחר קובץ יעד."; return; }
            if (Path.GetPathRoot(Path.GetFullPath(_imagePath)).TrimEnd('\\').Equals($"{_vol.Letter}:", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("אסור לשמור את התמונה על אותו כונן שמשכפלים. בחר כונן יעד אחר.", "יעד",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _mapPath = _imagePath + ".drmap";
            _map = File.Exists(_mapPath) ? ImagingMap.Load(_mapPath) : ImagingMap.Create(_vol.PartitionSize);
            StartImaging();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { _stopRequested = true; TxtStats.Text = "משהה ושומר מפה…"; }
            else StartImaging(); // resume
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) { _stopRequested = true; TxtStats.Text = "עוצר…"; }

        private void StartImaging()
        {
            _stopRequested = false; _running = true; SetRunning(true);
            var engine = new ImagingEngine { SmartProvider = SmartReader.Read, DiskNumber = _vol.DiskNumber };
            var progress = new Progress<ImagingProgress>(OnProgress);

            Task.Run(() =>
            {
                try
                {
                    using var reader = new RawDeviceReader(_vol.PhysicalPath, _vol.PartitionOffset, _vol.PartitionSize);
                    engine.Run(reader, _imagePath, _map, progress,
                        shouldStop: () => _stopRequested,
                        checkpoint: m => { try { m.Save(_mapPath); } catch { } });
                }
                catch (Exception ex) { Dispatcher.Invoke(() => TxtStats.Text = "שגיאה: " + ex.Message); }
                Dispatcher.Invoke(OnDone);
            });
        }

        private void OnProgress(ImagingProgress p)
        {
            Bar.Value = p.Percent;
            string rate = p.BytesPerSec > 0 ? $"  ·  {Format.Bytes((long)p.BytesPerSec)}/s" : "";
            string smart = p.Smart != null && p.Smart.PredictFailure ? "  ·  ⚠ חיזוי כשל!" : "";
            TxtStats.Text =
                $"מעבר {p.Pass}  ·  הועתק {Format.Bytes(p.DoneBytes)} / {Format.Bytes(p.TotalBytes)} ({p.Percent:0.0}%)" +
                $"  ·  פגום {Format.Bytes(p.BadBytes)}{rate}{smart}";
            if (p.Smart != null) RenderSmart(p.Smart);
        }

        private void OnDone()
        {
            _running = false; SetRunning(false);
            long done = _map?.Bytes(BlockState.Done) ?? 0;
            long bad = (_map?.Bytes(BlockState.Bad) ?? 0) + (_map?.Bytes(BlockState.BadFinal) ?? 0);
            bool complete = bad == 0 && done >= _vol.PartitionSize;
            TxtStats.Text = complete
                ? $"השכפול הושלם: {Format.Bytes(done)} → {_imagePath}"
                : $"נעצר/חלקי: הועתק {Format.Bytes(done)}, פגום {Format.Bytes(bad)}. אפשר להמשיך, או לשחזר מהתמונה.";
        }

        // ---- recover from the image ----
        private VolumeInfo ImageVolume()
        {
            if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath)) return null;
            long len = new FileInfo(_imagePath).Length;
            return new VolumeInfo
            {
                Letter = '#',
                Label = "(תמונה)",
                FileSystem = "NTFS",          // browser assumes NTFS; deep scan ignores this
                SourcePathOverride = _imagePath,
                PartitionOffset = 0,
                PartitionSize = len,
                SizeBytes = (ulong)len
            };
        }

        private void BtnBrowseImg_Click(object sender, RoutedEventArgs e)
        {
            var iv = ImageVolume();
            if (iv == null) { TxtStats.Text = "אין קובץ תמונה עדיין."; return; }
            new FileBrowserWindow(iv) { Owner = this }.Show();
        }

        private void BtnDeepImg_Click(object sender, RoutedEventArgs e)
        {
            var iv = ImageVolume();
            if (iv == null) { TxtStats.Text = "אין קובץ תמונה עדיין."; return; }
            new DeepScanWindow(iv) { Owner = this }.Show();
        }

        private void SetRunning(bool on)
        {
            BtnStart.IsEnabled = !on; BtnPause.IsEnabled = on || (_map != null);
            BtnPause.Content = on ? "⏸  השהה" : "▶  המשך";
            BtnStop.IsEnabled = on; BtnBrowse.IsEnabled = !on;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_running)
            {
                var r = MessageBox.Show("שכפול פעיל. לעצור ולצאת?", "יציאה", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                _stopRequested = true;
            }
            base.OnClosing(e);
        }
    }
}
