// BenchmarkRunner.cs
// Locates the appropriate C backend executable and runs it as a subprocess,
// reading the JSON result from stdout.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
                    return path;
            }

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

        using var process = new Process { StartInfo = psi };

        // Forward only error/warning messages from stderr; suppress informational noise
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // Only show lines that indicate errors, warnings, or important notes
            var line = e.Data;
            if (line.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                || line.Contains("NOTE:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(line);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        // Run C backend and detect hardware info in parallel
        var jsonTask   = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var detectTask = SystemInfoDetector.DetectAsync();

        await Task.WhenAll(jsonTask, detectTask);
        await process.WaitForExitAsync(cancellationToken);

        var jsonText = await jsonTask;
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        BenchmarkResult? cResult;
        try
        {
            cResult = JsonSerializer.Deserialize<BenchmarkResult>(jsonText, JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Failed to parse benchmark JSON: {ex.Message}");
            Console.Error.WriteLine(jsonText[..Math.Min(500, jsonText.Length)]);
            return null;
        }

        if (cResult is null) return null;

        // Merge .NET-detected hardware info into the C benchmark result
        var (sys, mem, cache) = await detectTask;
        return cResult with { System = sys, Memory = mem, Cache = cache };
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
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// Represents a GPU/NPU device discovered via --list-gpus.
/// </summary>
public record GpuDeviceInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public int ComputeUnits { get; init; }
    public int MaxFrequencyMhz { get; init; }
    public long GlobalMemoryBytes { get; init; }

    /// <summary>
    /// Returns "NPU" if the device appears to be a Neural Processing Unit, otherwise "GPU".
    /// </summary>
    public string DeviceKind => IsNpu(Name, Vendor) ? "NPU" : "GPU";

    /// <summary>
    /// Detects whether a device is an NPU rather than a GPU.
    /// Matches by:
    ///   - Device name keywords: "AI Boost", "NPU", "Neural", "Hexagon", "VPU".
    ///   - Vendor "Microsoft" (on Qualcomm Snapdragon X the NPU is exposed via
    ///     OpenCL with vendor "Microsoft", while the real Adreno GPU has vendor "Qualcomm").
    ///     Excludes the Microsoft Basic Render Driver (WARP) software rasterizer.
    /// </summary>
    public static bool IsNpu(string? deviceName, string? vendor = null)
    {
        if (string.IsNullOrEmpty(deviceName)) return false;
        var n = deviceName.ToUpperInvariant();

        // Name-based detection: explicit NPU keywords always win
        if (n.Contains("AI BOOST")
            || n.Contains("NPU")
            || n.Contains("NEURAL")
            || n.Contains("HEXAGON")
            || n.Contains("VPU"))
            return true;

        // If the device name explicitly says "GPU", treat it as a GPU
        // even if the vendor is Microsoft (e.g. Qualcomm Adreno GPU
        // exposed via Microsoft OpenCL driver on Snapdragon X2).
        if (n.Contains("GPU"))
            return false;

        // Vendor-based detection: "Microsoft" vendor on OpenCL typically
        // indicates an NPU driver, not a real GPU — except for WARP.
        if (!string.IsNullOrEmpty(vendor)
            && vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("BASIC RENDER"))
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

        // Strategy 1: Ask the OS for the real NPU device name (Windows only)
        string? osName = DetectNpuFromOs();
        if (osName is not null)
            return osName;

        // Strategy 2: Transform the OpenCL device name
        if (!string.IsNullOrEmpty(deviceName))
        {
            string cleaned = deviceName
                .Replace("GPU", "NPU", StringComparison.OrdinalIgnoreCase)
                .Trim();
            // If nothing changed (no "GPU" was in the name), just append " (NPU)"
            if (cleaned.Equals(deviceName.Trim(), StringComparison.Ordinal))
                cleaned += " (NPU)";
            return cleaned;
        }

        return "NPU";
    }

    // Cache the OS-detected NPU name (null = not queried, "" = queried but not found)
    private static string? _cachedOsNpuName;
    private static bool _osNpuQueried;
    private static readonly object _osNpuLock = new();

    /// <summary>
    /// Queries the OS for registered NPU devices.
    /// On Windows, uses "pnputil /enum-devices /connected" and looks for device
    /// descriptions containing "NPU", "Neural", or "AI Boost".
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

        try
        {
            var psi = new ProcessStartInfo("pnputil", "/enum-devices /connected")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute  = false,
                CreateNoWindow   = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            // Parse "Device Description:" lines for NPU keywords
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("Device Description:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string desc = line["Device Description:".Length..].Trim();
                var d = desc.ToUpperInvariant();
                if (d.Contains("NPU") || d.Contains("NEURAL") || d.Contains("AI BOOST"))
                {
                    lock (_osNpuLock) { _cachedOsNpuName = desc; }
                    return desc;
                }
            }
        }
        catch { /* pnputil not available or failed — fall through */ }

        return null;
    }
}
