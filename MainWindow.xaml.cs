using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskRescue.Core;

namespace DiskRescue
{
    public partial class MainWindow : Window
    {
        private List<VolumeInfo> _volumes = new List<VolumeInfo>();

        public MainWindow()
        {
            InitializeComponent();
            ThemeHelper.UseDarkTitleBar(this);
            Loaded += (s, e) => LoadVolumes();
        }

        private void LoadVolumes()
        {
            try
            {
                TxtStatus.Text = "סורק כוננים...";
                _volumes = DiskInventory.GetVolumes();
                GridVolumes.ItemsSource = _volumes;
                TxtStatus.Text = $"נמצאו {_volumes.Count} כוננים.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "שגיאה בסריקת כוננים: " + ex.Message;
            }
        }

        private VolumeInfo Selected => GridVolumes.SelectedItem as VolumeInfo;

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadVolumes();

        private void BtnDeepScan_Click(object sender, RoutedEventArgs e)
        {
            var v = Selected;
            if (v == null) { TxtStatus.Text = "בחר כונן קודם."; return; }
            var win = new DeepScanWindow(v) { Owner = this };
            win.Show();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var v = Selected;
            if (v == null) { TxtStatus.Text = "בחר כונן קודם."; return; }
            var win = new FileBrowserWindow(v) { Owner = this };
            win.Show();
        }

        private void GridVolumes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Selected != null) TxtStatus.Text = $"נבחר כונן {Selected.Letter}:";
        }

        private void BtnDiagnose_Click(object sender, RoutedEventArgs e)
        {
            var v = Selected;
            if (v == null) { TxtStatus.Text = "בחר כונן קודם."; return; }

            ClearLog();
            TxtStatus.Text = $"מאבחן את כונן {v.Letter}: ...";
            PanelActions.Children.Clear();

            TriageResult t;
            try { t = TriageEngine.Triage(v); }
            catch (Exception ex) { TxtStatus.Text = "שגיאת אבחון: " + ex.Message; return; }

            ShowResult(v, t);
            TxtStatus.Text = $"האבחון של כונן {v.Letter}: הסתיים.";
        }

        private void ShowResult(VolumeInfo v, TriageResult t)
        {
            TxtTitle.Text = t.Title;
            TxtSubtitle.Text = $"כונן {v.Letter}:  ·  {v.FsDisplay}  ·  {v.SizeDisplay}  ·  {v.BusDisplay}";
            var sevColor = SeverityColor(t.Severity);
            SeverityDot.Background = new SolidColorBrush(sevColor);
            SeverityBar.Background = new SolidColorBrush(Color.FromArgb(0x26, sevColor.R, sevColor.G, sevColor.B));
            ListFindings.ItemsSource = t.Findings;

            PanelActions.Children.Clear();
            if (t.Actions.Count == 0)
            {
                PanelActions.Children.Add(new TextBlock
                {
                    Text = "✓ אין צורך בפעולה.",
                    Foreground = new SolidColorBrush(SeverityColor(Severity.Ok)),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                return;
            }

            foreach (var act in t.Actions)
            {
                var btn = new Button
                {
                    Content = act.Label,
                    Padding = new Thickness(14, 11, 14, 11),
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Style = (Style)FindResource(act.WritesToDisk ? "BtnWarning" : "BtnGhost"),
                    ToolTip = act.Description
                };
                var captured = act;
                btn.Click += (s, e) => RunAction(v, captured);
                PanelActions.Children.Add(btn);

                PanelActions.Children.Add(new TextBlock
                {
                    Text = act.Description,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 5, 4, 10),
                    FontSize = 12
                });
            }
        }

        private async void RunAction(VolumeInfo v, FixAction act)
        {
            if (act.WritesToDisk)
            {
                var ok = MessageBox.Show(
                    $"פעולה זו תכתוב לכונן {v.Letter}:\n\n{act.Description}\n\nלהמשיך?",
                    "אישור פעולה", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.Yes) return;
            }

            SetBusy(true);
            Log($"== מתחיל: {act.Label} ==");
            try
            {
                string result = await Task.Run(() =>
                {
                    switch (act.Kind)
                    {
                        case FixKind.RestoreBootSector: return SafeFixes.RestoreBootSector(v, LogThreadSafe);
                        case FixKind.RunChkdsk: return SafeFixes.RunChkdsk(v, LogThreadSafe);
                        case FixKind.MechanicalSuspect:
                            return "חשד לתקלה מכנית: כבה את הכונן, אל תריץ סריקות, ושקול שכפול/מעבדה. (מודול ה-imaging בפיתוח)";
                        case FixKind.DeepScanNeeded:
                            return "שחזור עמוק עדיין לא ממומש בגרסה זו.";
                        default: return "אין פעולה.";
                    }
                });
                Log("== הסתיים: " + result);
                TxtStatus.Text = result;
                LoadVolumes(); // refresh health/fs after a fix
            }
            catch (Exception ex)
            {
                Log("שגיאה: " + ex.Message);
                TxtStatus.Text = "הפעולה נכשלה: " + ex.Message;
            }
            finally { SetBusy(false); }
        }

        // ---- logging helpers ----
        private void ClearLog() { TxtLog.Clear(); ListFindings.ItemsSource = null; }
        private void Log(string m) => TxtLog.AppendText(m + Environment.NewLine);
        private void LogThreadSafe(string m) => Dispatcher.Invoke(() => Log(m));
        private void SetBusy(bool busy)
        {
            BtnDiagnose.IsEnabled = !busy; BtnRefresh.IsEnabled = !busy; PanelActions.IsEnabled = !busy;
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private static Color SeverityColor(Severity s) => s switch
        {
            Severity.Ok => Color.FromRgb(0x35, 0xC7, 0x5A),
            Severity.Info => Color.FromRgb(0x5B, 0x8D, 0xEF),
            Severity.Warning => Color.FromRgb(0xF5, 0xA5, 0x24),
            Severity.Critical => Color.FromRgb(0xFF, 0x5A, 0x5A),
            _ => Color.FromRgb(0x6E, 0x6E, 0x80),
        };
    }
}
