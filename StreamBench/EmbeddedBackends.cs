// EmbeddedBackends.cs
// Extracts the C benchmark backend executables that are embedded as .NET resources.
// Binaries are cached in a per-version temp directory so extraction only happens once.
// This allows StreamBench to ship as a single self-contained executable.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace StreamBench;

public static class EmbeddedBackends
{
    // Cache directory: %TEMP%/StreamBench/<version> or /tmp/StreamBench/<version>
    private static readonly string CacheDir = Path.Combine(
        Path.GetTempPath(),
        "StreamBench",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev");

    /// <summary>
    /// Extracts the appropriate embedded C backend for the current OS/architecture.
    /// Returns the path to the extracted executable, or null if not found.
    /// </summary>
    public static string? ExtractBackend(bool isGpu)
    {
        string prefix = isGpu ? "stream_gpu" : "stream_cpu";
        string os = GetOsTag();
        string arch = GetArchTag();
        string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        string fileName = $"{prefix}_{os}_{arch}{ext}";

        TraceLog.BackendExtracting(fileName);

        // Resource names use dots instead of path separators, and are prefixed
        // with the default namespace.  We embedded them under backends/ folder,
        // so the resource name is: StreamBench.backends.<filename-with-dots-escaped>
        // .NET replaces hyphens in folder/file with underscores for resource names,
        // so we search by the suffix.
        var assembly = Assembly.GetExecutingAssembly();
        string? resourceName = FindResourceName(assembly, fileName);

        if (resourceName is null)
        {
            // Log all available resource names for diagnostics
            var availableNames = assembly.GetManifestResourceNames();
            string triedInfo = $"Wanted: {fileName}; Available resources: [{string.Join(", ", availableNames)}]";
            TraceLog.BackendNotFound(triedInfo);
            return null;
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogException(ex);
            return null;
        }

        string targetPath = Path.Combine(CacheDir, fileName);

        // If already extracted and hash matches, skip extraction
        if (File.Exists(targetPath) && IsUpToDate(assembly, resourceName, targetPath))
        {
            TraceLog.BackendCacheHit(targetPath);
            EnsureExecutable(targetPath);

            // On macOS, ensure bundled libomp.dylib is also present (needed by CPU backends)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !isGpu)
            {
                ExtractSupportLibrary(assembly, "libomp.dylib", CacheDir);
            }

            return targetPath;
        }

        // Extract resource to cache directory
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                DiagnosticHelper.LogError($"Resource stream is null for: {resourceName}");
                return null;
            }

            using var fs = File.Create(targetPath);
            stream.CopyTo(fs);
            fs.Close();

            EnsureExecutable(targetPath);
            TraceLog.BackendExtracted(targetPath);

            // On macOS, also extract bundled libomp.dylib if present (needed by CPU backends)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !isGpu)
            {
                ExtractSupportLibrary(assembly, "libomp.dylib", CacheDir);
            }

            return targetPath;
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogException(ex);
            return null;
        }
    }

    /// <summary>
    /// Returns true if embedded backends are available for the current platform.
    /// </summary>
    public static bool HasEmbeddedBackends()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string os = GetOsTag();
        string arch = GetArchTag();
        string cpuName = $"stream_cpu_{os}_{arch}";
        return FindResourceName(assembly, cpuName) is not null
            || FindResourceName(assembly, cpuName + ".exe") is not null;
    }

    /// <summary>
    /// Extracts a support library (e.g., libomp.dylib) from embedded resources to the given directory.
    /// </summary>
    private static void ExtractSupportLibrary(Assembly assembly, string fileName, string targetDir)
    {
        string? resName = FindResourceName(assembly, fileName);
        if (resName is null) return;

        string targetPath = Path.Combine(targetDir, fileName);
        if (File.Exists(targetPath) && IsUpToDate(assembly, resName, targetPath)) return;

        try
        {
            using var stream = assembly.GetManifestResourceStream(resName);
            if (stream is null) return;
            using var fs = File.Create(targetPath);
            stream.CopyTo(fs);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Failed to extract {fileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the embedded resource name that ends with the given file name.
    /// Resource names in .NET use dots as separators and may mangle special chars.
    /// </summary>
    private static string? FindResourceName(Assembly assembly, string fileName)
    {
        // Exact suffix match (resource names are like "StreamBench.backends.stream_cpu_win_x64.exe")
        string suffix = $".{fileName}";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // Also try with underscores replacing hyphens (MSBuild resource naming)
        string altSuffix = $".{fileName.Replace('-', '_')}";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(altSuffix, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    /// <summary>
    /// Quick check: compare file size to avoid re-extracting every time.
    /// </summary>
    private static bool IsUpToDate(Assembly assembly, string resourceName, string filePath)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return false;
            return new FileInfo(filePath).Length == stream.Length;
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Cache check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// On Unix, set the executable permission bit.
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                DiagnosticHelper.LogWarning($"chmod failed for {path}: {ex.Message}");
            }
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
}
