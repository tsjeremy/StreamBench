// Models/AiInferenceBenchmarkResult.cs
// Data model for AI inference benchmark results (Microsoft.AI.Foundry.Local).

using System.Text.Json.Serialization;

namespace StreamBench.Models;

/// <summary>
/// Timings and token counts for a single AI inference run.
/// </summary>
public record AiInferenceRun(
    [property: JsonPropertyName("model_load_sec")]      double ModelLoadSec,
    [property: JsonPropertyName("response_time_sec")]   double ResponseTimeSec,
    [property: JsonPropertyName("total_time_sec")]      double TotalTimeSec,
    [property: JsonPropertyName("prompt_tokens")]       int PromptTokens,
    [property: JsonPropertyName("completion_tokens")]   int CompletionTokens,
    [property: JsonPropertyName("tokens_per_second")]   double TokensPerSecond,
    [property: JsonPropertyName("response_text")]       string ResponseText,
    [property: JsonPropertyName("response_preview")]    string ResponsePreview
);

/// <summary>
/// Full AI benchmark result for one device (CPU / GPU / NPU).
/// </summary>
public record AiDeviceBenchmarkResult(
    [property: JsonPropertyName("device_type")]         string DeviceType,
    [property: JsonPropertyName("model_id")]            string ModelId,
    [property: JsonPropertyName("model_alias")]         string ModelAlias,
    [property: JsonPropertyName("execution_provider")]  string ExecutionProvider,
    [property: JsonPropertyName("question1")]           string Question1,
    [property: JsonPropertyName("run1")]                AiInferenceRun Run1,
    [property: JsonPropertyName("question2")]           string Question2,
    [property: JsonPropertyName("run2")]                AiInferenceRun Run2,
    [property: JsonPropertyName("timestamp")]           string Timestamp
);
