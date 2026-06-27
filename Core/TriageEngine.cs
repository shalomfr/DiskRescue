using System;
using System.Diagnostics;

namespace DiskRescue.Core
{
    /// <summary>
    /// The "triage before surgery" decision tree. Read-only.
    /// Decides whether a volume needs a 30-second fix, a chkdsk, or real recovery —
    /// and whether the symptoms look mechanical (stop, go to a lab).
    /// </summary>
    public static class TriageEngine
    {
        public static TriageResult Triage(VolumeInfo v)
        {
            var r = new TriageResult { Letter = v.Letter };

            // Hardware first: an unhealthy physical disk means we must be careful.
            if (v.HealthStatus == 2)
            {
                r.Severity = Severity.Critical;
                r.Title = "הדיסק הפיזי מדווח על תקלה";
                r.Findings.Add("מערכת ההפעלה מדווחת שהדיסק הפיזי במצב 'תקול'.");
                r.Findings.Add("אם נשמע תקתוק או יש שגיאות קריאה — כבה את הכונן ואל תריץ סריקות.");
                r.Actions.Add(new FixAction(FixKind.MechanicalSuspect,
                    "חשד לתקלה מכנית — מה לעשות",
                    "שכפול מיידי של הסקטורים הקריאים (imaging) לפני שהכונן מתדרדר, או פנייה למעבדה.", false));
                return r;
            }

            var boot = BootSectorAnalyzer.Analyze(v);
            r.Boot = boot;

            if (!boot.Opened)
            {
                r.Severity = Severity.Warning;
                r.Title = "לא ניתן לקרוא את הכונן";
                r.Findings.Add("פתיחת ההתקן נכשלה: " + boot.OpenError);
                r.Findings.Add("ייתכן שהכונן מנותק, או שאין הרשאות. נסה לחבר מחדש.");
                return r;
            }

            // Case 1: volume is RAW / not mounted by the OS.
            if (v.IsRaw)
            {
                if (boot.MainAllZero && boot.BackupValid)
                {
                    r.Severity = Severity.Warning;
                    r.Title = $"סקטור האתחול נמחק — אך יש גיבוי תקין ({boot.DetectedFs})";
                    r.Findings.Add("סקטור האתחול הראשי ריק לחלוטין (אפסים) — לכן הכונן מופיע כ-RAW.");
                    foreach (var n in boot.Notes) r.Findings.Add(n);
                    if (boot.MftLooksOk) r.Findings.Add("המידע עצמו שלם — צפוי שחזור מלא.");
                    r.Actions.Add(new FixAction(FixKind.RestoreBootSector,
                        "שחזר סקטור אתחול מהגיבוי (מומלץ)",
                        "מעתיק את סקטור האתחול התקין מהגיבוי חזרה לסקטור 0. שומר קודם עותק לביטול.", true));
                    return r;
                }
                if (boot.BackupValid)
                {
                    r.Severity = Severity.Warning;
                    r.Title = $"מערכת הקבצים לא נטענת — נמצא גיבוי ({boot.DetectedFs})";
                    r.Findings.Add("סקטור האתחול הראשי פגום, אך קיים גיבוי תקין לשחזור.");
                    foreach (var n in boot.Notes) r.Findings.Add(n);
                    r.Actions.Add(new FixAction(FixKind.RestoreBootSector,
                        "שחזר סקטור אתחול מהגיבוי",
                        "מעתיק את הגיבוי לסקטור 0. שומר קודם עותק לביטול.", true));
                    return r;
                }
                r.Severity = Severity.Critical;
                r.Title = "מערכת הקבצים פגומה — נדרש שחזור עמוק";
                r.Findings.Add("גם סקטור האתחול הראשי וגם הגיבוי אינם תקינים.");
                foreach (var n in boot.Notes) r.Findings.Add(n);
                r.Actions.Add(new FixAction(FixKind.DeepScanNeeded,
                    "נדרש שחזור עמוק (Deep Scan)",
                    "אין תיקון מהיר. יש לסרוק את הכונן ולשחזר קבצים לפי חתימות. (בפיתוח)", false));
                return r;
            }

            // Case 2: volume mounts fine — check the dirty flag.
            bool dirty = boot.Dirty ?? IsNtfsDirty(v.Letter);
            if (dirty)
            {
                r.Severity = Severity.Warning;
                r.Title = "הכונן תקין אך מסומן 'מלוכלך' (Dirty)";
                r.Findings.Add($"מערכת הקבצים ({boot.DetectedFs}) נטענת והמידע נגיש.");
                r.Findings.Add("נקבע דגל 'מלוכלך' אחרי ניתוק/כיבוי לא תקין — זו הסיבה לאזהרת 'Full Repair Needed'.");
                r.Actions.Add(new FixAction(FixKind.RunChkdsk,
                    "תקן עם chkdsk וכבה את הדגל (בטוח)",
                    "מריץ chkdsk /f כדי לתקן אי-עקביות קלה ולכבות את הדגל. בטוח — הכונן מזוהה והמידע נגיש.", true));
                return r;
            }

            r.Severity = v.HealthStatus == 1 ? Severity.Info : Severity.Ok;
            r.Title = v.HealthStatus == 1 ? "הכונן תקין (אזהרה קלה מהמערכת)" : "הכונן תקין";
            r.Findings.Add($"מערכת הקבצים ({boot.DetectedFs}) תקינה והמידע נגיש. לא נדרשת פעולה.");
            return r;
        }

        private static bool IsNtfsDirty(char letter)
        {
            try
            {
                var psi = new ProcessStartInfo("fsutil", $"dirty query {letter}:")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return outp.IndexOf("is Dirty", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }
}
