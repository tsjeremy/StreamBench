#if ENABLE_AI
// AiBenchmarkRunner.cs
// AI inference benchmark using Microsoft Foundry Local CLI + REST API.
//
// Fix4: Replaced Microsoft.AI.Foundry.Local NuGet with direct CLI/REST calls
// to eliminate OnnxRuntime native DLL conflicts in self-contained single-file
// publish (TypeInitializationException at NativeMethods..cctor).
//
// Measures response time and tokens/second for two prompts on each
// hardware device (CPU, GPU, NPU) using the local Foundry service:
//
//   Q1 "Hello World!"                                        — cold run (includes model load)
//   Q2 "How to calculate memory bandwidth on different memory?" — warm run (model already loaded)
//
// Results are displayed as formatted tables and saved as JSON when --ai is used.
//
// ETW events are emitted via TraceLog for diagnostics.
// All exceptions include source file/line via DiagnosticHelper.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamBench.Models;

namespace StreamBench;

public static class AiBenchmarkRunner
{
    // ── Benchmark prompts ─────────────────────────────────────────────────
    public const string Q1 = "Hello World!";
    public const string Q2 = "How to calculate memory bandwidth on different memory?";
    public static readonly string[] RelationQuestions =
    [
        Q1,
        Q2,
        "Based on all local JSON files in this folder (including files from other devices), summarize memory bandwidth and AI benchmark relationship, highlight the best combined profile and also try to explain the % from memory bandwidth benchmark result vs. the theoretical bandwidth calculation of the memory on the device."
    ];

    // Preferred aliases by device (robust defaults).
    private static readonly string[] PreferredAliasesCpu =
    [
        "phi-3.5-mini",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "phi-4-mini",
        "qwen2.5-7b",
        "phi-4-mini-reasoning",
        "gpt-oss-20b",
        "qwen2.5-14b",
        "phi-4",
        "deepseek-r1-7b",
        "deepseek-r1-14b",
    ];

    private static readonly string[] PreferredAliasesGpu =
    [
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "phi-3.5-mini",
        "phi-4-mini",
        "deepseek-r1-7b",
        "deepseek-r1-14b",
    ];

    private static readonly string[] PreferredAliasesNpu =
    [
        "phi-4-mini",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
        "phi-4-mini-reasoning",
        "deepseek-r1-7b",
    ];

