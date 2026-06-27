using System;
using System.Collections.Generic;
using System.Management;

namespace DiskRescue.Core.Imaging
{
    public sealed class SmartInfo
    {
        public string HealthStatus = "לא ידוע";
        public string MediaType = "";
        public bool PredictFailure;
        public int? TemperatureC;
        public long? ReadErrorsUncorrected;
        public long? ReadErrorsTotal;
        public int? WearPercent;
        public ulong? PowerOnHours;
        public bool Available;
        public List<string> Notes = new List<string>();

        public bool LooksFailing =>
            PredictFailure || HealthStatus == "תקול" ||
            (ReadErrorsUncorrected ?? 0) > 0 || (WearPercent ?? 0) >= 90;
    }

    /// <summary>Reads drive health via the Storage WMI namespace (MSFT_PhysicalDisk + reliability counters).</summary>
    public static class SmartReader
    {
        public static SmartInfo Read(uint diskNumber)
        {
            var info = new SmartInfo();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                var q = new ObjectQuery($"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId='{diskNumber}'");
                using var searcher = new ManagementObjectSearcher(scope, q);
                foreach (ManagementObject pd in searcher.Get())
                {
                    info.Available = true;
                    info.HealthStatus = MapHealth(pd["HealthStatus"]);
                    info.MediaType = MapMedia(pd["MediaType"]);

                    try
                    {
                        foreach (ManagementObject rc in pd.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            info.TemperatureC = ToInt(rc["Temperature"]);
                            info.ReadErrorsUncorrected = ToLong(rc["ReadErrorsUncorrected"]);
                            info.ReadErrorsTotal = ToLong(rc["ReadErrorsTotal"]);
                            info.WearPercent = ToInt(rc["Wear"]);
                            info.PowerOnHours = (ulong?)ToLong(rc["PowerOnHours"]);
                        }
                    }
                    catch (Exception ex) { info.Notes.Add("מוני אמינות לא זמינים: " + ex.Message); }
                }
            }
            catch (Exception ex) { info.Notes.Add("SMART לא זמין: " + ex.Message); }

            // best-effort failure prediction flag
            try
            {
                var scope2 = new ManagementScope(@"\\.\root\WMI");
                scope2.Connect();
                using var s2 = new ManagementObjectSearcher(scope2, new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus"));
                foreach (ManagementObject o in s2.Get())
                    if (o["PredictFailure"] is bool b && b) info.PredictFailure = true;
            }
            catch { /* not exposed on all systems */ }

            return info;
        }

        private static string MapHealth(object o)
        {
            try { return Convert.ToInt32(o) switch { 0 => "תקין", 1 => "אזהרה", 2 => "תקול", _ => "לא ידוע" }; }
            catch { return "לא ידוע"; }
        }
        private static string MapMedia(object o)
        {
            try { return Convert.ToInt32(o) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" }; }
            catch { return ""; }
        }
        private static int? ToInt(object o) { try { return o == null ? (int?)null : Convert.ToInt32(o); } catch { return null; } }
        private static long? ToLong(object o) { try { return o == null ? (long?)null : Convert.ToInt64(o); } catch { return null; } }
    }
}
