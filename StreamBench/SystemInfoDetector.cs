// SystemInfoDetector.cs
// Cross-platform hardware detection using .NET 10 APIs.
// Replaces the platform-specific C code that was in stream_hwinfo.h.
//
// Detection strategy:
//   macOS  — sysctl, sw_vers, system_profiler (no root needed)
//   Linux  — /proc, /sys file reads; dmidecode for memory modules (may need sudo)
//   Windows — PowerShell WMI queries (encoded command, no escaping issues)
//
// All methods are exception-safe and return sensible defaults on failure.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using StreamBench.Models;

namespace StreamBench;

public static class SystemInfoDetector
{
    /// <summary>
    /// Detects system, memory module, and cache info in parallel.
    /// Returns populated objects; falls back to empty/zero values when detection fails.
    /// </summary>
    public static async Task<(SystemInfo System, MemoryInfo Memory, CacheInfo Cache)> DetectAsync()
    {
        TraceLog.SystemInfoDetectionStarted();

        var systemTask = Task.Run(DetectSystem);
        var memoryTask = Task.Run(DetectMemory);
        var cacheTask  = Task.Run(DetectCache);
        await Task.WhenAll(systemTask, memoryTask, cacheTask);

        TraceLog.SystemInfoDetectionCompleted();
        return (systemTask.Result, memoryTask.Result, cacheTask.Result);
    }

    // ── System Info ───────────────────────────────────────────────────────

    private static SystemInfo DetectSystem()
    {
        string hostname = Environment.MachineName;
        string os       = SafeDetect("OS", GetOsName, RuntimeInformation.OSDescription);
        string arch     = RuntimeInformation.OSArchitecture.ToString();
        string cpu      = SafeDetect("CPU", GetCpuModel, "Unknown");
        int    cpus     = Environment.ProcessorCount;
        double ram      = SafeDetect("RAM", GetTotalRamGb, 0.0);
        var (baseMhz, maxMhz) = SafeDetect("CPUFreq", GetCpuFreqMhz, (0, 0));
        int    numa     = SafeDetect("NUMA", GetNumaNodes, 1);

        return new SystemInfo(
            Hostname:     hostname,
            Os:           os,
            Architecture: arch,
            CpuModel:     cpu,
            LogicalCpus:  cpus,
            CpuBaseMhz:   baseMhz,
            CpuMaxMhz:    maxMhz > 0 && maxMhz != baseMhz ? maxMhz : null,
            TotalRamGb:   ram,
            NumaNodes:    numa);
    }