    // Shared-model priorities used when benchmarking multiple devices side-by-side.
    // Models with broad device coverage (CPU+GPU+NPU) are listed first to minimise
    // unnecessary downloads when running multi-device comparisons.
    private static readonly string[] SharedAliasPriority =
    [
        "phi-4-mini",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
        "deepseek-r1-7b",
        "phi-4-mini-reasoning",
        "qwen2.5-1.5b",
        "phi-3.5-mini",
        "deepseek-r1-14b",
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Foundry CLI helpers ───────────────────────────────────────────────

    /// <summary>Finds the foundry CLI executable.</summary>
    private static string? FindFoundryCli()
    {
        foreach (var name in new[] { "foundry", "foundrylocal" })
        {
            try
            {
                var psi = new ProcessStartInfo(name, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return name;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Runs a foundry CLI command and returns stdout.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunFoundryAsync(
        string cli, string arguments, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(cli, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "Timeout waiting for foundry CLI");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Starts the Foundry Local service and returns the base URL.</summary>
    private static async Task<string?> StartServiceAsync(string cli)
    {
        // Check if already running
        var (exitCode, stdout, _) = await RunFoundryAsync(cli, "service status");
        if (stdout.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            var url = ExtractServiceUrl(stdout);
            if (url is not null)
            {
                TraceLog.DiagnosticInfo($"Foundry service already running at {url}");
                return url;
            }
        }

        // Start the service
        TraceLog.AiServiceStarting();
        var (startExit, startOut, startErr) = await RunFoundryAsync(cli, "service start", 60_000);

        // Extract URL from output (e.g. "Service is Started on http://127.0.0.1:57502/")
        var serviceUrl = ExtractServiceUrl(startOut + "\n" + startErr);
        if (serviceUrl is not null)
        {
            TraceLog.AiServiceStarted();
            return serviceUrl;
        }

        // Fallback: try default port
        if (startExit == 0)
        {
            TraceLog.AiServiceStarted();
            return "http://127.0.0.1:5273";
        }

        TraceLog.AiServiceStartFailed(
            $"foundry service start failed (exit={startExit}): {startErr}", "AiBenchmarkRunner.cs", 0);
        return null;
    }

    private static string? ExtractServiceUrl(string text)
    {
        // Look for http://host:port pattern in the output and return only the base URL
        // (scheme + host + port). Foundry CLI may append a path like /openai/status
        // which must NOT be included — the caller appends /v1/chat/completions.
        foreach (var line in text.Split('\n'))
        {
            int idx = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            int end = idx;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ',' && line[end] != '!')
                end++;

            string url = line[idx..end].TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                // Strip path — keep only scheme://host:port
                string baseUrl = $"{uri.Scheme}://{uri.Authority}";
                return baseUrl;
            }
        }
        return null;
    }

    private static async Task StopServiceAsync(string cli)
    {
        try
        {
            await RunFoundryAsync(cli, "service stop", 15_000);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"StopService: {ex.Message}");
        }
    }

    // ── Model catalog via CLI ─────────────────────────────────────────────

    /// <summary>Model info parsed from foundry CLI JSON output.</summary>
    private sealed record FoundryModel(
        string Id,
        string Alias,
        string DeviceType,
        string ExecutionProvider,
        double FileSizeMb,
        bool IsCached);

    /// <summary>Lists all available models from the foundry catalog.</summary>
    private static async Task<List<FoundryModel>> ListModelsAsync(string cli)
    {
        var models = new List<FoundryModel>();

        // Try JSON output first
        var (exitCode, stdout, _) = await RunFoundryAsync(cli, "model list --json", 180_000);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            var jsonModels = TryParseModelListJson(stdout);
            if (jsonModels.Count > 0)
                return jsonModels;
        }

        // Fallback: parse text output
        (exitCode, stdout, _) = await RunFoundryAsync(cli, "model list", 180_000);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            return ParseModelListText(stdout);
        }

        return models;
    }

    private static List<FoundryModel> TryParseModelListJson(string json)
    {
        var models = new List<FoundryModel>();
        try
        {
            // Try to find JSON array in the output (skip non-JSON lines)
            int arrayStart = json.IndexOf('[');
            if (arrayStart < 0) return models;
            string jsonTrimmed = json[arrayStart..];

            using var doc = JsonDocument.Parse(jsonTrimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return models;

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                // Each entry may be a model with variants, or a flat model variant
                if (elem.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
                {
                    string modelAlias = elem.TryGetProperty("alias", out var a) ? a.GetString() ?? "" : "";
                    foreach (var v in variants.EnumerateArray())
                        AddModelFromJson(models, v, modelAlias);
                }
                else
                {
                    AddModelFromJson(models, elem, null);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Failed to parse model list JSON: {ex.Message}");
        }
        return models;
    }

    private static void AddModelFromJson(List<FoundryModel> models, JsonElement elem, string? parentAlias)
    {
        string id = elem.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id) && elem.TryGetProperty("name", out var nameProp))
            id = nameProp.GetString() ?? "";
        if (string.IsNullOrEmpty(id)) return;

        string alias = parentAlias ?? "";
        if (elem.TryGetProperty("alias", out var aliasProp))
            alias = aliasProp.GetString() ?? alias;
        if (string.IsNullOrEmpty(alias))
            alias = id.Split('/').Last().Split('-').FirstOrDefault() ?? id;

        string device = "CPU";
        if (elem.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object)
        {
            if (rt.TryGetProperty("deviceType", out var dt))
                device = dt.GetString()?.ToUpperInvariant() ?? "CPU";
            else if (rt.TryGetProperty("device_type", out dt))
                device = dt.GetString()?.ToUpperInvariant() ?? "CPU";
        }
        else if (elem.TryGetProperty("device_type", out var dt2))
            device = dt2.GetString()?.ToUpperInvariant() ?? "CPU";

        string ep = "";
        if (elem.TryGetProperty("runtime", out var rt2) && rt2.ValueKind == JsonValueKind.Object
            && rt2.TryGetProperty("executionProvider", out var epProp))
            ep = epProp.GetString() ?? "";

        double sizeMb = 0;
        if (elem.TryGetProperty("fileSizeMb", out var sz))
            sz.TryGetDouble(out sizeMb);
        else if (elem.TryGetProperty("file_size_mb", out sz))
            sz.TryGetDouble(out sizeMb);

        bool cached = false;
        if (elem.TryGetProperty("isCached", out var c))
            cached = c.ValueKind == JsonValueKind.True;

        models.Add(new FoundryModel(id, alias, device, ep, sizeMb, cached));
    }

    private static List<FoundryModel> ParseModelListText(string text)
    {
        // Parse tabular output from `foundry model list` (v0.8.x format):
        //   Alias          Device  Task       File Size  License  Model ID
        //   ---------------------------------------------------------------
        //   phi-3.5-mini   GPU     chat       2.16 GB    MIT      Phi-3.5-mini-instruct-generic-gpu:1
        //                  CPU     chat       2.53 GB    MIT      Phi-3.5-mini-instruct-generic-cpu:1
        //   ---------------------------------------------------------------
        //   phi-4          GPU     chat       8.37 GB    MIT      Phi-4-generic-gpu:1
        //                  CPU     chat       10.16 GB   MIT      Phi-4-generic-cpu:1
        var models = new List<FoundryModel>();
        string lastAlias = "";

        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Skip header line
            if (trimmed.StartsWith("Alias", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Device", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Model ID", StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip separator lines (dashes)
            if (trimmed.StartsWith('-') || trimmed.StartsWith('─'))
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            // Detect whether this is an alias line or a continuation line.
            // Continuation lines start with whitespace (the raw line, not trimmed).
            bool isContinuation = line.Length > 0 && char.IsWhiteSpace(line[0]);

            string alias;
            string[] fields;
            if (isContinuation)
            {
                alias = lastAlias;
                fields = parts;
            }
            else
            {
                alias = parts[0];
                lastAlias = alias;
                fields = parts[1..];
            }

            // Detect device type from the first field after alias
            string device = "CPU";
            if (fields.Length > 0)
            {
                string firstField = fields[0];
                if (firstField.Equals("GPU", StringComparison.OrdinalIgnoreCase)) device = "GPU";
                else if (firstField.Equals("NPU", StringComparison.OrdinalIgnoreCase)) device = "NPU";
                else if (firstField.Equals("CPU", StringComparison.OrdinalIgnoreCase)) device = "CPU";
            }

            // Model ID is the last field (e.g. "Phi-3.5-mini-instruct-generic-gpu:1")
            string id = fields[^1];
            // Avoid using license or size tokens as ID
            if (id.Length < 4 || id.All(c => c == '-' || char.IsDigit(c) || c == '.'))
                continue;

            models.Add(new FoundryModel(id, alias, device, "", 0, IsCached: false));
        }
        return models;
    }

    /// <summary>Loads a model via foundry CLI. Downloads first if not cached.</summary>
    private static async Task<bool> LoadModelAsync(string cli, string modelId, int timeoutMs = 300_000)
    {
        TraceLog.AiModelLoading(modelId, "");

        // Try loading directly first (succeeds if model is already cached)
        var (exitCode, stdout, stderr) = await RunFoundryAsync(cli, $"model load \"{modelId}\"", timeoutMs);
        if (exitCode == 0)
        {
            TraceLog.AiModelLoaded(modelId, 0);
            return true;
        }

        // If load failed because model isn't downloaded, download it first
        string combined = (stdout + " " + stderr).ToLowerInvariant();
        if (combined.Contains("not found locally") || combined.Contains("download")
            || combined.Contains("bad request") || combined.Contains("not cached"))
        {
            ConsoleOutput.WriteMarkup($"[dim]  Model not cached — downloading {modelId}...[/]");
            TraceLog.DiagnosticInfo($"Model not cached, downloading: {modelId}");

            var (dlExit, _, dlErr) = await RunFoundryAsync(cli, $"model download \"{modelId}\"", 600_000);
            if (dlExit != 0)
            {
                TraceLog.AiModelLoadFailed(modelId, $"Download failed: {dlErr}", "AiBenchmarkRunner.cs", 0);
                ConsoleOutput.WriteMarkup($"[red]  Download failed for {modelId}[/]");
                return false;
            }

            ConsoleOutput.WriteMarkup($"[dim]  Download complete — loading {modelId}...[/]");

            // Retry load after download
            (exitCode, _, stderr) = await RunFoundryAsync(cli, $"model load \"{modelId}\"", timeoutMs);
            if (exitCode == 0)
            {
                TraceLog.AiModelLoaded(modelId, 0);
                return true;
            }
        }

        TraceLog.AiModelLoadFailed(modelId, stderr, "AiBenchmarkRunner.cs", 0);
        return false;
    }

    /// <summary>Unloads a model via foundry CLI.</summary>
    private static async Task UnloadModelAsync(string cli, string modelId)
    {
        try
        {
            await RunFoundryAsync(cli, $"model unload \"{modelId}\"", 15_000);
            TraceLog.AiModelUnloaded(modelId);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Model unload failed for {modelId}: {ex.Message}");
        }
    }

    // ── REST API inference ────────────────────────────────────────────────

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMsg[] Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens = 2048);

    private sealed record ChatMsg(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] ChatChoice[]? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatChoiceMessage? Message);

    private sealed record ChatChoiceMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// Runs the AI inference benchmark on the requested device(s).
    /// Returns one result per successfully benchmarked device.
    /// </summary>
    public static async Task<List<AiDeviceBenchmarkResult>> RunAsync(
        IEnumerable<string>? devices = null,
        string? modelAlias = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AiDeviceBenchmarkResult>();

        string? cli = FindFoundryCli();
        if (cli is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Foundry Local CLI not found (foundry / foundrylocal).");
            ConsoleOutput.WriteMarkup("[dim]  Install: winget install Microsoft.FoundryLocal[/]");
            return results;
        }

        string? serviceUrl = await StartServiceAsync(cli);
        if (serviceUrl is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Cannot start Foundry Local service.");
            ConsoleOutput.WriteMarkup("[dim]  Try: foundry service start[/]");
            return results;
        }

        TraceLog.DiagnosticInfo($"Foundry service URL: {serviceUrl}");
        ConsoleOutput.WriteMarkup($"[bold cyan]Starting Microsoft AI Foundry Local service...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Service URL: {serviceUrl}[/]");

        try
        {
            var allModels = await ListModelsAsync(cli);

            if (allModels.Count == 0)
            {
                TraceLog.AiCatalogUnavailable("No models found in catalog");
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No models found in catalog. " +
                    "Ensure Foundry Local is installed and models are downloaded.");
                return results;
            }

            var targetDevices = ParseDeviceFilter(devices);
            string? effectiveAlias = modelAlias;
            bool strictAlias = false;

            // Multi-device side-by-side comparison with shared model
            if (string.IsNullOrWhiteSpace(effectiveAlias) && targetDevices.Count > 1)
            {
                var sharedCandidates = SelectSharedAliasCandidates(allModels, targetDevices);
                if (sharedCandidates.Count > 0)
                {
                    strictAlias = true;

                    int bestSuccessCount = -1;
                    List<AiDeviceBenchmarkResult> bestAttemptResults = [];

                    foreach (var sharedAlias in sharedCandidates)
                    {
                        ConsoleOutput.WriteMarkup(
                            $"[bold cyan]Trying shared model alias for side-by-side comparison:[/] [white]{sharedAlias}[/]");

                        var attemptResults = new List<AiDeviceBenchmarkResult>();
                        int successCount = 0;

                        foreach (var deviceType in targetDevices)
                        {
                            var model = FindBestModel(allModels, deviceType, sharedAlias, strictAlias);
                            if (model is null)
                            {
                                ConsoleOutput.WriteMarkup(
                                    $"[yellow][SKIP][/] Shared alias [white]{sharedAlias}[/] not available for [white]{deviceType}[/].");
                                continue;
                            }

                            Console.WriteLine();
                            ConsoleOutput.WriteMarkup(
                                $"[bold cyan]── AI Benchmark: {deviceType} ({model.Id}) ──[/]");

                            var result = await BenchmarkModelAsync(
                                cli, serviceUrl, model, deviceType, cancellationToken);
                            if (result is not null)
                            {
                                attemptResults.Add(result);
                                successCount++;
                            }
                        }

                        if (successCount > bestSuccessCount)
                        {
                            bestSuccessCount = successCount;
                            bestAttemptResults = attemptResults;
                        }

                        if (successCount == targetDevices.Count)
                            return attemptResults;

                        ConsoleOutput.WriteMarkup(
                            $"[yellow][WARN][/] Shared alias [white]{sharedAlias}[/] covered [white]{successCount}/{targetDevices.Count}[/] devices; trying next candidate.[/]");
                    }

                    if (bestAttemptResults.Count > 0)
                    {
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][WARN][/] No single shared model covered all selected devices; returning best coverage result [white]{bestAttemptResults.Count}/{targetDevices.Count}[/].");
                        return bestAttemptResults;
                    }

                    ConsoleOutput.WriteMarkup(
                        "[yellow][WARN][/] No shared alias produced successful results; falling back to per-device defaults.");
                    strictAlias = false;
                    effectiveAlias = null;
                }
                else
                {
                    ConsoleOutput.WriteMarkup(
                        "[yellow][WARN][/] No shared alias found across selected devices; using per-device defaults.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(effectiveAlias))
            {
                strictAlias = true;
            }

            foreach (var deviceType in targetDevices)
            {
                var model = FindBestModel(allModels, deviceType, effectiveAlias, strictAlias);
                if (model is null)
                {
                    ConsoleOutput.WriteMarkup(
                        $"[yellow][SKIP][/] No model available for [white]{deviceType}[/].");
                    continue;
                }

                Console.WriteLine();
                ConsoleOutput.WriteMarkup(
                    $"[bold cyan]── AI Benchmark: {deviceType} ({model.Id}) ──[/]");

                var result = await BenchmarkModelAsync(
                    cli, serviceUrl, model, deviceType, cancellationToken);
                if (result is not null)
                    results.Add(result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diag = DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[red]Error:[/] Foundry Local service error: {ex.Message}");
            ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
        }
        finally
        {
            await StopServiceAsync(cli);
        }

        return results;
    }

    /// <summary>
    /// Runs three local-AI analysis questions over all benchmark JSON files
    /// in the specified directory to summarize memory-bandwidth vs AI relation.
    /// </summary>
    public static async Task<AiLocalRelationSummaryResult?> RunLocalRelationSummaryAsync(
        string directoryPath,
        string? modelAlias = null,
        CancellationToken cancellationToken = default)
    {
        string sourceDir = Path.GetFullPath(
            string.IsNullOrWhiteSpace(directoryPath) ? "." : directoryPath);

        var dataset = ReadLocalRelationDataset(sourceDir);
        if (dataset.MemorySamples.Count == 0)
        {
            ConsoleOutput.WriteMarkup("[yellow][WARN][/] No STREAM result JSON files found for local relation summary.");
            return null;
        }

        if (dataset.AiSamples.Count == 0)
        {
            ConsoleOutput.WriteMarkup("[yellow][WARN][/] No AI benchmark JSON files found for local relation summary.");
            return null;
        }

        var deviceAggregates = BuildDeviceAggregates(dataset.MemorySamples, dataset.AiSamples);
        double? deviceCorrelation = CalculateDeviceCorrelation(deviceAggregates);
        string relationContext = BuildRelationContext(sourceDir, dataset, deviceAggregates, deviceCorrelation);

        string? cli = FindFoundryCli();
        if (cli is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Foundry Local CLI not found for relation summary.");
            return null;
        }

        string? serviceUrl = await StartServiceAsync(cli);
        if (serviceUrl is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Cannot start Foundry Local service for relation summary.");
            return null;
        }

        ConsoleOutput.WriteMarkup("[bold cyan]Running local-AI relation summary from saved JSON files...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Source folder: {sourceDir}[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Files: {dataset.MemoryFileCount} memory JSON, {dataset.AiFileCount} AI JSON[/]");

        FoundryModel? model = null;
        try
        {
            var allModels = await ListModelsAsync(cli);
            if (allModels.Count == 0)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No local AI models available for relation summary.");
                return null;
            }

            bool strictAlias = !string.IsNullOrWhiteSpace(modelAlias);
            model = FindBestModel(allModels, "CPU", modelAlias, strictAlias);
            if (model is null)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No CPU model available for local relation summary.");
                return null;
            }

            // Load model
            ConsoleOutput.WriteMarkup($"[dim]  Loading summary model: {model.Id}[/]");
            if (!await LoadModelAsync(cli, model.Id))
            {
                ConsoleOutput.WriteMarkup($"[red][FAIL][/] Failed to load model {model.Id}");
                return null;
            }

            var answers = new List<AiRelationQuestionAnswer>();

            for (int i = 0; i < RelationQuestions.Length; i++)
            {
                string question = RelationQuestions[i];
                string prompt = i switch
                {
                    0 => Q1,
                    1 => Q2,
                    _ => BuildRelationPrompt(relationContext, question),
                };
                ConsoleOutput.WriteMarkup($"[dim]  Q{i + 1}: {question}[/]");

                var run = await RunInferenceAsync(
                    serviceUrl, model.Id, prompt,
                    modelLoadSec: 0,
                    deviceLabel: "CPU",
                    ct: cancellationToken);

                if (run is null)
                    return null;

                answers.Add(new AiRelationQuestionAnswer(
                    Index: i + 1,
                    Question: question,
                    Answer: run.ResponseText.Trim(),
                    Run: run));
            }

            return new AiLocalRelationSummaryResult(
                SourceDirectory: sourceDir,
                MemoryJsonFiles: dataset.MemoryFileCount,
                AiJsonFiles: dataset.AiFileCount,
                MemorySamples: dataset.MemorySamples.Count,
                AiSamples: dataset.AiSamples.Count,
                ModelId: model.Id,
                ModelAlias: model.Alias,
                ExecutionProvider: model.ExecutionProvider,
                DeviceLevelCorrelation: deviceCorrelation,
                DeviceAggregates: deviceAggregates,
                Questions: answers,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diag = DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[red]Error:[/] Local relation summary failed: {ex.Message}");
            ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
            return null;
        }
        finally
        {
            if (model is not null)
                await UnloadModelAsync(cli, model.Id);
            await StopServiceAsync(cli);
        }
    }

    // ── Core benchmark for a single model ─────────────────────────────────

    private static async Task<AiDeviceBenchmarkResult?> BenchmarkModelAsync(
        string cli, string serviceUrl, FoundryModel model,
        string deviceLabel, CancellationToken ct)
    {
        // Load model
        ConsoleOutput.WriteMarkup("[dim]  Loading model...[/]");
        var loadSw = Stopwatch.StartNew();
        if (!await LoadModelAsync(cli, model.Id))
        {
            ConsoleOutput.WriteMarkup($"[red]  Failed to load {model.Id}[/]");
            return null;
        }
        loadSw.Stop();
        double modelLoadSec = loadSw.Elapsed.TotalSeconds;
        ConsoleOutput.WriteMarkup($"[dim]  Model loaded in {modelLoadSec:F2} s[/]");

        try
        {
            // Q1: first inference (cold — model just loaded)
            ConsoleOutput.WriteMarkup($"[dim]  Q1: {Q1}[/]");
            var run1 = await RunInferenceAsync(
                serviceUrl, model.Id, Q1, modelLoadSec, deviceLabel, ct);

            if (run1 is null)
            {
                ConsoleOutput.WriteMarkup("[red]  Q1 inference failed.[/]");
                return null;
            }

            // Q2: second inference (warm — model already in memory)
            ConsoleOutput.WriteMarkup($"[dim]  Q2: {Q2}[/]");
            var run2 = await RunInferenceAsync(
                serviceUrl, model.Id, Q2, modelLoadSec: 0, deviceLabel, ct);

            if (run2 is null)
            {
                ConsoleOutput.WriteMarkup("[red]  Q2 inference failed.[/]");
                return null;
            }

            return new AiDeviceBenchmarkResult(
                DeviceType:        deviceLabel,
                ModelId:           model.Id,
                ModelAlias:        model.Alias,
                ExecutionProvider: model.ExecutionProvider,
                Question1:         Q1,
                Run1:              run1,
                Question2:         Q2,
                Run2:              run2,
                Timestamp:         DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[red]  Benchmark model failed:[/] {ex.Message}");
            return null;
        }
        finally
        {
            await UnloadModelAsync(cli, model.Id);
        }
    }

    // ── Single inference run via REST API ──────────────────────────────────

    private static async Task<AiInferenceRun?> RunInferenceAsync(
        string serviceUrl, string modelId, string prompt,
        double modelLoadSec, string deviceLabel = "unknown",
        CancellationToken ct = default)
    {
        string promptPreview = prompt.Length > 60 ? prompt[..60] + "…" : prompt;
        TraceLog.AiInferenceStarted(promptPreview, deviceLabel);

        var request = new ChatRequest(
            Model: modelId,
            Messages: [new ChatMsg("user", prompt)]);

        var sw = Stopwatch.StartNew();
        ChatResponse? response;

        try
        {
            var httpResponse = await Http.PostAsJsonAsync(
                $"{serviceUrl}/v1/chat/completions", request, JsonOpts, ct);
            httpResponse.EnsureSuccessStatusCode();
            response = await httpResponse.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var diag = DiagnosticHelper.LogException(ex);
            TraceLog.AiInferenceFailed(ex.Message, "AiBenchmarkRunner.cs", 0);
            ConsoleOutput.WriteMarkup($"[red]  Inference error:[/] {ex.Message}");
            ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
            return null;
        }

        sw.Stop();
        double responseSec = sw.Elapsed.TotalSeconds;

        if (response is null || response.Choices is null || response.Choices.Length == 0)
        {
            TraceLog.AiInferenceFailed("Empty response", "AiBenchmarkRunner.cs", 0);
            ConsoleOutput.WriteMarkup("[red]  Inference failed:[/] Empty or null response");
            return null;
        }

        string content = response.Choices[0].Message?.Content ?? "";
        int completionTokens = response.Usage?.CompletionTokens ?? EstimateTokens(content);
        int promptTokens = response.Usage?.PromptTokens ?? 0;
        double tokensPerSec = responseSec > 0 ? completionTokens / responseSec : 0;
        string preview = content.Length > 200 ? content[..200] + "…" : content;

        TraceLog.AiInferenceCompleted(sw.ElapsedMilliseconds, completionTokens);

        return new AiInferenceRun(
            ModelLoadSec:      modelLoadSec,
            ResponseTimeSec:   responseSec,
            TotalTimeSec:      modelLoadSec + responseSec,
            PromptTokens:      promptTokens,
            CompletionTokens:  completionTokens,
            TokensPerSecond:   tokensPerSec,
            ResponseText:      content,
            ResponsePreview:   preview);
    }

    // ── Model selection ───────────────────────────────────────────────────

    private static FoundryModel? FindBestModel(
        IReadOnlyList<FoundryModel> allModels,
        string deviceLabel,
        string? aliasHint,
        bool strictAlias)
    {
        var candidates = allModels
            .Where(m => m.DeviceType.Equals(deviceLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            TraceLog.DiagnosticInfo(
                $"No model candidates found for device={deviceLabel}");
            return null;
        }

        // User-specified alias takes priority
        if (!string.IsNullOrWhiteSpace(aliasHint))
        {
            var match = candidates.FirstOrDefault(m =>
                m.Alias.Equals(aliasHint, StringComparison.OrdinalIgnoreCase)
                || m.Id.Equals(aliasHint, StringComparison.OrdinalIgnoreCase)
                || m.Id.Contains(aliasHint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;

            ConsoleOutput.WriteMarkup(
                $"[yellow][WARN][/] Model '{aliasHint}' not found for {deviceLabel}; falling back to preferred list.");

            if (strictAlias)
                return null;
        }

        var preferredAliases = GetPreferredAliases(deviceLabel);

        // 1. Preferred aliases that are already cached
        foreach (var alias in preferredAliases)
        {
            var cachedPreferred = candidates.FirstOrDefault(c =>
                c.IsCached &&
                (c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.Contains(alias, StringComparison.OrdinalIgnoreCase)));
            if (cachedPreferred is not null) return cachedPreferred;
        }

        // 2. Any cached model for this device (prefer smaller model)
        var anyCached = candidates
            .Where(c => c.IsCached)
            .OrderBy(c => c.FileSizeMb > 0 ? c.FileSizeMb : double.MaxValue)
            .FirstOrDefault();
        if (anyCached is not null) return anyCached;

        // 3. Preferred aliases in configured order (download if needed)
        foreach (var alias in preferredAliases)
        {
            var v = candidates.FirstOrDefault(c =>
                c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.Contains(alias, StringComparison.OrdinalIgnoreCase));
            if (v is not null) return v;
        }

        // 4. Smallest available model
        return candidates
            .OrderBy(c => c.FileSizeMb > 0 ? c.FileSizeMb : double.MaxValue)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> GetPreferredAliases(string deviceLabel) =>
        deviceLabel.ToUpperInvariant() switch
        {
            "GPU" => PreferredAliasesGpu,
            "NPU" => PreferredAliasesNpu,
            _     => PreferredAliasesCpu,
        };

    private static List<string> SelectSharedAliasCandidates(
        IReadOnlyList<FoundryModel> allModels,
        IReadOnlyList<string> targetDevices)
    {
        if (targetDevices.Count == 0)
            return [];

        var targetSet = new HashSet<string>(targetDevices.Select(d => d.ToUpperInvariant()));

        var byAlias = allModels
            .Where(m => !string.IsNullOrWhiteSpace(m.Alias))
            .GroupBy(m => m.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var deviceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in g)
                    deviceSet.Add(m.DeviceType.ToUpperInvariant());

                int coverage = deviceSet.Count(d => targetSet.Contains(d));
                return new { Alias = g.Key, Coverage = coverage, Devices = deviceSet };
            })
            .Where(x => x.Coverage > 0)
            .ToList();

        if (byAlias.Count == 0)
            return [];

        int PriorityIndex(string alias)
        {
            int idx = Array.FindIndex(SharedAliasPriority,
                p => p.Equals(alias, StringComparison.OrdinalIgnoreCase));
            return idx < 0 ? int.MaxValue : idx;
        }

        return byAlias
            .OrderByDescending(x => x.Coverage)
            .ThenBy(x => PriorityIndex(x.Alias))
            .ThenBy(x => x.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Alias)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed record MemorySample(
        string DeviceType,
        double TriadMbps,
        string Hostname,
        string Timestamp,
        double? TheoreticalGbps,
        double? MeasuredVsTheoreticalPercent,
        double? MemorySpeedMts,
        double? BusWidthBits);

    private sealed record AiSample(
        string DeviceType,
        double WarmTokensPerSecond,
        string ModelAlias,
        string Timestamp);

    private sealed record LocalRelationDataset(
        int MemoryFileCount,
        int AiFileCount,
        List<MemorySample> MemorySamples,
        List<AiSample> AiSamples);

    private static LocalRelationDataset ReadLocalRelationDataset(string directoryPath)
    {
        int memoryFileCount = 0;
        int aiFileCount = 0;
        var memorySamples = new List<MemorySample>();
        var aiSamples = new List<AiSample>();

        if (!Directory.Exists(directoryPath))
            return new LocalRelationDataset(memoryFileCount, aiFileCount, memorySamples, aiSamples);

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(filePath);
                if (fileName.StartsWith("stream_", StringComparison.OrdinalIgnoreCase)
                    && fileName.Contains("_results_", StringComparison.OrdinalIgnoreCase))
                {
                    memoryFileCount++;
                    TryReadMemorySample(filePath, fileName, memorySamples);
                }
                else if (fileName.StartsWith("ai_inference_benchmark_", StringComparison.OrdinalIgnoreCase))
                {
                    aiFileCount++;
                    TryReadAiSamples(filePath, aiSamples);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Error enumerating JSON files in {directoryPath}: {ex.Message}");
        }

        return new LocalRelationDataset(memoryFileCount, aiFileCount, memorySamples, aiSamples);
    }

    private static void TryReadMemorySample(
        string filePath,
        string fileName,
        List<MemorySample> memorySamples)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            var root = doc.RootElement;
            double? triadMbps = TryGetDouble(root, "results", "triad", "best_rate_mbps");
            if (!triadMbps.HasValue || triadMbps.Value <= 0)
                return;

            string? typeFromJson = TryGetString(root, "type");
            string deviceType = NormalizeDeviceType(typeFromJson, fileName);
            string host = TryGetString(root, "system", "hostname") ?? "unknown";
            string timestamp = TryGetString(root, "timestamp") ?? "";
            var (theoreticalGbps, speedMts, busWidthBits) = EstimateTheoreticalMemoryBandwidth(root);
            double measuredGbps = triadMbps.Value / 1000.0;
            double? measuredVsTheoretical = theoreticalGbps.HasValue && theoreticalGbps.Value > 0
                ? (measuredGbps / theoreticalGbps.Value) * 100.0
                : null;

            memorySamples.Add(new MemorySample(
                deviceType,
                triadMbps.Value,
                host,
                timestamp,
                theoreticalGbps,
                measuredVsTheoretical,
                speedMts,
                busWidthBits));
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup(
                $"[yellow][WARN][/] Skipping invalid memory JSON [white]{fileName}[/]: {ex.Message}");
        }
    }

    private static void TryReadAiSamples(string filePath, List<AiSample> aiSamples)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                using var entries = root.EnumerateArray();
                while (entries.MoveNext())
                    TryReadAiSampleEntry(entries.Current, aiSamples);
                return;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array)
            {
                using var entries = results.EnumerateArray();
                while (entries.MoveNext())
                    TryReadAiSampleEntry(entries.Current, aiSamples);
                return;
            }

            if (root.ValueKind == JsonValueKind.Object)
                TryReadAiSampleEntry(root, aiSamples);
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup(
                $"[yellow][WARN][/] Skipping invalid AI JSON [white]{Path.GetFileName(filePath)}[/]: {ex.Message}");
        }
    }

    private static void TryReadAiSampleEntry(JsonElement entry, List<AiSample> aiSamples)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return;

        string deviceType = NormalizeDeviceType(TryGetString(entry, "device_type"), null);
        double? warmTok = TryGetDouble(entry, "run2", "tokens_per_second")
                          ?? TryGetDouble(entry, "run1", "tokens_per_second");
        if (!warmTok.HasValue || warmTok.Value <= 0)
            return;

        string modelAlias = TryGetString(entry, "model_alias") ?? "unknown";
        string timestamp = TryGetString(entry, "timestamp") ?? "";
        aiSamples.Add(new AiSample(deviceType, warmTok.Value, modelAlias, timestamp));
    }

    private static List<AiRelationDeviceAggregate> BuildDeviceAggregates(
        List<MemorySample> memorySamples,
        List<AiSample> aiSamples)
    {
        var deviceOrder = new[] { "CPU", "GPU", "NPU" };
        var aggregates = new List<AiRelationDeviceAggregate>();

        foreach (var device in deviceOrder)
        {
            var memoryVals = memorySamples
                .Where(x => x.DeviceType == device)
                .Select(x => x.TriadMbps / 1000.0)
                .ToList();
            var aiVals = aiSamples
                .Where(x => x.DeviceType == device)
                .Select(x => x.WarmTokensPerSecond)
                .ToList();

            if (memoryVals.Count == 0 && aiVals.Count == 0)
                continue;

            aggregates.Add(new AiRelationDeviceAggregate(
                DeviceType: device,
                MemorySamples: memoryVals.Count,
                AvgMemoryTriadGbps: memoryVals.Count > 0 ? memoryVals.Average() : 0,
                AiSamples: aiVals.Count,
                AvgAiWarmTokensPerSecond: aiVals.Count > 0 ? aiVals.Average() : 0));
        }

        return aggregates;
    }

    private static double? CalculateDeviceCorrelation(IReadOnlyList<AiRelationDeviceAggregate> aggregates)
    {
        var paired = aggregates
            .Where(x => x.MemorySamples > 0 && x.AiSamples > 0)
            .ToList();

        if (paired.Count < 2)
            return null;

        double meanX = paired.Average(x => x.AvgMemoryTriadGbps);
        double meanY = paired.Average(x => x.AvgAiWarmTokensPerSecond);

        double covariance = paired.Sum(x =>
            (x.AvgMemoryTriadGbps - meanX) * (x.AvgAiWarmTokensPerSecond - meanY));
        double varianceX = paired.Sum(x =>
            Math.Pow(x.AvgMemoryTriadGbps - meanX, 2));
        double varianceY = paired.Sum(x =>
            Math.Pow(x.AvgAiWarmTokensPerSecond - meanY, 2));

        if (varianceX <= double.Epsilon || varianceY <= double.Epsilon)
            return null;

        return covariance / Math.Sqrt(varianceX * varianceY);
    }

    private static string BuildRelationContext(
        string sourceDir,
        LocalRelationDataset dataset,
        IReadOnlyList<AiRelationDeviceAggregate> deviceAggregates,
        double? correlation)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Source directory: {sourceDir}");
        sb.AppendLine($"Memory JSON files: {dataset.MemoryFileCount} (parsed samples: {dataset.MemorySamples.Count})");
        sb.AppendLine($"AI JSON files: {dataset.AiFileCount} (parsed samples: {dataset.AiSamples.Count})");
        sb.AppendLine();
        sb.AppendLine("Device aggregates:");

        foreach (var d in deviceAggregates)
        {
            sb.AppendLine(
                $"- {d.DeviceType}: memory triad avg {d.AvgMemoryTriadGbps:F2} GB/s from {d.MemorySamples} sample(s); " +
                $"AI warm avg {d.AvgAiWarmTokensPerSecond:F2} tok/s from {d.AiSamples} sample(s)");
        }

        var theoreticalSamples = dataset.MemorySamples
            .Where(x => x.TheoreticalGbps.HasValue && x.MeasuredVsTheoreticalPercent.HasValue)
            .ToList();

        sb.AppendLine();
        if (theoreticalSamples.Count > 0)
        {
            sb.AppendLine("Measured vs theoretical memory bandwidth:");
            foreach (var s in theoreticalSamples.Take(8))
            {
                string speed = s.MemorySpeedMts.HasValue ? $"{s.MemorySpeedMts.Value:F0}" : "unknown";
                string width = s.BusWidthBits.HasValue ? $"{s.BusWidthBits.Value:F0}" : "unknown";
                sb.AppendLine(
                    $"- {s.DeviceType} ({s.Hostname}): measured {s.TriadMbps / 1000.0:F2} GB/s vs theoretical {s.TheoreticalGbps!.Value:F2} GB/s " +
                    $"at {speed} MT/s and {width}-bit => {s.MeasuredVsTheoreticalPercent!.Value:F1}%");
            }
            sb.AppendLine("Formula used: theoretical GB/s = speed(MT/s) × bus width(bits) ÷ 8 ÷ 1000.");
        }
        else
        {
            sb.AppendLine("Measured vs theoretical memory bandwidth: insufficient speed/width fields in JSON.");
        }

        sb.AppendLine();
        if (correlation.HasValue)
            sb.AppendLine($"Device-level Pearson correlation (memory vs AI warm tok/s): {correlation.Value:F3}");
        else
            sb.AppendLine("Device-level Pearson correlation: insufficient paired device data.");

        var topMem = dataset.MemorySamples.OrderByDescending(x => x.TriadMbps).FirstOrDefault();
        if (topMem is not null)
        {
            sb.AppendLine(
                $"Top memory sample: {topMem.DeviceType} {topMem.TriadMbps / 1000.0:F2} GB/s (host: {topMem.Hostname}, timestamp: {topMem.Timestamp})");
        }

        var topAi = dataset.AiSamples.OrderByDescending(x => x.WarmTokensPerSecond).FirstOrDefault();
        if (topAi is not null)
        {
            sb.AppendLine(
                $"Top AI warm sample: {topAi.DeviceType} {topAi.WarmTokensPerSecond:F2} tok/s (model alias: {topAi.ModelAlias}, timestamp: {topAi.Timestamp})");
        }

        return sb.ToString();
    }

