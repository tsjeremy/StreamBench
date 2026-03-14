// ResultSaver.cs
// Saves benchmark results to JSON and CSV files.
// JSON: pretty-printed, same schema as C backend output.
// CSV: one row per kernel, compatible with the original C CSV format.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamBench.Models;

namespace StreamBench;

public static class ResultSaver
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── JSON save ─────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the full benchmark result as a pretty-printed JSON file.
    /// Returns the path written, or null on failure.
    /// </summary>
    public static string? SaveJson(BenchmarkResult result, string? outputDir = null)
    {
        string filename = BuildFilename(result, "json", outputDir);
        try
        {
            string json = JsonSerializer.Serialize(result, PrettyJson);
            File.WriteAllText(filename, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            TraceLog.FileSaved(filename);
            return filename;
        }
        catch (Exception ex)
        {
            TraceLog.FileSaveFailed(filename, ex.Message);
            DiagnosticHelper.LogException(ex);
            Console.Error.WriteLine($"Warning: Could not save JSON: {ex.Message}");
            Console.Error.WriteLine($"  Path: {filename}");
            return null;
        }
    }

    // ── CSV save ──────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the benchmark results as a CSV file (one row per kernel).
    /// Returns the path written, or null on failure.
    /// </summary>
    public static string? SaveCsv(BenchmarkResult result, string? outputDir = null)
    {
        string filename = BuildFilename(result, "csv", outputDir);
        try
        {
            using var sw = new StreamWriter(filename, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            WriteCsvHeader(sw);
            WriteCsvRows(sw, result);
            TraceLog.FileSaved(filename);
            return filename;
        }
        catch (Exception ex)
        {
            TraceLog.FileSaveFailed(filename, ex.Message);
            DiagnosticHelper.LogException(ex);
            Console.Error.WriteLine($"Warning: Could not save CSV: {ex.Message}");
            Console.Error.WriteLine($"  Path: {filename}");
            return null;
        }
    }

    // ── Consolidated range CSV ────────────────────────────────────────────

    /// <summary>
    /// Opens a consolidated CSV file for range testing (write header once).
    /// Caller should dispose the writer when done.
    /// </summary>
    public static StreamWriter? OpenRangeCsv(string filePath)
    {
        try
        {
            var sw = new StreamWriter(filePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            WriteCsvHeader(sw);
            sw.Flush();
            return sw;
        }
        catch (Exception ex)
        {
            TraceLog.FileSaveFailed(filePath, ex.Message);
            DiagnosticHelper.LogException(ex);
            Console.Error.WriteLine($"Warning: Could not create range CSV: {ex.Message}");
            Console.Error.WriteLine($"  Path: {filePath}");
            return null;
        }
    }

    /// <summary>Appends one benchmark result's rows to an open range CSV.</summary>
    public static void AppendRangeCsv(StreamWriter sw, BenchmarkResult result)
    {
        WriteCsvRows(sw, result);
        sw.Flush();
    }

    // ── AI benchmark JSON save ────────────────────────────────────────────

    /// <summary>
    /// Saves all AI inference benchmark results to a single JSON file.
    /// When available, embeds the relation summary so Q1/Q2/Q3 live together.
    /// Returns the path written, or null on failure.
    /// </summary>
    public static string? SaveAiJson(
        AiBenchmarkTwoPassResult twoPassResult,
        AiLocalRelationSummaryResult? relationSummary = null,
        string? outputDir = null)
    {
        var allResults = twoPassResult.SharedResults
            .Concat(twoPassResult.BestPerDeviceResults)
            .ToList();
        if (allResults.Count == 0) return null;

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string name = $"ai_inference_benchmark_{timestamp}.json";
        string filename = outputDir is not null ? Path.Combine(outputDir, name) : name;

        try
        {
            var root = JsonSerializer.SerializeToNode(twoPassResult, PrettyJson) as JsonObject
                ?? new JsonObject();
            if (relationSummary is not null)
                root["ai_relation_summary"] = JsonSerializer.SerializeToNode(relationSummary, PrettyJson);

            string json = root.ToJsonString(PrettyJson);
            File.WriteAllText(filename, json,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            TraceLog.FileSaved(filename);
            return filename;
        }
        catch (Exception ex)
        {
            TraceLog.FileSaveFailed(filename, ex.Message);
            DiagnosticHelper.LogException(ex);
            Console.Error.WriteLine($"Warning: Could not save AI benchmark JSON: {ex.Message}");
            Console.Error.WriteLine($"  Path: {filename}");
            return null;
        }
    }

    /// <summary>
    /// Saves the local-AI relation summary (3-question output) as JSON.
    /// Returns the path written, or null on failure.
    /// </summary>
    public static string? SaveAiRelationSummaryJson(
        StreamBench.Models.AiLocalRelationSummaryResult summary,
        string? outputDir = null)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string modelTag = SanitizeFilenamePart(summary.ModelAlias);
        string name = $"ai_relation_summary_{modelTag}_{timestamp}.json";
        string filename = outputDir is not null ? Path.Combine(outputDir, name) : name;

        try
        {
            string json = JsonSerializer.Serialize(summary, PrettyJson);
            File.WriteAllText(filename, json,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            TraceLog.FileSaved(filename);
            return filename;
        }
        catch (Exception ex)
        {
            TraceLog.FileSaveFailed(filename, ex.Message);
            DiagnosticHelper.LogException(ex);
            Console.Error.WriteLine($"Warning: Could not save AI relation summary JSON: {ex.Message}");
            Console.Error.WriteLine($"  Path: {filename}");
            return null;
        }
    }

    /// <summary>
    /// Embeds AI benchmark (Q1/Q2) and relation summary (Q3) into memory JSON files
    /// such as stream_cpu_results_*.json / stream_gpu_results_*.json.
    /// Returns the list of files updated.
    /// </summary>
    public static IReadOnlyList<string> MergeAiIntoMemoryJson(
        IEnumerable<string>? memoryJsonPaths,
        AiBenchmarkTwoPassResult twoPassResult,
        AiLocalRelationSummaryResult? relationSummary,
        string? outputDir = null)
    {
        var candidates = (memoryJsonPaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        if (candidates.Count == 0)
        {
            string dir = Path.GetFullPath(outputDir ?? ".");
            if (!Directory.Exists(dir))
                return [];

            candidates = Directory.EnumerateFiles(dir, "stream_*_results_*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (candidates.Count == 0)
            return [];

        var updated = new List<string>();
        foreach (var path in candidates)
        {
            try
            {
                JsonObject? root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root is null)
                    continue;

                root["ai_inference_benchmark"] = JsonSerializer.SerializeToNode(twoPassResult, PrettyJson);
                if (relationSummary is not null)
                    root["ai_relation_summary"] = JsonSerializer.SerializeToNode(relationSummary, PrettyJson);
                else
                    root.Remove("ai_relation_summary");

                File.WriteAllText(path, root.ToJsonString(PrettyJson),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                TraceLog.FileSaved(path);
                updated.Add(path);
            }
            catch (Exception ex)
            {
                TraceLog.FileSaveFailed(path, ex.Message);
                DiagnosticHelper.LogException(ex);
            }
        }

        return updated;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static string SanitizeFilenamePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown-model";

        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var cleaned = new string(chars).Trim('-');
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(cleaned))
            return "unknown-model";

        return cleaned.Length > 48 ? cleaned[..48] : cleaned;
    }

    private static void WriteCsvHeader(StreamWriter sw)
    {
        sw.WriteLine("Array_Size_Elements,Array_Size_MiB,Total_Memory_GiB," +
                     "Function,Best_Rate_MBps,Avg_Time_sec,Min_Time_sec,Max_Time_sec");
    }

    private static void WriteCsvRows(StreamWriter sw, BenchmarkResult result)
    {
        var cfg = result.Config;
        double totalGib = cfg.TotalMemoryMib / 1024.0;

        var kernels = new[]
        {
            ("Copy",  result.Results.Copy),
            ("Scale", result.Results.Scale),
            ("Add",   result.Results.Add),
            ("Triad", result.Results.Triad),
        };

        foreach (var (name, k) in kernels)
        {
            sw.WriteLine(
                $"{cfg.ArraySizeElements},{cfg.ArraySizeMib:F1},{totalGib:F3}," +
                $"{name},{k.BestRateMbps:F1},{k.AvgTimeSec:F6},{k.MinTimeSec:F6},{k.MaxTimeSec:F6}");
        }
    }

    private static string BuildFilename(BenchmarkResult result, string ext, string? outputDir)
    {
        string type = result.Type.ToLowerInvariant();
        string deviceTag = "";
        if (type == "gpu" && result.Device is not null)
        {
            string? npuName = GpuDeviceInfo.InferNpuDisplayName(
                result.Device.Name,
                result.Device.Vendor,
                result.System?.CpuModel);
            if (!string.IsNullOrWhiteSpace(npuName))
                type = "npu";

            // Include a sanitized device name tag so multi-GPU results don't overwrite each other
            string rawName = npuName
                ?? GpuDeviceInfo.InferGpuDisplayName(result.Device.Name, result.Device.Vendor)
                ?? result.Device.Name;
            deviceTag = "_" + SanitizeFilenamePart(rawName);
        }

        long   sizeMels = result.Config.ArraySizeElements / 1_000_000;
        string name     = $"stream_{type}{deviceTag}_results_{sizeMels}M.{ext}";
        return outputDir is not null ? Path.Combine(outputDir, name) : name;
    }
}