    private static string GetOsName()
    {
        if (IsOSX)
        {
            string v = Run("sw_vers", "-productVersion").Trim();
            return string.IsNullOrEmpty(v) ? "macOS" : $"macOS {v}";
        }
        if (IsLinux)
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
                if (line.StartsWith("PRETTY_NAME="))
                    return line[12..].Trim('"');
            return "Linux";
        }
        return RuntimeInformation.OSDescription;
    }

    private static string GetCpuModel()
    {
        if (IsOSX)
            return Run("sysctl", "-n machdep.cpu.brand_string").Trim();

        if (IsLinux)
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name") || line.StartsWith("Model"))
                {
                    int i = line.IndexOf(':');
                    if (i >= 0) return line[(i + 1)..].Trim();
                }
            return "Unknown";
        }
        // Windows: WMI via PowerShell
        return RunPowerShell("(Get-WmiObject Win32_Processor | Select-Object -First 1).Name").Trim();
    }

    private static double GetTotalRamGb()
    {
        if (IsOSX)
        {
            if (long.TryParse(Run("sysctl", "-n hw.memsize").Trim(), out long b))
                return b / (1024.0 * 1024.0 * 1024.0);
        }
        else if (IsLinux)
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemTotal:"))
                {
                    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2 && long.TryParse(p[1], out long kb))
                        return kb / (1024.0 * 1024.0);
                }
        }
        else
        {
            // Windows: TotalPhysicalMemory in bytes
            if (long.TryParse(
                RunPowerShell("(Get-WmiObject Win32_ComputerSystem).TotalPhysicalMemory").Trim(),
                out long b))
                return b / (1024.0 * 1024.0 * 1024.0);
        }
        return 0;
    }

    private static (int Base, int Max) GetCpuFreqMhz()
    {
        if (IsLinux)
        {
            int b = 0, m = 0;
            var bf = TryReadFile("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency");
            if (int.TryParse(bf?.Trim(), out int bk)) b = bk / 1000;
            var mf = TryReadFile("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
            if (int.TryParse(mf?.Trim(), out int mk)) m = mk / 1000;
            if (b == 0)
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("cpu MHz"))
                    {
                        int ci = line.IndexOf(':');
                        if (ci >= 0 && double.TryParse(line[(ci + 1)..].Trim(), out double mhz))
                        { b = (int)mhz; break; }
                    }
            return (b, m);
        }
        if (IsOSX)
        {
            // x64 macOS exposes hw.cpufrequency; ARM64 macOS returns 0
            if (long.TryParse(Run("sysctl", "-n hw.cpufrequency").Trim(), out long hz) && hz > 0)
                return ((int)(hz / 1_000_000), 0);
            return (0, 0);
        }
        // Windows: MaxClockSpeed in MHz
        if (int.TryParse(
            RunPowerShell("(Get-WmiObject Win32_Processor | Select-Object -First 1).MaxClockSpeed").Trim(),
            out int mhzW))
            return (mhzW, mhzW);
        return (0, 0);
    }

    private static int GetNumaNodes()
    {
        if (!IsLinux) return 1;
        int count = 0;
        for (int i = 0; i < 256; i++)
        {
            if (File.Exists($"/sys/devices/system/node/node{i}/cpulist"))
                count++;
            else
                break;
        }
        return count > 0 ? count : 1;
    }

    // ── Cache Info ────────────────────────────────────────────────────────

    private static CacheInfo DetectCache()
    {
        int l1d = 0, l1i = 0, l2 = 0, l3 = 0;
        try
        {
            if (IsOSX)
            {
                l1d = SysctlKb("hw.l1dcachesize");
                l1i = SysctlKb("hw.l1icachesize");
                l2  = SysctlKb("hw.l2cachesize");
                l3  = SysctlKb("hw.l3cachesize");
            }
            else if (IsLinux)
            {
                for (int i = 0; i < 10; i++)
                {
                    string bp = $"/sys/devices/system/cpu/cpu0/cache/index{i}";
                    if (!File.Exists($"{bp}/level")) break;
                    int    level = int.Parse(File.ReadAllText($"{bp}/level").Trim());
                    string type  = File.ReadAllText($"{bp}/type").Trim();
                    string szStr = File.ReadAllText($"{bp}/size").Trim(); // e.g. "32K" or "12M"
                    int    kb    = szStr.EndsWith('M')
                        ? int.Parse(szStr[..^1]) * 1024
                        : int.Parse(szStr.TrimEnd('K'));
                    switch (level)
                    {
                        case 1 when type is "Data" or "Unified": l1d = kb; break;
                        case 1 when type == "Instruction":       l1i = kb; break;
                        case 2: l2 = kb; break;
                        case 3: l3 = kb; break;
                    }
                }
            }
            else // Windows: WMI L2/L3 sizes are in KB
            {
                string script = """
$cpu = Get-WmiObject Win32_Processor | Select-Object -First 1
@{ L2=$cpu.L2CacheSize; L3=$cpu.L3CacheSize } | ConvertTo-Json
""";
                var json = RunPowerShell(script).Trim();
                if (!string.IsNullOrEmpty(json))
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("L2", out var l2e)) l2 = l2e.GetInt32();
                        if (doc.RootElement.TryGetProperty("L3", out var l3e)) l3 = l3e.GetInt32();
                    }
            }
        }
        catch (Exception ex)
        {
            TraceLog.SystemInfoDetectionWarning("Cache", ex.Message);
        }
        return new CacheInfo(l1d, l1i, l2, l3);
    }

    // ── Memory Module Info ────────────────────────────────────────────────

    private static readonly MemoryInfo NoMemoryInfo = new(null, 0, 0, 0, 0, null, false);

    private static MemoryInfo DetectMemory()
    {
        try
        {
            if (IsOSX)   return DetectMemoryMacOS();
            if (IsLinux) return DetectMemoryLinux();
            return DetectMemoryWindows();
        }
        catch (Exception ex)
        {
            TraceLog.SystemInfoDetectionWarning("Memory", ex.Message);
            return NoMemoryInfo;
        }
    }

    private static MemoryInfo DetectMemoryMacOS()
    {
        var json = Run("system_profiler", "SPMemoryDataType -json");
        if (string.IsNullOrWhiteSpace(json)) return NoMemoryInfo;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("SPMemoryDataType", out var arr)) return NoMemoryInfo;

        var modules = new List<MemoryModule>();
