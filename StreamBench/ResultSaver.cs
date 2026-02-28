// ResultSaver.cs
// Saves benchmark results to JSON and CSV files.
// JSON: pretty-printed, same schema as C backend output.
// CSV: one row per kernel, compatible with the original C CSV format.

using System.Text;
using System.Text.Json;
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
            return filename;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not save JSON: {ex.Message}");
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
            return filename;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not save CSV: {ex.Message}");
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
            Console.Error.WriteLine($"Warning: Could not create range CSV: {ex.Message}");
            return null;
        }
    }

    /// <summary>Appends one benchmark result's rows to an open range CSV.</summary>
    public static void AppendRangeCsv(StreamWriter sw, BenchmarkResult result)
    {
        WriteCsvRows(sw, result);
        sw.Flush();
    }

    // ── Private helpers ───────────────────────────────────────────────────

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
        string type     = result.Type.ToLower();
        long   sizeMels = result.Config.ArraySizeElements / 1_000_000;
        string name     = $"stream_{type}_results_{sizeMels}M.{ext}";
        return outputDir is not null ? Path.Combine(outputDir, name) : name;
    }
}