    private static string BuildRelationPrompt(string context, string question)
    {
        return
            "You are a benchmark analyst.\n" +
            "Use only the data summary below.\n" +
            "If data is insufficient, explicitly state that.\n" +
            "Respond with one concise summary in 5-8 bullet points.\n\n" +
            "Question:\n" + question + "\n\n" +
            "Data summary:\n" + context;
    }

    private static (double? TheoreticalGbps, double? SpeedMts, double? BusWidthBits)
        EstimateTheoreticalMemoryBandwidth(JsonElement root)
    {
        if (!TryGetElement(root, out var memory, "memory") || memory.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        double? speedMts = TryGetDouble(memory, "configured_speed_mts")
                           ?? TryGetDouble(memory, "speed_mts");

        if (!speedMts.HasValue || speedMts.Value <= 0)
            return (null, speedMts, null);

        double busWidthBits = 0;

        if (memory.TryGetProperty("modules", out var modules)
            && modules.ValueKind == JsonValueKind.Array)
        {
            using var entries = modules.EnumerateArray();
            while (entries.MoveNext())
            {
                var module = entries.Current;
                double? bits = TryGetDouble(module, "total_width_bits")
                               ?? TryGetDouble(module, "data_width_bits");
                if (bits.HasValue && bits.Value > 0)
                    busWidthBits += bits.Value;
            }
        }

        if (busWidthBits <= 0)
        {
            double? modulesPopulated = TryGetDouble(memory, "modules_populated");
            if (modulesPopulated.HasValue && modulesPopulated.Value > 0)
                busWidthBits = modulesPopulated.Value * 64.0;
        }

        if (busWidthBits <= 0)
            return (null, speedMts, null);

        double theoreticalGbps = speedMts.Value * (busWidthBits / 8.0) / 1000.0;
        return (theoreticalGbps, speedMts, busWidthBits);
    }

    private static string NormalizeDeviceType(string? rawType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string fn = fileName.ToUpperInvariant();
            if (fn.Contains("_NPU_")) return "NPU";
            if (fn.Contains("_CPU_")) return "CPU";
            if (fn.Contains("_GPU_")) return "GPU";
        }

        string type = (rawType ?? "").Trim().ToUpperInvariant();
        if (type.Contains("NPU")) return "NPU";
        if (type.Contains("CPU")) return "CPU";
        if (type.Contains("GPU")) return "GPU";
        return "GPU";
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        if (!TryGetElement(element, out var value, path))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static double? TryGetDouble(JsonElement element, params string[] path)
    {
        if (!TryGetElement(element, out var value, path))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;

        return null;
    }

    private static bool TryGetElement(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private static List<string> ParseDeviceFilter(IEnumerable<string>? devices)
    {
        var all = new[] { "CPU", "GPU", "NPU" };
        if (devices is null)
            return [..all];

        var list = devices
            .Select(d => d.Trim().ToUpperInvariant())
            .Where(d => all.Contains(d))
            .Distinct()
            .ToList();

        return list.Count > 0 ? list : [..all];
    }

    /// <summary>Rough token count estimate: ~4 chars per token on average.</summary>
    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);

    // ── Spinner helper ────────────────────────────────────────────────────

    private static readonly char[] SpinFrames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private static async Task SpinAsync(string label, CancellationToken token)
    {
        int frame = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"\r{SpinFrames[frame % SpinFrames.Length]} {label}");
                Console.ResetColor();
                frame++;
                await Task.Delay(80, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.Write($"\r{new string(' ', label.Length + 3)}\r");
            Console.ResetColor();
        }
    }
}
#endif
