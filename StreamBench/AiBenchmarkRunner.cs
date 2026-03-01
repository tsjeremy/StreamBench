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

    // Preferred aliases by device (quality-focused defaults).
    // The runner falls back automatically when a preferred alias is unavailable.
    private static readonly string[] PreferredAliasesCpu =
    [
        "deepseek-r1-14b",
        "phi-4-mini-reasoning",
        "gpt-oss-20b",
        "qwen2.5-14b",
        "phi-4",
        "phi-4-mini",
        "qwen2.5-7b",
        "phi-3.5-mini",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
    ];

    private static readonly string[] PreferredAliasesGpu =
    [
        "deepseek-r1-14b",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "phi-4-mini",
        "phi-3.5-mini",
    ];

    private static readonly string[] PreferredAliasesNpu =
    [
        "deepseek-r1-14b",
        "qwen2.5-7b",
        "qwen2.5-1.5b",
        "phi-3.5-mini",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
    ];

    // Shared-model priorities used when benchmarking multiple devices side-by-side.
    // Goal: maximize coverage across selected devices while preferring stronger common aliases.
    private static readonly string[] SharedAliasPriority =
    [
        "deepseek-r1-14b",
        "qwen2.5-1.5b",
        "qwen2.5-7b",
        "qwen2.5-0.5b",
        "phi-3.5-mini",
        "phi-3-mini-128k",
        "phi-3-mini-4k",
        "deepseek-r1-7b",
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

        // 1. Preferred aliases in configured order (download if needed)
        foreach (var alias in preferredAliases)
        {
            var v = candidates.FirstOrDefault(c =>
                c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase));
            if (v is not null) return v;
        }

        // 2. Any cached model for this device
        var anyCached = candidates.FirstOrDefault(c => cachedIds.Contains(c.Id));
        if (anyCached is not null) return anyCached;

        // 3. Smallest available model
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