#pragma warning disable IDISP004 // JsonElement.ArrayEnumerator is disposed by foreach
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("dimm_size", out _))
            {
                // Standard DIMM entry (Intel Mac / Mac Pro)
                string locator = StrProp(item, "_name") ?? StrProp(item, "dimm_bank") ?? "";
                int    sizeMb  = ParseSizeMb(StrProp(item, "dimm_size") ?? "");
                string type    = StrProp(item, "dimm_type") ?? "";
                int    speed   = ParseSpeedMts(StrProp(item, "dimm_speed") ?? "");
                string mfr     = StrProp(item, "dimm_manufacturer") ?? "";
                string part    = StrProp(item, "dimm_part") ?? "";
                if (sizeMb > 0)
                    modules.Add(MakeModule(locator, sizeMb, type, "SO-DIMM", speed, speed, mfr, part));
            }
            else
            {
                // ARM64 macOS unified memory: total is in "SPMemoryDataType"; no per-slot info
                string? dimmType = StrProp(item, "dimm_type");
                string? totalStr = StrProp(item, "SPMemoryDataType");
                if (dimmType is null || totalStr is null) continue;
                int    sizeMb = ParseSizeMb(totalStr);
                string mfr    = StrProp(item, "dimm_manufacturer") ?? "";
                if (sizeMb > 0)
                    modules.Add(MakeModule("Unified", sizeMb, dimmType, "Unified", 0, 0, mfr, ""));
            }
        }
#pragma warning restore IDISP004
        return BuildMemoryInfo(modules);
    }

    private static MemoryInfo DetectMemoryLinux()
    {
        // dmidecode may require elevated privileges; returns empty if unavailable
        var output = Run("dmidecode", "-t 17");
        if (string.IsNullOrWhiteSpace(output)) return NoMemoryInfo;

        var modules = new List<MemoryModule>();
        string? locator = null, type = null, mfr = null, part = null, ff = null;
        int sizeMb = 0, speed = 0, cfgSpeed = 0;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("Memory Device"))
            {
                if (sizeMb > 0)
                    modules.Add(MakeModule(locator, sizeMb, type, ff, speed, cfgSpeed, mfr, part));
                locator = type = mfr = part = ff = null;
                sizeMb = speed = cfgSpeed = 0;
            }
            else if (line.StartsWith("Size:"))
            {
                var p = line[5..].Trim().Split(' ');
                if (p.Length >= 2 && int.TryParse(p[0], out int n))
                    sizeMb = p[1].StartsWith("GB", StringComparison.OrdinalIgnoreCase) ? n * 1024 : n;
            }
            else if (line.StartsWith("Type:") && !line.StartsWith("Type Detail:"))
                type = line[5..].Trim();
            else if (line.StartsWith("Speed:"))
            {
                var p = line[6..].Trim().Split(' ');
                if (int.TryParse(p[0], out int s)) speed = s;
            }
            else if (line.StartsWith("Configured Memory Speed:"))
            {
                var p = line.Split(' ');
                if (p.Length >= 4 && int.TryParse(p[3], out int s)) cfgSpeed = s;
            }
            else if (line.StartsWith("Locator:") && !line.StartsWith("Bank Locator:"))
                locator = line[8..].Trim();
            else if (line.StartsWith("Manufacturer:"))  mfr  = line[13..].Trim();
            else if (line.StartsWith("Part Number:"))   part = line[12..].Trim();
            else if (line.StartsWith("Form Factor:"))   ff   = line[12..].Trim();
        }
        if (sizeMb > 0)
            modules.Add(MakeModule(locator, sizeMb, type, ff, speed, cfgSpeed, mfr, part));

        return BuildMemoryInfo(modules);
    }

    private static MemoryInfo DetectMemoryWindows()
    {
        // PowerShell WMI query: each module -> hashtable -> JSON array
        string script = """
$mems = @(Get-WmiObject Win32_PhysicalMemory | ForEach-Object {
    @{
        loc  = $_.DeviceLocator
        mb   = [int]($_.Capacity / 1MB)
        tid  = [int]$_.SMBIOSMemoryType
        spd  = [int]$_.Speed
        cspd = [int]$_.ConfiguredClockSpeed
        mfr  = [string]$_.Manufacturer
        part = [string]$_.PartNumber
    }
})
$mems | ConvertTo-Json -Depth 1
""";
        var json = RunPowerShell(script).Trim();
        if (string.IsNullOrEmpty(json)) return NoMemoryInfo;

        // PowerShell returns a bare object (not array) when there is only one module
        if (!json.StartsWith('[')) json = $"[{json}]";

        using var doc = JsonDocument.Parse(json);
        var modules = new List<MemoryModule>();
#pragma warning disable IDISP004 // JsonElement.ArrayEnumerator is disposed by foreach
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            int mb = IntProp(item, "mb");
            if (mb <= 0) continue;
            string type = MemTypeFromSmbiosId(IntProp(item, "tid"));
            int speed = IntProp(item, "spd");
            // JEDEC LPDDR5X starts at 6400 MT/s; some BIOS/firmware report
            // LPDDR5X modules with the LPDDR5 SMBIOS type code (0x23).
            if (type == "LPDDR5" && speed >= 6400)
                type = "LPDDR5X";
            modules.Add(MakeModule(
                StrProp(item, "loc"), mb, type, "DIMM",
                speed, IntProp(item, "cspd"),
                StrProp(item, "mfr"), StrProp(item, "part")));
        }
