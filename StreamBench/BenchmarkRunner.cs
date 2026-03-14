// BenchmarkRunner.cs
// Locates the appropriate C backend executable and runs it as a subprocess,
// reading the JSON result from stdout.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using StreamBench.Models;

namespace StreamBench;

public static class BenchmarkRunner
{
    // JSON deserialization options: case-insensitive, lenient
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Finds the C backend executable next to this assembly or in the project root.
    /// Falls back to extracting an embedded backend if no external binary is found.
    /// Naming convention: stream_cpu_<os>_<arch>[.exe] or stream_gpu_<os>_<arch>[.exe]
    /// </summary>
    public static string? FindExecutable(bool isGpu)
    {
        string prefix = isGpu ? "stream_gpu" : "stream_cpu";
        string os = GetOsTag();
        string arch = GetArchTag();
        string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";

        // Candidate names in priority order
        string[] candidates =
        [
            $"{prefix}_{os}_{arch}{ext}",
            $"{prefix}_{os}{ext}",
            $"{prefix}{ext}",
            // Windows compiled names
            $"{prefix}_win_{arch}{ext}",
            $"{prefix}_win_x64{ext}",
        ];

        // Search directories: next to binary, then project root (dev scenario)
        string[] searchDirs =
        [
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        ];

        foreach (var dir in searchDirs)
            foreach (var name in candidates)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    TraceLog.DiagnosticInfo($"Found backend: {path}");
                    return path;
                }
            }

        TraceLog.DiagnosticInfo(
            $"No external backend found for {prefix}_{os}_{arch}; trying embedded extraction");

        // No external binary found — try extracting from embedded resources
        return EmbeddedBackends.ExtractBackend(isGpu);
    }

    /// <summary>
    /// Runs the C backend with optional --array-size and --gpu-device arguments.
    /// Progress messages from the backend (stderr) are forwarded to the console.
    /// System, memory, and cache info are detected by .NET and merged into the result.
    /// </summary>
    public static async Task<BenchmarkResult?> RunAsync(
        string executablePath,
        long? arraySize = null,
        int? gpuDeviceIndex = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>();
        if (arraySize.HasValue)
            args.AddRange(["--array-size", arraySize.Value.ToString()]);
        if (gpuDeviceIndex.HasValue)
            args.AddRange(["--gpu-device", gpuDeviceIndex.Value.ToString()]);

        string benchType = executablePath.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "GPU" : "CPU";
        TraceLog.BenchmarkStarted(benchType, executablePath, arraySize);
        var benchSw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        Process process;
        try
        {
            process = new Process { StartInfo = psi };

            // Forward only error/warning messages from stderr; suppress informational noise
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                // Only show lines that indicate errors, warnings, or important notes
                var line = e.Data.Trim();
                // "Solution Validates: avg error less than..." is a success message
                // that contains "error" as a substring — exclude it from forwarding.
                if (line.StartsWith("Solution Validates", StringComparison.OrdinalIgnoreCase))
                    return;
                if (line.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("NOTE:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    // Spinner redraws on the same console row; clear that row so backend
                    // notes/warnings appear as clean standalone lines.
                    Console.Error.Write("\r" + new string(' ', 180) + "\r");
                    Console.Error.WriteLine(line);
                }
            };

            process.Start();
            TraceLog.BackendProcessStarted(executablePath, process.Id);
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            benchSw.Stop();
            var diag = DiagnosticHelper.LogException(ex);
            TraceLog.BenchmarkError(benchType, ex.Message, "BenchmarkRunner.cs", 0);
            Console.Error.WriteLine($"Failed to start backend process: {executablePath}");
            Console.Error.WriteLine($"  {diag}");
            return null;
        }

        try
        {
            // Run C backend and detect hardware info in parallel
            var jsonTask   = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var detectTask = SystemInfoDetector.DetectAsync();

            await Task.WhenAll(jsonTask, detectTask);
            await process.WaitForExitAsync(cancellationToken);

            benchSw.Stop();

            if (process.ExitCode != 0)
            {
                TraceLog.BackendProcessExitedWithError(process.ExitCode, executablePath);
            }

            var jsonText = await jsonTask;
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                DiagnosticHelper.LogError($"Backend produced no output (exit code: {process.ExitCode})");
                return null;
            }

            BenchmarkResult? cResult;
            try
            {
                cResult = JsonSerializer.Deserialize<BenchmarkResult>(jsonText, JsonOptions);
            }
            catch (JsonException ex)
            {
                string preview = jsonText[..Math.Min(500, jsonText.Length)];
                TraceLog.JsonParseFailed(ex.Message, preview);
                DiagnosticHelper.LogException(ex);
                Console.Error.WriteLine($"Failed to parse benchmark JSON: {ex.Message}");
                Console.Error.WriteLine(preview);
                return null;
            }

            if (cResult is null)
            {
                DiagnosticHelper.LogError("JSON deserialized to null");
                return null;
            }

            // Merge .NET-detected hardware info into the C benchmark result
            var (sys, mem, cache) = await detectTask;
            TraceLog.BenchmarkCompleted(benchType, benchSw.ElapsedMilliseconds);
            return cResult with { System = sys, Memory = mem, Cache = cache };
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogException(ex);
            TraceLog.BenchmarkError(benchType, ex.Message, "BenchmarkRunner.cs", 0);
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string GetOsTag() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"   :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "macos" : "linux";

    private static string GetArchTag() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64   => "x64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLower()
        };

    /// <summary>
    /// Calls the GPU backend with --list-gpus to discover all available GPU devices.
    /// Returns a list of GpuDeviceInfo, or empty if the backend is unavailable.
    /// </summary>
    public static async Task<List<GpuDeviceInfo>> ListGpusAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--list-gpus",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.ErrorDataReceived += (_, _) => { }; // discard stderr
            process.Start();
            process.BeginErrorReadLine();

            var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
                return [];

            return JsonSerializer.Deserialize<List<GpuDeviceInfo>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"GPU discovery failed: {ex.Message}");
            return [];
        }
    }
}

