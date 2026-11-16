using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace PerfWidget;

/// <summary>
/// CPU temperature. Windows has no driver-free way to read Ryzen temps, so this reads LibreHardwareMonitor's
/// WMI sensors when that app is running. Returns null otherwise; retries every minute.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class CpuTempReader
{
    long nextTry;
    double? last;
    public string? Source { get; private set; }

    public double? Read()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < nextTry) return last;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor",
                "SELECT Identifier, Name, Value FROM Sensor WHERE SensorType='Temperature'");
            double? best = null, any = null;
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    string id = (mo["Identifier"] as string ?? "").ToLowerInvariant();
                    string name = mo["Name"] as string ?? "";
                    if (!id.Contains("cpu")) continue;
                    double v = Convert.ToDouble(mo["Value"]);
                    if (v <= 0 || v > 125) continue;
                    if (name.Contains("Tctl") || name.Contains("Tdie") || name.Contains("Package")) best = Math.Max(best ?? 0, v);
                    any = Math.Max(any ?? 0, v);
                }
            }
            last = best ?? any;
            Source = last != null ? "LibreHardwareMonitor" : null;
            nextTry = now + Stopwatch.Frequency * (last != null ? 3 : 60);
        }
        catch
        {
            last = null;
            Source = null;
            nextTry = now + Stopwatch.Frequency * 60;
        }
        return last;
    }
}