#pragma warning restore IDISP004
        return BuildMemoryInfo(modules);
    }

    // ── NPU Hardware Detection ────────────────────────────────────────────

    /// <summary>
    /// Probes for NPU (Neural Processing Unit) hardware on Windows via WMI.
    /// Returns a description string if found, or null if no NPU detected.
    /// Non-Windows platforms always return null.
    /// </summary>
    public static string? DetectNpuHardware()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            // Query PnP devices for known NPU identifiers
            string psScript =
                "$npus = Get-CimInstance Win32_PnPEntity | " +
                "Where-Object { " +
                "  $_.Name -match 'NPU|Neural|AI Accelerator|AI Boost' -or " +
                "  ($_.PNPClass -eq 'System' -and $_.Name -match 'NPU') " +
                "}; " +
                "if ($npus) { ($npus | Select-Object -ExpandProperty Name -Unique) -join '; ' } " +
                "else { '' }";

            string encoded = Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(psScript));

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15_000);

            if (!string.IsNullOrWhiteSpace(output))
            {
                // Filter well-known false positives that the WMI query may incorrectly match
                // (e.g. "Microsoft Input Configuration Device" on Snapdragon ARM64 devices)
                string[] knownFalsePositives =
                [
                    "Microsoft Input Configuration Device",
                ];
                var names = output.Split(';')
                    .Select(n => n.Trim())
                    .Where(n => n.Length > 0
                        && !knownFalsePositives.Any(fp =>
                            n.Equals(fp, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (names.Length > 0)
                    return string.Join("; ", names);
            }
        }
        catch (Exception ex)
        {
            TraceLog.SystemInfoDetectionWarning("NPU", ex.Message);
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsOSX   => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Safely runs a detection function; returns the default value on any exception
    /// and logs a warning via ETW.
    /// </summary>
    private static T SafeDetect<T>(string component, Func<T> detect, T defaultValue)
    {
        try
        {
            return detect();
        }
        catch (Exception ex)
        {
            TraceLog.SystemInfoDetectionWarning(component, ex.Message);
            return defaultValue;
        }
    }

    private static MemoryInfo BuildMemoryInfo(List<MemoryModule> modules)
    {
        if (modules.Count == 0) return NoMemoryInfo;
        string type  = modules[0].Type;
        int    speed = modules.Max(m => m.SpeedMts);
        return new MemoryInfo(type, speed, speed, modules.Count, modules.Count, modules, true);
    }

    private static MemoryModule MakeModule(
        string? locator, int sizeMb, string? type, string? ff,
        int speed, int cfgSpeed, string? mfr, string? part) =>
        new(locator?.Trim() ?? "", sizeMb, type ?? "", ff ?? "",
            speed, cfgSpeed, 64, 64, 1,
            mfr?.Trim() ?? "", part?.Trim() ?? "");

    private static int SysctlKb(string key) =>
        long.TryParse(Run("sysctl", $"-n {key}").Trim(), out long b) ? (int)(b / 1024) : 0;

    private static string? TryReadFile(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    private static string? StrProp(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int IntProp(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.TryGetInt32(out int n) ? n : 0;

    private static int ParseSizeMb(string s)
    {
        var p = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 2 && double.TryParse(p[0], out double n))
            return p[1].StartsWith("GB", StringComparison.OrdinalIgnoreCase) ? (int)(n * 1024) : (int)n;
        return 0;
    }

    private static int ParseSpeedMts(string s)
    {
        var p = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length > 0 && int.TryParse(p[0], out int n) ? n : 0;
    }

    private static string MemTypeFromSmbiosId(int id) => id switch
    {
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x25 => "LPDDR5X",
        _    => "Unknown"
    };

    private static string Run(string cmd, string args, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return output;
        }
        catch { return ""; }
    }

    // Uses -EncodedCommand (base64 UTF-16LE) to avoid shell escaping issues
    private static string RunPowerShell(string script, int timeoutMs = 8000)
    {
        byte[] bytes   = System.Text.Encoding.Unicode.GetBytes(script);
        string encoded = Convert.ToBase64String(bytes);
        return Run("powershell", $"-NoProfile -NonInteractive -EncodedCommand {encoded}", timeoutMs);
    }
}