/// <summary>
/// Represents a GPU device discovered via --list-gpus.
/// NPU detection is retained for AI benchmark device classification
/// but NPU devices are excluded from memory bandwidth benchmarks.
/// </summary>
public record GpuDeviceInfo
{
    [JsonPropertyName("index")]
    public int Index { get; init; }
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("vendor")]
    public string Vendor { get; init; } = "";
    [JsonPropertyName("compute_units")]
    public int ComputeUnits { get; init; }
    [JsonPropertyName("max_frequency_mhz")]
    public int MaxFrequencyMhz { get; init; }
    [JsonPropertyName("global_memory_bytes")]
    public long GlobalMemoryBytes { get; init; }

    /// <summary>
    /// Returns "NPU" if the device appears to be a Neural Processing Unit, otherwise "GPU".
    /// </summary>
    public string DeviceKind => IsNpu(Name, Vendor) ? "NPU" : "GPU";

    /// <summary>
    /// Detects whether a device is an NPU rather than a GPU.
    /// Matches by:
    ///   - Device name keywords: "AI Boost", "NPU", "Neural", "Hexagon", "VPU".
    ///   - OS-level detection via pnputil class GUID for Neural Processors:
    ///     if the OS has a registered NPU and the OpenCL vendor is "Microsoft",
    ///     the device is the NPU (some NPUs are exposed via OpenCL
    ///     with vendor "Microsoft").
    /// </summary>
    public static bool IsNpu(string? deviceName, string? vendor = null)
    {
        if (string.IsNullOrEmpty(deviceName)) return false;
        var n = deviceName.ToUpperInvariant();

        // Explicit NPU signatures from the device name.
        if (n.Contains("NPU")
            || n.Contains("NEURAL")
            || n.Contains("HEXAGON")
            || n.Contains("VPU")
            || n.Contains("AI BOOST"))
            return true;

        // Strategy 1 — OS-level class GUID (highest priority, ground truth).
        // If Windows has a registered NPU device (ComputeAccelerator or Neural
        // Processors class) and this OpenCL device has vendor "Microsoft" (not
        // the WARP software rasterizer) and isn't explicitly a GPU, it's the NPU.
        if (!string.IsNullOrEmpty(vendor)
            && vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("BASIC RENDER")
            && !n.Contains("GPU")
            && DetectNpuFromOs() is not null)
            return true;

        return false;
    }

    /// <summary>
    /// Derives the correct NPU display name without hardcoded naming rules.
    /// 1. On Windows, queries pnputil for the OS-registered NPU device name (ground truth).
    /// 2. Falls back to transforming the OpenCL device name (replaces "GPU" → "NPU").
    /// Returns null if the device is not an NPU.
    /// </summary>
    public static string? InferNpuDisplayName(string? deviceName, string? vendor = null, string? cpuModel = null)
    {
        if (!IsNpu(deviceName, vendor))
            return null;

        // Ask the OS for the real NPU device name (Windows only)
        string? osName = DetectNpuFromOs();
        if (osName is not null)
            return osName;

        // No name-derived fallback: classification/display should be based on OS class-guid evidence only.
        return null;
    }

    /// <summary>
    /// Returns a user-friendly GPU display name by querying the OS for driver-reported names.
    /// On Windows, uses Win32_VideoController to find a matching GPU name by vendor keyword.
    /// Returns null if the device name is already user-friendly or no match is found.
    /// </summary>
    public static string? InferGpuDisplayName(string? deviceName, string? vendor = null)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;
        // If the name already looks user-friendly (contains spaces, brand keywords), skip
        if (deviceName.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Arc ", StringComparison.OrdinalIgnoreCase))
            return null;

        // Only attempt OS lookup for codename-style names (e.g. "gfx1150", "GA106")
        var osNames = DetectGpuNamesFromOs();
        if (osNames is null || osNames.Count == 0) return null;

        // Match by vendor substring (e.g. "AMD" in vendor matches "AMD Radeon..." in OS name)
        if (!string.IsNullOrEmpty(vendor))
        {
            // Try vendor keyword matching
            string vendorUpper = vendor.ToUpperInvariant();
            foreach (var osName in osNames)
            {
                string osUpper = osName.ToUpperInvariant();
                if ((vendorUpper.Contains("AMD") || vendorUpper.Contains("ADVANCED MICRO"))
                    && (osUpper.Contains("RADEON") || osUpper.Contains("AMD")))
                    return osName;
                if (vendorUpper.Contains("NVIDIA") && osUpper.Contains("NVIDIA"))
                    return osName;
                if (vendorUpper.Contains("INTEL") && (osUpper.Contains("INTEL") || osUpper.Contains("ARC")))
                    return osName;
            }
        }

        return null;
    }

    // Cache the OS-detected GPU names
    private static List<string>? _cachedOsGpuNames;
    private static bool _osGpuQueried;
    private static readonly object _osGpuLock = new();

    /// <summary>
    /// Queries Win32_VideoController for all GPU display adapter names reported by the OS.
    /// </summary>
    private static List<string>? DetectGpuNamesFromOs()
    {
        lock (_osGpuLock)
        {
            if (_osGpuQueried)
                return _cachedOsGpuNames;
            _osGpuQueried = true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(
                "(Get-WmiObject Win32_VideoController).Name");
            string encoded = Convert.ToBase64String(bytes);
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);

            var names = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrEmpty(n)
                    && !n.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                .ToList();

            lock (_osGpuLock) { _cachedOsGpuNames = names; }
            return names;
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"GPU name detection via WMI failed: {ex.Message}");
            return null;
        }
    }

    // Cache the OS-detected NPU name (null = not queried, "" = queried but not found)
    private static string? _cachedOsNpuName;
    private static bool _osNpuQueried;
    private static readonly object _osNpuLock = new();

    // Windows class GUIDs that may host NPU devices
    private static readonly string[] NpuClassGuids =
    [
        "{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}",   // ComputeAccelerator class
        "{d3540260-d950-4922-a562-3aafcab6e49a}",    // Neural Processors class
    ];

    /// <summary>
    /// Queries the OS for registered NPU devices using known NPU class GUIDs.
    /// On Windows, uses "pnputil /enum-devices /connected /class {GUID}" to directly
    /// enumerate only NPU-class devices — no keyword matching needed.
    /// Checks ComputeAccelerator and Neural Processors classes.
    /// Returns the first matching device description, or null if none found.
    /// </summary>
    private static string? DetectNpuFromOs()
    {
        lock (_osNpuLock)
        {
            if (_osNpuQueried)
                return _cachedOsNpuName;
            _osNpuQueried = true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        foreach (var guid in NpuClassGuids)
        {
            try
            {
                var psi = new ProcessStartInfo("pnputil",
                    $"/enum-devices /connected /class \"{guid}\" /format csv")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute  = false,
                    CreateNoWindow   = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                string output = p.StandardOutput.ReadToEnd();
                _ = p.StandardError.ReadToEnd(); // drain stderr to avoid deadlocks on noisy systems
                p.WaitForExit(5000);

                // Parse machine-readable CSV output first.
                // Headers are expected to include DeviceDescription.
                var lines = output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .ToList();

                if (lines.Count >= 2)
                {
                    var header = ParseCsvLine(lines[0]);
                    int descIndex = Array.FindIndex(header,
                        h => h.Equals("DeviceDescription", StringComparison.OrdinalIgnoreCase));

                    if (descIndex >= 0)
                    {
                        for (int i = 1; i < lines.Count; i++)
                        {
                            var row = ParseCsvLine(lines[i]);
                            if (descIndex >= row.Length) continue;
                            string desc = row[descIndex].Trim();
                            if (string.IsNullOrEmpty(desc)) continue;

                            lock (_osNpuLock) { _cachedOsNpuName = desc; }
                            return desc;
                        }
                    }
                }

                // Fallback for older pnputil variants without /format csv support.
                foreach (var rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("Device Description:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string desc = line["Device Description:".Length..].Trim();
                    if (!string.IsNullOrEmpty(desc))
                    {
                        lock (_osNpuLock) { _cachedOsNpuName = desc; }
                        return desc;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticHelper.LogWarning($"NPU detection via pnputil GUID {guid} failed: {ex.Message}");
            }
        }

        return null;
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                values.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        values.Add(sb.ToString());
        return values.ToArray();
    }
}
