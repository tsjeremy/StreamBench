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

        // Forward stderr to our stderr in real time
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Console.Error.WriteLine(e.Data);
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
/// Represents a GPU device discovered via --list-gpus.
/// </summary>
public record GpuDeviceInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public int ComputeUnits { get; init; }
    public int MaxFrequencyMhz { get; init; }
    public long GlobalMemoryBytes { get; init; }
}
