#if ENABLE_AI
// AiBackendConfig.cs
// Persisted configuration for AI backend selection. Saved to
// streambench_ai_config.json next to the executable or in the output directory.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamBench;

/// <summary>
/// Which AI backend to use for inference benchmarking.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum AiBackendType
{
    /// <summary>Auto-detect: try Foundry first (Windows/macOS), then Ollama (Linux), then LM Studio.</summary>
    Auto,
    /// <summary>Microsoft Foundry Local (Windows-only, supports CPU/GPU/NPU).</summary>
    Foundry,
    /// <summary>LM Studio (cross-platform, supports CPU/GPU).</summary>
    LmStudio,
    /// <summary>Ollama (cross-platform, supports CPU/GPU).</summary>
    Ollama,
}

/// <summary>
/// Serializable configuration for AI backend preferences.
/// Persisted to <c>streambench_ai_config.json</c>.
/// </summary>
internal sealed record AiBackendConfig
{
    [JsonPropertyName("backend")]
    public AiBackendType Backend { get; init; } = AiBackendType.Auto;

    [JsonPropertyName("foundry_endpoint")]
    public string? FoundryEndpoint { get; init; }

    [JsonPropertyName("lmstudio_endpoint")]
    public string? LmStudioEndpoint { get; init; }

    [JsonPropertyName("ollama_endpoint")]
    public string? OllamaEndpoint { get; init; }

    [JsonPropertyName("preferred_model")]
    public string? PreferredModel { get; init; }

    private const string ConfigFileName = "streambench_ai_config.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads config from disk. Returns defaults if file doesn't exist.
    /// Searches: output dir → executable dir → cwd.
    /// </summary>
    public static AiBackendConfig Load(string? outputDir = null)
    {
        foreach (var dir in CandidateDirs(outputDir))
        {
            var path = Path.Combine(dir, ConfigFileName);
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AiBackendConfig>(json, JsonOpts) ?? new();
            }
            catch
            {
                // Corrupt config — use defaults
            }
        }
        return new();
    }

    /// <summary>Saves config to disk (in executable dir or output dir).</summary>
    public void Save(string? outputDir = null)
    {
        var dir = outputDir
            ?? Path.GetDirectoryName(Environment.ProcessPath)
            ?? Directory.GetCurrentDirectory();
        try
        {
            var path = Path.Combine(dir, ConfigFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
            TraceLog.DiagnosticInfo($"AI config saved to {path}");
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Failed to save AI config: {ex.Message}");
        }
    }

    private static IEnumerable<string> CandidateDirs(string? outputDir)
    {
        if (!string.IsNullOrEmpty(outputDir)) yield return outputDir;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir)) yield return exeDir;
        yield return Directory.GetCurrentDirectory();
    }
}
#endif
