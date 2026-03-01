#if ENABLE_AI
// AiBenchmarkRunner.cs
// AI inference benchmark using Microsoft.AI.Foundry.Local.
//
// Measures response time and tokens/second for two prompts on each
// hardware device (CPU, GPU, NPU) using the local Foundry service:
//
//   Q1 "What is DRIPS in Windows?"        — cold run (includes model load)
//   Q2 "How to improve DRIPS% on Windows?" — warm run (model already loaded)
//
// Results are displayed as formatted tables and saved as JSON when --ai is used.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
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
    // The runner falls back automatically when a preferred alias is unavailable.
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
        "qwen2.5-1.5b",
        "phi-3.5-mini",
        "qwen2.5-7b",
        "qwen2.5-0.5b",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
        "deepseek-r1-7b",
        "deepseek-r1-14b",
    ];

    // Shared-model priorities used when benchmarking multiple devices side-by-side.
    // Goal: maximize coverage across selected devices while preferring stronger common aliases.
    private static readonly string[] SharedAliasPriority =
    [
        "qwen2.5-1.5b",
        "phi-3.5-mini",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
        "deepseek-r1-7b",
        "deepseek-r1-14b",
    ];

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// Runs the AI inference benchmark on the requested device(s).
    /// Returns one result per successfully benchmarked device.
    /// </summary>
    /// <param name="devices">
    /// Devices to test: null or empty = all with available models.
    /// Accepted values (case-insensitive): "cpu", "gpu", "npu".
    /// </param>
    /// <param name="modelAlias">
    /// Specific model alias to use (e.g. "phi-3.5-mini").
    /// When null the runner picks the smallest available model per device.
    /// </param>
    public static async Task<List<AiDeviceBenchmarkResult>> RunAsync(
        IEnumerable<string>? devices = null,
        string? modelAlias = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AiDeviceBenchmarkResult>();

        FoundryLocalManager manager;
        try
        {
            manager = FoundryLocalManager.Instance;
        }
        catch (FoundryLocalException)
        {
            await FoundryLocalManager.CreateAsync(
                new Configuration
                {
                    AppName = "StreamBench",
                    Web = new Configuration.WebService
                    {
                        Urls = "http://127.0.0.1:5273"
                    }
                },
                NullLogger.Instance,
                cancellationToken);
            manager = FoundryLocalManager.Instance;
        }

        ConsoleOutput.WriteMarkup("[bold cyan]Starting Microsoft AI Foundry Local service...[/]");

        try
        {
            await manager.StartWebServiceAsync(cancellationToken);

            var catalog = await manager.GetCatalogAsync(cancellationToken);
            var allVariants = await TryListModelsAsync(catalog, cancellationToken);

            if (allVariants.Count == 0)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No models found in catalog. " +
                    "Ensure Foundry Local is installed and models are downloaded.");
                return results;
            }

            var targetDevices = ParseDeviceFilter(devices);
            string? effectiveAlias = modelAlias;
            bool strictAlias = false;

            // For multi-device side-by-side comparison, auto-pick and retry shared aliases
            // so all devices use the same model whenever possible.
            if (string.IsNullOrWhiteSpace(effectiveAlias) && targetDevices.Count > 1)
            {
                var sharedCandidates = SelectSharedAliasCandidates(allVariants, targetDevices);
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
                            ModelVariant? variant = await FindBestVariantAsync(
                                catalog, allVariants, deviceType, sharedAlias, strictAlias, cancellationToken);

                            if (variant is null)
                            {
                                ConsoleOutput.WriteMarkup(
                                    $"[yellow][SKIP][/] Shared alias [white]{sharedAlias}[/] not available for [white]{deviceType}[/].");
                                continue;
                            }

                            Console.WriteLine();
                            ConsoleOutput.WriteMarkup(
                                $"[bold cyan]── AI Benchmark: {deviceType} ({variant.Id}) ──[/]");

                            var result = await BenchmarkVariantAsync(variant, deviceType, cancellationToken);
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
                        {
                            return attemptResults;
                        }

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
                // If user provided alias explicitly, do not silently switch models.
                strictAlias = true;
            }

            foreach (var deviceType in targetDevices)
            {
                ModelVariant? variant = await FindBestVariantAsync(
                    catalog, allVariants, deviceType, effectiveAlias, strictAlias, cancellationToken);

                if (variant is null)
                {
                    ConsoleOutput.WriteMarkup(
                        $"[yellow][SKIP][/] No model available for [white]{deviceType}[/].");
                    continue;
                }

                Console.WriteLine();
                ConsoleOutput.WriteMarkup(
                    $"[bold cyan]── AI Benchmark: {deviceType} ({variant.Id}) ──[/]");

                var result = await BenchmarkVariantAsync(variant, deviceType, cancellationToken);
                if (result is not null)
                    results.Add(result);
            }
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup($"[red]Error:[/] Foundry Local service error: {ex.Message}");
        }
        finally
        {
            // Always unload models and stop the service to release resources
            try { await manager.StopWebServiceAsync(CancellationToken.None); } catch { }
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

        FoundryLocalManager manager;
        try
        {
            manager = FoundryLocalManager.Instance;
        }
        catch (FoundryLocalException)
        {
            await FoundryLocalManager.CreateAsync(
                new Configuration
                {
                    AppName = "StreamBench",
                    Web = new Configuration.WebService
                    {
                        Urls = "http://127.0.0.1:5273"
                    }
                },
                NullLogger.Instance,
                cancellationToken);
            manager = FoundryLocalManager.Instance;
        }

        ConsoleOutput.WriteMarkup("[bold cyan]Running local-AI relation summary from saved JSON files...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Source folder: {sourceDir}[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Files: {dataset.MemoryFileCount} memory JSON, {dataset.AiFileCount} AI JSON[/]");

        ModelVariant? variant = null;
        try
        {
            await manager.StartWebServiceAsync(cancellationToken);

            var catalog = await manager.GetCatalogAsync(cancellationToken);
            var allVariants = await TryListModelsAsync(catalog, cancellationToken);
            if (allVariants.Count == 0)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No local AI models available for relation summary.");
                return null;
            }

            bool strictAlias = !string.IsNullOrWhiteSpace(modelAlias);
            variant = await FindBestVariantAsync(
                catalog, allVariants, "CPU", modelAlias, strictAlias, cancellationToken);

            if (variant is null)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No CPU model available for local relation summary.");
                return null;
            }

            bool cached = await variant.IsCachedAsync(cancellationToken);
            if (!cached)
            {
                ConsoleOutput.WriteMarkup(
                    $"[dim]  Downloading {variant.Info.Id} ({variant.Info.FileSizeMb:F0} MB)...[/]");
                using var cts = new CancellationTokenSource();
                var spinTask = SpinAsync($"Downloading {variant.Info.Id}", cts.Token);
                await variant.DownloadAsync(_ => { }, cancellationToken);
                await cts.CancelAsync();
                await spinTask;
            }

            ConsoleOutput.WriteMarkup($"[dim]  Loading summary model: {variant.Info.Id}[/]");
            await variant.LoadAsync(cancellationToken);

            var chatClient = await variant.GetChatClientAsync(cancellationToken);
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
                    chatClient,
                    prompt,
                    modelLoadSec: 0,
                    ct: cancellationToken);

                if (run is null)
                    return null;

                answers.Add(new AiRelationQuestionAnswer(
                    Index: i + 1,
                    Question: question,
                    Answer: run.ResponseText.Trim()));
            }

            return new AiLocalRelationSummaryResult(
                SourceDirectory: sourceDir,
                MemoryJsonFiles: dataset.MemoryFileCount,
                AiJsonFiles: dataset.AiFileCount,
                MemorySamples: dataset.MemorySamples.Count,
                AiSamples: dataset.AiSamples.Count,
                ModelId: variant.Info.Id,
                ModelAlias: variant.Alias,
                ExecutionProvider: variant.Info.Runtime?.ExecutionProvider ?? "unknown",
                DeviceLevelCorrelation: deviceCorrelation,
                DeviceAggregates: deviceAggregates,
                Questions: answers,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup($"[red]Error:[/] Local relation summary failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (variant is not null)
                await TryUnloadAsync(variant);
            try { await manager.StopWebServiceAsync(CancellationToken.None); } catch { }
        }
    }

    // ── Core benchmark for a single variant ──────────────────────────────

    private static async Task<AiDeviceBenchmarkResult?> BenchmarkVariantAsync(
        ModelVariant variant,
        string deviceLabel,
        CancellationToken ct)
    {
        var info = variant.Info;

        // ── Download if not already cached ────────────────────────────────
        bool cached = await variant.IsCachedAsync(ct);
        if (!cached)
        {
            ConsoleOutput.WriteMarkup(
                $"[dim]  Downloading {info.Id} ({info.FileSizeMb:F0} MB)...[/]");

            using var cts = new CancellationTokenSource();
            var spinTask = SpinAsync($"Downloading {info.Id}", cts.Token);

            await variant.DownloadAsync(
                progress => { /* progress reported via spinner */ },
                ct);

            await cts.CancelAsync();
            await spinTask;
        }

        // ── Time model loading ────────────────────────────────────────────
        ConsoleOutput.WriteMarkup("[dim]  Loading model...[/]");
        var loadSw = Stopwatch.StartNew();
        try
        {
            await variant.LoadAsync(ct);
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup($"[red]  Failed to load {info.Id}:[/] {ex.Message}");
            return null;
        }
        loadSw.Stop();
        double modelLoadSec = loadSw.Elapsed.TotalSeconds;
        ConsoleOutput.WriteMarkup(
            $"[dim]  Model loaded in {modelLoadSec:F2} s[/]");

        OpenAIChatClient chatClient;
        try
        {
            chatClient = await variant.GetChatClientAsync(ct);
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup($"[red]  Failed to get chat client:[/] {ex.Message}");
            await TryUnloadAsync(variant);
            return null;
        }

        try
        {
            // ── Q1: first inference (cold — model just loaded) ─────────────
            ConsoleOutput.WriteMarkup($"[dim]  Q1: {Q1}[/]");
            AiInferenceRun? run1 = await RunInferenceAsync(
                chatClient, Q1, modelLoadSec, ct);

            if (run1 is null)
            {
                ConsoleOutput.WriteMarkup("[red]  Q1 inference failed.[/]");
                return null;
            }

            // ── Q2: second inference (warm — model already in memory) ──────
            ConsoleOutput.WriteMarkup($"[dim]  Q2: {Q2}[/]");
            AiInferenceRun? run2 = await RunInferenceAsync(
                chatClient, Q2, modelLoadSec: 0, ct);

            if (run2 is null)
            {
                ConsoleOutput.WriteMarkup("[red]  Q2 inference failed.[/]");
                return null;
            }

            return new AiDeviceBenchmarkResult(
                DeviceType:        deviceLabel,
                ModelId:           info.Id,
                ModelAlias:        variant.Alias,
                ExecutionProvider: info.Runtime?.ExecutionProvider ?? "unknown",
                Question1:         Q1,
                Run1:              run1,
                Question2:         Q2,
                Run2:              run2,
                Timestamp:         DateTime.UtcNow.ToString("O"));
        }
        finally
        {
            // Always unload the model after benchmarking to free device memory
            await TryUnloadAsync(variant);
        }
    }

    // ── Single inference run ──────────────────────────────────────────────

    private static async Task<AiInferenceRun?> RunInferenceAsync(
        OpenAIChatClient chatClient,
        string prompt,
        double modelLoadSec,
        CancellationToken ct)
    {
        var messages = new[] { ChatMessage.FromUser(prompt) };
        var sw = Stopwatch.StartNew();
        Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels.ChatCompletionCreateResponse? response;

        try
        {
            response = await chatClient.CompleteChatAsync(messages, ct);
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteMarkup($"[red]  Inference error:[/] {ex.Message}");
            return null;
        }

        sw.Stop();
        double responseSec = sw.Elapsed.TotalSeconds;

        if (response is null || !response.Successful)
        {
            ConsoleOutput.WriteMarkup(
                $"[red]  Inference failed:[/] {response?.Error?.Message ?? "null response"}");
            return null;
        }

        string content = response.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        int completionTokens = response.Usage?.CompletionTokens ?? EstimateTokens(content);
        int promptTokens = response.Usage?.PromptTokens ?? 0;
        double tokensPerSec = responseSec > 0 ? completionTokens / responseSec : 0;
        string preview = content.Length > 200 ? content[..200] + "…" : content;

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

    // ── Model discovery ───────────────────────────────────────────────────

    /// <summary>
    /// Finds the best model variant for the given device type.
    /// Preference: user-specified alias → preferred alias order → any cached → smallest available.
    /// </summary>
    private static async Task<ModelVariant?> FindBestVariantAsync(
        ICatalog catalog,
        List<ModelVariant> allVariants,
        string deviceLabel,
        string? aliasHint,
        bool strictAlias,
        CancellationToken ct)
    {
        DeviceType targetType = DeviceLabelToEnum(deviceLabel);
        var preferredAliases = GetPreferredAliases(deviceLabel);

        // Filter to variants matching the target device
        var candidates = allVariants
            .Where(v => v.Info?.Runtime?.DeviceType == targetType
                     && (v.Info?.Task?.Contains("chat", StringComparison.OrdinalIgnoreCase) ?? true))
            .ToList();

        if (candidates.Count == 0)
            return null;

        // User-specified alias takes priority
        if (!string.IsNullOrWhiteSpace(aliasHint))
        {
            var match = candidates.FirstOrDefault(v =>
                v.Alias.Equals(aliasHint, StringComparison.OrdinalIgnoreCase)
                || v.Id.Equals(aliasHint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;

            ConsoleOutput.WriteMarkup(
                $"[yellow][WARN][/] Model '{aliasHint}' not found for {deviceLabel}; falling back to preferred list.");

            if (strictAlias)
                return null;
        }

        // Cache index for fallback heuristics
        var cached = await TryCachedModelsAsync(catalog, ct);
        var cachedIds = new HashSet<string>(
            cached.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);

        // 1. Preferred aliases that are already cached
        foreach (var alias in preferredAliases)
        {
            var cachedPreferred = candidates.FirstOrDefault(c =>
                (c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
                && cachedIds.Contains(c.Id));
            if (cachedPreferred is not null) return cachedPreferred;
        }

        // 2. Any cached model for this device (prefer smaller model)
        var anyCached = candidates
            .Where(c => cachedIds.Contains(c.Id))
            .OrderBy(c => c.Info?.FileSizeMb ?? double.MaxValue)
            .FirstOrDefault();
        if (anyCached is not null) return anyCached;

        // 3. Preferred aliases in configured order (download if needed)
        foreach (var alias in preferredAliases)
        {
            var v = candidates.FirstOrDefault(c =>
                c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase));
            if (v is not null) return v;
        }

        // 4. Smallest available model
        return candidates
            .OrderBy(c => c.Info?.FileSizeMb ?? double.MaxValue)
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
        IReadOnlyList<ModelVariant> allVariants,
        IReadOnlyList<string> targetDevices)
    {
        if (targetDevices.Count == 0)
            return [];

        var targetSet = new HashSet<string>(targetDevices.Select(d => d.ToUpperInvariant()));

        var byAlias = allVariants
            .Where(v => !string.IsNullOrWhiteSpace(v.Alias)
                     && (v.Info?.Task?.Contains("chat", StringComparison.OrdinalIgnoreCase) ?? true))
            .GroupBy(v => v.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var deviceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in g)
                {
                    string? device = v.Info?.Runtime?.DeviceType.ToString();
                    if (!string.IsNullOrWhiteSpace(device))
                        deviceSet.Add(device.ToUpperInvariant());
                }

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
            .OrderBy(x => PriorityIndex(x.Alias))
            .ThenByDescending(x => x.Coverage)
            .ThenBy(x => x.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Alias)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a flat list of all ModelVariant objects from the catalog.
    /// Falls back to GetCachedModelsAsync if ListModelsAsync is unavailable.
    /// </summary>
    private static async Task<List<ModelVariant>> TryListModelsAsync(
        ICatalog catalog, CancellationToken ct)
    {
        try
        {
            var models = await catalog.ListModelsAsync(ct);
            return models
                ?.SelectMany(m => m.Variants ?? [])
                .ToList() ?? [];
        }
        catch
        {
            // Service offline or catalog unavailable — use locally cached models
            return await TryCachedModelsAsync(catalog, ct);
        }
    }

    private static async Task<List<ModelVariant>> TryCachedModelsAsync(
        ICatalog catalog, CancellationToken ct)
    {
        try   { return await catalog.GetCachedModelsAsync(ct) ?? []; }
        catch { return []; }
    }

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

    private static DeviceType DeviceLabelToEnum(string label) =>
        label.ToUpperInvariant() switch
        {
            "CPU" => DeviceType.CPU,
            "GPU" => DeviceType.GPU,
            "NPU" => DeviceType.NPU,
            _     => DeviceType.CPU,
        };

    private static async Task TryUnloadAsync(ModelVariant variant)
    {
        try { await variant.UnloadAsync(CancellationToken.None); } catch { }
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
