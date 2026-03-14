#if ENABLE_AI
// AiBenchmarkRunner.cs
// AI inference benchmark using pluggable backends (Foundry Local, LM Studio, etc.)
// via IAiBackend abstraction + IChatClient from Microsoft.Extensions.AI.
//
// Measures response time and tokens/second for two prompts on each
// hardware device (CPU, GPU, NPU) using the configured AI backend:
//
//   Q1 "Hello World!"                                        — cold run (includes model load)
//   Q2 "How to calculate memory bandwidth on different memory?" — warm run (model already loaded)
//
// Results are displayed as formatted tables and saved as JSON when --ai is used.
//
// Trace events are emitted via TraceLog for diagnostics.
// All exceptions include source file/line via DiagnosticHelper.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using StreamBench.Models;

namespace StreamBench;

public static class AiBenchmarkRunner
{
    // ── Benchmark prompts ─────────────────────────────────────────────────
    public const string Q1 = "Hello World!";
    public const string Q2 = "How to calculate memory bandwidth on different memory?";
    public const string Q3Label = "Summarize local memory bandwidth and AI benchmark results from saved JSON files.";
    public const string Q3 = "Based on all local JSON files in this folder (including files from other devices), summarize memory bandwidth and AI benchmark relationship, highlight the best combined profile and also try to explain the % from memory bandwidth benchmark result vs. the theoretical bandwidth calculation of the memory on the device.";
    public static readonly string[] RelationQuestions =
    [
        Q1,
        Q2,
        Q3
    ];

    private const int Q1MaxOutputTokens = 128;
    private const int DetailedAnswerMaxOutputTokens = 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── IChatClient creation ─────────────────────────────────────────────

    /// <summary>
    /// Creates an IChatClient pointing at the backend's OpenAI-compatible endpoint.
    /// Both Foundry and LM Studio expose /v1/chat/completions.
    /// Uses DirectOpenAiChatClient (raw HttpClient) instead of the OpenAI SDK to
    /// avoid deserialization bugs with non-OpenAI backends that return
    /// "tool_calls": [] in their responses.
    /// </summary>
    private static IChatClient CreateChatClient(string serviceUrl, string? modelId = null)
    {
        return new DirectOpenAiChatClient(serviceUrl, modelId ?? "default");
    }

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// Session context returned by RunAsync for reuse by the relation summary.
    /// Avoids redundant service start and catalog reload.
    /// Caller should call <see cref="StopSessionAsync"/> when done.
    /// </summary>
    internal sealed class AiSession
    {
        public IAiBackend Backend { get; init; } = null!;
        public string BackendName { get; init; } = "AI";
        public string ServiceUrl { get; set; } = "";
        public List<AiModelInfo> Catalog { get; init; } = [];

        public async Task StopAsync()
        {
            await Backend.StopAsync();
        }
    }

    /// <summary>
    /// Runs the AI inference benchmark on the requested device(s).
    /// Returns a two-pass result: shared model comparison + best-per-device performance.
    /// The returned <see cref="AiSession"/> can be passed to <see cref="RunLocalRelationSummaryAsync"/>
    /// to avoid a redundant service restart.
    /// Caller should call <c>session.StopAsync()</c> after all AI work is complete.
    /// When sharedOnly is true, the best-per-device pass is skipped.
    /// When noDownload is true, only already-cached models are used.
    /// </summary>
    internal static Task<(AiBenchmarkTwoPassResult Result, AiSession? Session)> RunAsync(
        AiExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            options.DevicesOrDefault,
            options.ModelAlias,
            options.SharedOnly,
            options.NoDownload,
            options.QuickMode,
            options.BackendType,
            cancellationToken);
    }

    internal static async Task<(AiBenchmarkTwoPassResult Result, AiSession? Session)> RunAsync(
        IEnumerable<string>? devices = null,
        string? modelAlias = null,
        bool sharedOnly = false,
        bool noDownload = false,
        bool quickMode = false,
        AiBackendType backendType = AiBackendType.Auto,
        CancellationToken cancellationToken = default)
    {
        // Defence-in-depth: prevent sleep even when called outside the normal Program.cs flow.
        using var _sleep = SleepPreventer.Acquire();

        var sharedResults = new List<AiDeviceBenchmarkResult>();
        var bestPerDeviceResults = new List<AiDeviceBenchmarkResult>();

        // Create backend via factory
        var config = AiBackendConfig.Load();
        if (backendType != AiBackendType.Auto)
            config = config with { Backend = backendType };

        var backend = AiBackendFactory.Create(config);
        TraceLog.DiagnosticInfo($"AI backend selected: {backend.Name}");

        if (!backend.IsAvailable())
        {
            ConsoleOutput.WriteMarkup($"[red][FAIL][/] {backend.Name} is not available.");
            ConsoleOutput.WriteMarkup($"[dim]  {AiBackendFactory.GetInstallInstructions(config.Backend)}[/]");
            return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), null);
        }

        string? serviceUrl = await backend.StartAsync(cancellationToken);
        if (serviceUrl is null)
        {
            ConsoleOutput.WriteMarkup($"[red][FAIL][/] Cannot start {backend.Name} service.");
            return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), null);
        }

        TraceLog.DiagnosticInfo($"{backend.Name} service URL: {serviceUrl}");
        ConsoleOutput.WriteMarkup($"[bold cyan]Starting {backend.Name} AI service...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Service URL: {serviceUrl}[/]");

        var targetDevices = ParseDeviceFilter(devices).ToList();
        var sharedPassDevices = targetDevices.ToList();

        // For backends without device targeting, simplify to single pass
        if (!backend.SupportsDeviceTargeting)
        {
            sharedPassDevices = ["GPU"]; // LM Studio uses GPU primarily
            targetDevices = ["GPU"];
            TraceLog.DiagnosticInfo($"{backend.Name} does not support device targeting; using single-device mode");
        }

        // Quick mode (--quick-ai): cached models only, skip shared pass, 1 model per device.
        // Apply this before catalog bootstrap so a clean machine does not trigger a download
        // that the quick path will immediately refuse to use.
        if (quickMode)
        {
            noDownload = true;
            sharedOnly = false;
            TraceLog.DiagnosticInfo("Quick mode: noDownload=true, skipping shared pass");
            ConsoleOutput.WriteMarkup("[dim]  Quick mode: using cached models only, 1 model per device.[/]");
        }

        List<AiModelInfo> allModels = [];
        try
        {
            // Bootstrap catalog (backend-specific: Foundry does EP download, LM Studio queries /v1/models)
            if (backend is FoundryAiBackend foundryBackend)
            {
                allModels = await foundryBackend.BootstrapCatalogAsync(noDownload, cancellationToken);
            }
            else
            {
                allModels = await backend.ListModelsAsync(cancellationToken);

                // LM Studio: if no models loaded, suggest loading one
                if (allModels.Count == 0)
                {
                    ConsoleOutput.WriteMarkup("[yellow][INFO][/] No models currently loaded in LM Studio.");
                    ConsoleOutput.WriteMarkup("[dim]  Please open LM Studio and load a model (e.g. phi-3.5-mini).[/]");
                    ConsoleOutput.WriteMarkup("[dim]  Or use: lms load phi-3.5-mini[/]");

                    // Try auto-loading recommended model
                    var loaded = await backend.LoadModelAsync("phi-3.5-mini", cancellationToken);
                    if (loaded is not null)
                    {
                        allModels = await backend.ListModelsAsync(cancellationToken);
                    }
                }
            }

            if (allModels.Count == 0)
            {
                TraceLog.AiCatalogUnavailable("No models found in catalog after retry and bootstrap");
                ConsoleOutput.WriteMarkup($"[yellow][WARN][/] No models found in {backend.Name} catalog.");
                ConsoleOutput.WriteMarkup($"[dim]  For {backend.Name}: ensure a model is loaded and the service is running.[/]");
                await backend.StopAsync(cancellationToken);
                return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), null);
            }

            // Log a summary of device types available in the catalog
            LogCatalogDeviceTypes(allModels);

            TraceLog.DiagnosticInfo($"Target devices: {string.Join(", ", targetDevices)}, noDownload: {noDownload}, sharedOnly: {sharedOnly}, quickMode: {quickMode}");

            // Drop target devices that have no compatible models in the catalog —
            // avoids wasting time trying shared aliases for devices the backend cannot run.
            // Only relevant for backends that support device targeting.
            if (backend.SupportsDeviceTargeting)
            {
                var catalogDeviceTypes = allModels
                    .Select(m => NormalizeDeviceType(m.DeviceType, null))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var deviceType in targetDevices.ToList())
                {
                    if (catalogDeviceTypes.Contains(deviceType))
                        continue;

                    sharedPassDevices.RemoveAll(d => d.Equals(deviceType, StringComparison.OrdinalIgnoreCase));
                    targetDevices.RemoveAll(d => d.Equals(deviceType, StringComparison.OrdinalIgnoreCase));
                    TraceLog.DiagnosticInfo($"{deviceType} removed from target devices — no compatible models in catalog");

                    if (deviceType.Equals("NPU", StringComparison.OrdinalIgnoreCase))
                    {
                        // Probe for NPU hardware to provide a better diagnostic message
                        var npuHwInfo = SystemInfoDetector.DetectNpuHardware();
                        if (npuHwInfo is not null)
                        {
                            TraceLog.NpuHardwareDetected(npuHwInfo);
                            ConsoleOutput.WriteMarkup(
                                $"[yellow][WARN][/] NPU hardware detected ([white]{npuHwInfo}[/]) but no compatible AI models found in catalog.");
                            ConsoleOutput.WriteMarkup($"[dim]  Try updating {backend.Name} or loading a compatible model.[/]");
                        }
                        else
                        {
                            TraceLog.NpuHardwareNotDetected();
                            ConsoleOutput.WriteMarkup(
                                "[yellow][INFO][/] No NPU hardware detected — skipping NPU benchmark.");
                            ConsoleOutput.WriteMarkup(
                                "[dim]  NPU benchmarks require Intel Core Ultra (AI Boost) or Qualcomm Snapdragon X (Hexagon NPU).[/]");
                        }
                    }
                    else
                    {
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][INFO][/] No compatible [white]{deviceType}[/] models found in {backend.Name} catalog — skipping {deviceType} benchmark.");
                    }
                }
            }

            string? effectiveAlias = modelAlias;
            bool strictAlias = false;
            bool allowNpuFailFast = string.IsNullOrWhiteSpace(modelAlias) && targetDevices.Count > 1;

            // ── Pass 1: Multi-device side-by-side comparison with shared model ──
            if (!quickMode && string.IsNullOrWhiteSpace(effectiveAlias) && sharedPassDevices.Count > 1)
            {
                var sharedCandidates = SelectSharedAliasCandidates(allModels, sharedPassDevices, backend);
                TraceLog.DiagnosticInfo($"Shared model candidates: {string.Join(", ", sharedCandidates)}");
                if (sharedCandidates.Count > 0)
                {
                    strictAlias = true;
                    TraceLog.AiPassStarted("shared", sharedPassDevices.Count);

                    // Determine whether any shared candidates are already cached —
                    // if so, prefer cached models in the shared pass to avoid downloads.
                    bool sharedHasCachedModels = sharedCandidates.Any(alias =>
                        allModels.Any(m => m.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && m.IsCached));
                    bool sharedNoDownload = noDownload || sharedHasCachedModels;
                    if (sharedHasCachedModels && !noDownload)
                    {
                        TraceLog.DiagnosticInfo("Shared pass: cached models available, skipping downloads for shared candidates");
                        ConsoleOutput.WriteMarkup("[dim]  Cached models available — preferring cached models for shared comparison.[/]");
                    }

                    const int MaxSharedAttempts = 5;
                    var sharedTimeBudget = TimeSpan.FromMinutes(10);
                    var sharedPassTimer = Stopwatch.StartNew();
                    int sharedAttempt = 0;
                    int consecutiveFailures = 0;
                    bool serviceRestarted = false;
                    int bestSuccessCount = -1;
                    List<AiDeviceBenchmarkResult> bestAttemptResults = [];

                    foreach (var sharedAlias in sharedCandidates)
                    {
                        // Guard: stop after MaxSharedAttempts or when time budget is exhausted
                        if (++sharedAttempt > MaxSharedAttempts)
                        {
                            TraceLog.DiagnosticInfo($"Shared pass: reached max {MaxSharedAttempts} attempts, stopping");
                            ConsoleOutput.WriteMarkup(
                                $"[yellow][INFO][/] Shared pass: tried {MaxSharedAttempts} models — moving on.");
                            break;
                        }
                        if (sharedPassTimer.Elapsed > sharedTimeBudget)
                        {
                            TraceLog.DiagnosticInfo($"Shared pass: time budget ({sharedTimeBudget.TotalMinutes:0} min) exhausted after {sharedAttempt - 1} attempts");
                            ConsoleOutput.WriteMarkup(
                                $"[yellow][INFO][/] Shared pass: time budget exhausted ({sharedPassTimer.Elapsed.TotalMinutes:0.0} min) — moving on.");
                            break;
                        }

                        ConsoleOutput.WriteMarkup(
                            $"[bold cyan]Pass 1 — Trying shared model for side-by-side comparison:[/] [white]{sharedAlias}[/]");

                        var attemptResults = new List<AiDeviceBenchmarkResult>();
                        int successCount = 0;

                        foreach (var deviceType in sharedPassDevices.ToList())
                        {
                            var model = FindBestModel(backend, allModels, deviceType, sharedAlias, strictAlias);
                            if (model is null)
                            {
                                TraceLog.DiagnosticInfo($"Shared alias '{sharedAlias}' not available for {deviceType}");
                                ConsoleOutput.WriteMarkup(
                                    $"[yellow][SKIP][/] Shared alias [white]{sharedAlias}[/] not available for [white]{deviceType}[/].");
                                continue;
                            }

                            Console.WriteLine();
                            TraceLog.AiBenchmarkDeviceStarted(deviceType, model.Id);
                            ConsoleOutput.WriteMarkup(
                                $"[bold cyan]── Pass 1: AI Benchmark: {deviceType} ({model.Id}) ──[/]");

                            var devSw = Stopwatch.StartNew();
                            var result = await BenchmarkModelAsync(
                                backend, serviceUrl, model, deviceType, sharedNoDownload, cancellationToken);
                            devSw.Stop();
                            if (result is not null)
                            {
                                attemptResults.Add(result with { BenchmarkPass = "shared" });
                                successCount++;
                                consecutiveFailures = 0;
                                TraceLog.AiBenchmarkDeviceCompleted(deviceType, model.Id, devSw.ElapsedMilliseconds);
                            }
                            else
                            {
                                consecutiveFailures++;

                                if (allowNpuFailFast && deviceType.Equals("NPU", StringComparison.OrdinalIgnoreCase))
                                {
                                    sharedPassDevices.RemoveAll(d => d.Equals("NPU", StringComparison.OrdinalIgnoreCase));
                                    allowNpuFailFast = false;
                                    TraceLog.DiagnosticInfo("NPU removed from shared-model comparison after first model-load failure");
                                    ConsoleOutput.WriteMarkup(sharedOnly
                                        ? "[yellow][WARN][/] NPU model load failed in shared-model pass; continuing with CPU/GPU for this shared-only run."
                                        : "[yellow][WARN][/] NPU model load failed in shared-model pass; continuing with CPU/GPU for now and retrying NPU in per-device benchmarking.");
                                }

                                // Detect service crash: 2 consecutive failures → try restart once
                                if (consecutiveFailures >= 2 && !serviceRestarted && backend is FoundryAiBackend foundryRestart)
                                {
                                    TraceLog.Warn("2 consecutive failures in shared pass — attempting service restart");
                                    ConsoleOutput.WriteMarkup($"[yellow][WARN][/] Multiple failures detected — restarting {backend.Name} service...");
                                    var newUrl = await foundryRestart.RestartServiceAsync();
                                    if (newUrl is not null)
                                    {
                                        serviceUrl = newUrl;
                                        TraceLog.DiagnosticInfo($"Service restarted, new URL: {serviceUrl}");
                                        ConsoleOutput.WriteMarkup($"[dim]  Service restarted: {serviceUrl}[/]");
                                    }
                                    serviceRestarted = true;
                                    consecutiveFailures = 0;
                                }

                                // Bail from shared pass entirely after 3+ consecutive failures
                                if (consecutiveFailures >= 3)
                                {
                                    TraceLog.Warn("3 consecutive failures — aborting shared pass");
                                    ConsoleOutput.WriteMarkup("[yellow][WARN][/] Too many consecutive failures — stopping shared pass.");
                                    goto EndSharedPass;
                                }
                            }
                        }

                        TraceLog.AiSharedModelAttempt(sharedAlias, successCount, sharedPassDevices.Count);

                        if (successCount > bestSuccessCount)
                        {
                            bestSuccessCount = successCount;
                            bestAttemptResults = attemptResults;
                        }

                        if (successCount == sharedPassDevices.Count)
                        {
                            sharedResults = attemptResults;
                            TraceLog.AiPassCompleted("shared", successCount, sharedPassDevices.Count);
                            break;
                        }

                        ConsoleOutput.WriteMarkup(
                            $"[yellow][WARN][/] Shared alias [white]{sharedAlias}[/] covered [white]{successCount}/{sharedPassDevices.Count}[/] devices; trying next candidate.");
                    }

                    EndSharedPass:

                    if (sharedResults.Count == 0 && bestAttemptResults.Count > 0)
                    {
                        TraceLog.DiagnosticInfo($"No full coverage; using best partial: {bestAttemptResults.Count}/{sharedPassDevices.Count}");
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][WARN][/] No single shared model covered all selected devices; using best coverage [white]{bestAttemptResults.Count}/{sharedPassDevices.Count}[/].");
                        sharedResults = bestAttemptResults;
                        TraceLog.AiPassCompleted("shared", bestAttemptResults.Count, sharedPassDevices.Count);
                    }

                    if (sharedResults.Count == 0)
                    {
                        TraceLog.Warn("No shared alias produced results; falling back to per-device defaults");
                        ConsoleOutput.WriteMarkup(
                            "[yellow][WARN][/] No shared alias produced successful results; falling back to per-device defaults.");
                        strictAlias = false;
                        effectiveAlias = null;
                    }
                }
                else
                {
                    TraceLog.Warn("No shared alias candidates found; using per-device defaults");
                    ConsoleOutput.WriteMarkup(
                        "[yellow][WARN][/] No shared alias found across selected devices; using per-device defaults.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(effectiveAlias))
            {
                strictAlias = true;
            }

            // If shared pass didn't produce results (single device or no shared model),
            // run per-device as the primary pass and tag as "shared" for compatibility
            if (sharedResults.Count == 0)
            {
                TraceLog.AiPassStarted("per-device-fallback", targetDevices.Count);
                foreach (var deviceType in targetDevices)
                {
                    var model = FindBestModel(backend, allModels, deviceType, effectiveAlias, strictAlias);
                    if (model is null)
                    {
                        TraceLog.DiagnosticInfo($"No model available for {deviceType}");
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][SKIP][/] No model available for [white]{deviceType}[/].");
                        continue;
                    }

                    Console.WriteLine();
                    TraceLog.AiBenchmarkDeviceStarted(deviceType, model.Id);
                    ConsoleOutput.WriteMarkup(
                        $"[bold cyan]── AI Benchmark: {deviceType} ({model.Id}) ──[/]");

                    var devSw = Stopwatch.StartNew();
                    var result = await BenchmarkModelAsync(
                        backend, serviceUrl, model, deviceType, noDownload, cancellationToken);
                    devSw.Stop();
                    if (result is not null)
                    {
                        sharedResults.Add(result with { BenchmarkPass = "shared" });
                        TraceLog.AiBenchmarkDeviceCompleted(deviceType, model.Id, devSw.ElapsedMilliseconds);
                    }
                }
                TraceLog.AiPassCompleted("per-device-fallback", sharedResults.Count, targetDevices.Count);
            }

            // ── Pass 2: Best-per-device performance ──
            if (!sharedOnly && targetDevices.Count > 1 && sharedResults.Count > 0)
            {
                Console.WriteLine();
                ConsoleOutput.WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
                ConsoleOutput.WriteMarkup("[bold cyan]  Pass 2 — Best-Per-Device Performance[/]");
                ConsoleOutput.WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
                TraceLog.AiPassStarted("best-per-device", targetDevices.Count);

                // Build a set of model IDs used in shared pass per device
                var sharedModelByDevice = sharedResults
                    .ToDictionary(r => r.DeviceType, r => r.ModelId, StringComparer.OrdinalIgnoreCase);

                foreach (var deviceType in targetDevices)
                {
                    // Find this device's best model (no alias constraint)
                    var bestModel = FindBestModel(backend, allModels, deviceType, aliasHint: null, strictAlias: false);
                    if (bestModel is null)
                    {
                        TraceLog.DiagnosticInfo($"No model for {deviceType} in best-per-device pass");
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][SKIP][/] No model available for [white]{deviceType}[/] in best-per-device pass.");
                        continue;
                    }

                    // If the best model is the same as the shared model, reuse the result
                    if (sharedModelByDevice.TryGetValue(deviceType, out var sharedModelId)
                        && bestModel.Id.Equals(sharedModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        var sharedResult = sharedResults.First(
                            r => r.DeviceType.Equals(deviceType, StringComparison.OrdinalIgnoreCase));
                        bestPerDeviceResults.Add(sharedResult with { BenchmarkPass = "best_per_device" });
                        TraceLog.DiagnosticInfo($"{deviceType}: best model same as shared ({bestModel.Alias}), reusing");
                        ConsoleOutput.WriteMarkup(
                            $"[dim]  {deviceType}: best model same as shared ({bestModel.Alias}) — reusing result[/]");
                        continue;
                    }

                    Console.WriteLine();
                    TraceLog.AiBenchmarkDeviceStarted(deviceType, bestModel.Id);
                    ConsoleOutput.WriteMarkup(
                        $"[bold cyan]── Pass 2: AI Benchmark: {deviceType} ({bestModel.Id}) ──[/]");

                    var devSw = Stopwatch.StartNew();
                    var result = await BenchmarkModelAsync(
                        backend, serviceUrl, bestModel, deviceType, noDownload, cancellationToken);
                    devSw.Stop();
                    if (result is not null)
                    {
                        bestPerDeviceResults.Add(result with { BenchmarkPass = "best_per_device" });
                        TraceLog.AiBenchmarkDeviceCompleted(deviceType, bestModel.Id, devSw.ElapsedMilliseconds);
                    }
                }
                TraceLog.AiPassCompleted("best-per-device", bestPerDeviceResults.Count, targetDevices.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diag = DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[red]Error:[/] {backend.Name} service error: {ex.Message}");
            ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
        }

        // Return session so caller can reuse it for relation summary and stop it when done.
        // Service is NOT stopped here — caller is responsible for calling session.StopAsync().
        var session = new AiSession { Backend = backend, BackendName = backend.Name, ServiceUrl = serviceUrl, Catalog = allModels };
        return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), session);
    }

    /// <summary>
    /// Runs local-AI relation questions over benchmark JSON files in the
    /// specified directory. Questions are executed per available target device.
    /// When an existing <paramref name="existingSession"/> is provided, the service
    /// and catalog are reused instead of starting a new session (saves ~200 s).
    /// When <paramref name="existingAiResults"/> is provided, Q1/Q2 answers are copied
    /// from the existing benchmark instead of re-running inference.
    /// </summary>
    internal static async Task<AiLocalRelationSummaryResult?> RunLocalRelationSummaryAsync(
        string directoryPath,
        AiExecutionOptions options,
        AiSession? existingSession = null,
        AiBenchmarkTwoPassResult? existingAiResults = null,
        CancellationToken cancellationToken = default)
    {
        using var _sleep = SleepPreventer.Acquire();

        string sourceDir = Path.GetFullPath(
            string.IsNullOrWhiteSpace(directoryPath) ? "." : directoryPath);

        var dataset = ReadLocalRelationDataset(sourceDir, existingAiResults);
        if (dataset.MemorySamples.Count == 0)
        {
            ConsoleOutput.WriteMarkup("[yellow][WARN][/] No STREAM result JSON files found for local relation summary.");
            return null;
        }

        if (dataset.AiSamples.Count == 0)
        {
            ConsoleOutput.WriteMarkup("[yellow][WARN][/] No AI benchmark data found for local relation summary.");
            return null;
        }

        var deviceAggregates = BuildDeviceAggregates(dataset.MemorySamples, dataset.AiSamples);
        double? deviceCorrelation = CalculateDeviceCorrelation(deviceAggregates);
        string relationContext = BuildRelationContext(sourceDir, dataset, deviceAggregates, deviceCorrelation);

        // Reuse existing session if provided
        bool ownsService = false;
        IAiBackend backend;
        string? serviceUrl;
        List<AiModelInfo> allModels;

        if (existingSession is not null)
        {
            backend = existingSession.Backend;
            serviceUrl = existingSession.ServiceUrl;
            allModels = existingSession.Catalog;
            ConsoleOutput.WriteMarkup("[dim]  Reusing existing AI service session[/]");
        }
        else
        {
            var config = AiBackendConfig.Load();
            if (options.BackendType != AiBackendType.Auto)
                config = config with { Backend = options.BackendType };
            backend = AiBackendFactory.Create(config);

            if (!backend.IsAvailable())
            {
                ConsoleOutput.WriteMarkup($"[red][FAIL][/] {backend.Name} not available for relation summary.");
                return null;
            }

            serviceUrl = await backend.StartAsync(cancellationToken);
            ownsService = true;
            if (serviceUrl is null)
            {
                ConsoleOutput.WriteMarkup($"[red][FAIL][/] Cannot start {backend.Name} service for relation summary.");
                return null;
            }

            allModels = await backend.ListModelsAsync(cancellationToken);
        }

        ConsoleOutput.WriteMarkup("[bold cyan]Running local-AI relation summary from saved JSON files...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Source folder: {sourceDir}[/]");
        string aiSourceNote = existingAiResults is null ? "" : " (+ current run)";
        ConsoleOutput.WriteMarkup($"[dim]  Files: {dataset.MemoryFileCount} memory JSON, {dataset.AiFileCount} AI JSON{aiSourceNote}[/]");

        try
        {
            if (allModels.Count == 0)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No local AI models available for relation summary.");
                return null;
            }

            bool strictAlias = !string.IsNullOrWhiteSpace(options.ModelAlias);
            var targetDevices = ParseDeviceFilter(options.DevicesOrDefault);
            TraceLog.DiagnosticInfo(
                $"Relation summary target devices: {string.Join(", ", targetDevices)}");

            var answers = new List<AiRelationQuestionAnswer>();
            var selectedModels = new List<AiRelationModelSelection>();
            var existingResultsByDevice = BuildRelationSeedResults(existingAiResults);

            foreach (var deviceType in targetDevices)
            {
                existingResultsByDevice.TryGetValue(deviceType, out var existingResult);
                var triedModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool deviceSucceeded = false;

                // Retry loop: if the first model fails (e.g. NPU timeout), try the
                // next preferred model for this device before giving up.
                const int maxRetries = 2;
                for (int attempt = 0; attempt < maxRetries && !deviceSucceeded; attempt++)
                {
                    var model = FindRelationSummaryModel(
                        backend,
                        allModels,
                        deviceType,
                        existingResult,
                        options.ModelAlias,
                        strictAlias,
                        triedModelIds);
                    if (model is null)
                    {
                        if (attempt == 0)
                            ConsoleOutput.WriteMarkup(
                                $"[yellow][SKIP][/] No model available for local relation summary on [white]{deviceType}[/].");
                        break;
                    }
                    triedModelIds.Add(model.Id);

                    ConsoleOutput.WriteMarkup(
                        $"[dim]  Loading summary model ({deviceType}): {model.Id}[/]");
                    var loadedId = await backend.LoadModelAsync(model.Id, cancellationToken);
                    if (loadedId is null)
                    {
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][SKIP][/] Failed to load summary model on [white]{deviceType}[/]: {model.Id}");
                        continue;
                    }

                    bool deviceFailed = false;
                    var deviceAnswers = new List<AiRelationQuestionAnswer>();
                    try
                    {
                        for (int i = 0; i < RelationQuestions.Length; i++)
                        {
                            string question = RelationQuestions[i];

                            // Reuse Q1/Q2 from existing AI benchmark results if available
                            if (i < 2
                                && existingResult is not null
                                && model.Id.Equals(existingResult.ModelId, StringComparison.OrdinalIgnoreCase))
                            {
                                var existingRun = i == 0 ? existingResult.Run1 : existingResult.Run2;
                                ConsoleOutput.WriteMarkup($"[dim]  {deviceType} Q{i + 1}: reusing from AI benchmark[/]");
                                deviceAnswers.Add(new AiRelationQuestionAnswer(
                                    Index: i + 1,
                                    Question: question,
                                    Answer: existingRun.ResponseText.Trim(),
                                    DeviceType: deviceType,
                                    Run: existingRun));
                                continue;
                            }

                            string prompt = i switch
                            {
                                0 => Q1,
                                1 => Q2,
                                _ => BuildRelationPrompt(relationContext, question),
                            };
                            ConsoleOutput.WriteMarkup($"[dim]  {deviceType} Q{i + 1}: {question}[/]");

                            var run = await RunInferenceAsync(
                                serviceUrl, model.Id, prompt,
                                modelLoadSec: 0,
                                deviceLabel: deviceType,
                                maxOutputTokens: i == 0 ? Q1MaxOutputTokens : DetailedAnswerMaxOutputTokens,
                                ct: cancellationToken);

                            if (run is null)
                            {
                                deviceFailed = true;
                                break;
                            }

                            deviceAnswers.Add(new AiRelationQuestionAnswer(
                                Index: i + 1,
                                Question: question,
                                Answer: run.ResponseText.Trim(),
                                DeviceType: deviceType,
                                Run: run));
                        }
                    }
                    finally
                    {
                        await backend.UnloadModelAsync(model.Id, cancellationToken);
                    }

                    if (deviceFailed)
                    {
                        ConsoleOutput.WriteMarkup(
                            $"[yellow][RETRY][/] Relation questions did not complete on [white]{deviceType}[/] with {model.Alias}; trying next model...");
                        continue;
                    }

                    answers.AddRange(deviceAnswers);
                    selectedModels.Add(new AiRelationModelSelection(
                        DeviceType: deviceType,
                        ModelId: model.Id,
                        ModelAlias: model.Alias,
                        ExecutionProvider: model.ExecutionProvider));
                    deviceSucceeded = true;
                }

                if (!deviceSucceeded && triedModelIds.Count > 0)
                {
                    ConsoleOutput.WriteMarkup(
                        $"[yellow][SKIP][/] Relation questions did not complete on [white]{deviceType}[/] after {triedModelIds.Count} model(s).");
                }
            }

            if (answers.Count == 0 || selectedModels.Count == 0)
            {
                ConsoleOutput.WriteMarkup(
                    "[yellow][WARN][/] Local relation summary did not produce completed question sets on available devices.");
                return null;
            }

            var primaryModel = selectedModels[0];
            string summaryDeviceType = selectedModels.Count == 1 ? primaryModel.DeviceType : "MULTI";

            return new AiLocalRelationSummaryResult(
                Version: VersionInfo.Version,
                SourceDirectory: sourceDir,
                MemoryJsonFiles: dataset.MemoryFileCount,
                AiJsonFiles: dataset.AiFileCount,
                MemorySamples: dataset.MemorySamples.Count,
                AiSamples: dataset.AiSamples.Count,
                ModelId: primaryModel.ModelId,
                ModelAlias: primaryModel.ModelAlias,
                ExecutionProvider: primaryModel.ExecutionProvider,
                DeviceLevelCorrelation: deviceCorrelation,
                DeviceAggregates: deviceAggregates,
                Questions: answers,
                Timestamp: DateTime.UtcNow.ToString("O"),
                SummaryDeviceType: summaryDeviceType,
                Models: selectedModels,
                BackendName: backend.Name);
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
            if (ownsService)
                await backend.StopAsync(cancellationToken);
        }
    }

    // ── Core benchmark for a single model ─────────────────────────────────

    private static async Task<AiDeviceBenchmarkResult?> BenchmarkModelAsync(
        IAiBackend backend, string serviceUrl, AiModelInfo model,
        string deviceLabel, bool noDownload, CancellationToken ct)
    {
        // Load model
        ConsoleOutput.WriteMarkup("[dim]  Loading model...[/]");
        var loadSw = Stopwatch.StartNew();
        var loadedId = await backend.LoadModelAsync(model.Id, ct);
        if (loadedId is null)
        {
            if (!noDownload)
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
                serviceUrl, loadedId, Q1, modelLoadSec, deviceLabel, Q1MaxOutputTokens, ct);

            if (run1 is null)
            {
                ConsoleOutput.WriteMarkup("[red]  Q1 inference failed.[/]");
                return null;
            }

            // Q2: second inference (warm — model already in memory)
            ConsoleOutput.WriteMarkup($"[dim]  Q2: {Q2}[/]");
            var run2 = await RunInferenceAsync(
                serviceUrl, loadedId, Q2, modelLoadSec: 0, deviceLabel: deviceLabel, maxOutputTokens: DetailedAnswerMaxOutputTokens, ct: ct);

            if (run2 is null)
            {
                ConsoleOutput.WriteMarkup("[red]  Q2 inference failed.[/]");
                return null;
            }

            return new AiDeviceBenchmarkResult(
                Version:           VersionInfo.Version,
                DeviceType:        deviceLabel,
                ModelId:           model.Id,
                ModelAlias:        model.Alias,
                ExecutionProvider: model.ExecutionProvider,
                Question1:         Q1,
                Run1:              run1,
                Question2:         Q2,
                Run2:              run2,
                Timestamp:         DateTime.UtcNow.ToString("O"),
                BackendName:       backend.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[red]  Benchmark model failed:[/] {ex.Message}");
            return null;
        }
        finally
        {
            await backend.UnloadModelAsync(model.Id, ct);
        }
    }

    // ── Single inference run via IChatClient (MEAI) ──────────────────────────

    private static async Task<AiInferenceRun?> RunInferenceAsync(
        string serviceUrl, string modelId, string prompt,
        double modelLoadSec, string deviceLabel = "unknown",
        int maxOutputTokens = DetailedAnswerMaxOutputTokens,
        CancellationToken ct = default)
    {
        string promptPreview = prompt.Length > 60 ? prompt[..60] + "…" : prompt;
        TraceLog.AiInferenceStarted(promptPreview, deviceLabel);

        var sw = Stopwatch.StartNew();

        try
        {
            using var chatClient = CreateChatClient(serviceUrl, modelId);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, prompt)
            };

            var options = new ChatOptions
            {
                MaxOutputTokens = maxOutputTokens
            };

            var response = await chatClient.GetResponseAsync(messages, options, ct);
            sw.Stop();

            double responseSec = sw.Elapsed.TotalSeconds;

            string content = response.Text ?? "";
            int completionTokens = (int)(response.Usage?.OutputTokenCount ?? EstimateTokens(content));
            int promptTokens = (int)(response.Usage?.InputTokenCount ?? 0);
            double tokensPerSec = responseSec > 0 ? completionTokens / responseSec : 0;
            if (response.Usage?.OutputTokenCount is null)
                TraceLog.DiagnosticInfo($"Token count estimated (backend did not report usage): ~{completionTokens} tokens");
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
        catch (Exception ex)
        {
            sw.Stop();
            var diag = DiagnosticHelper.LogException(ex);
            TraceLog.AiInferenceFailed(ex.Message, "AiBenchmarkRunner.cs", 0);
            ConsoleOutput.WriteMarkup($"[red]  Inference error:[/] {ex.Message}");
            ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
            return null;
        }
    }

    // ── Model selection ───────────────────────────────────────────────────

    /// <summary>
    /// Ranks a model's execution provider for GPU selection.
    /// Lower = better: CUDA (0) > DirectML (1) > generic (2) > unknown (3).
    /// CPU/NPU models are unaffected (always 0).
    /// </summary>
    private static int RankExecutionProvider(string modelId, string executionProvider)
    {
        string id = modelId.ToLowerInvariant();
        string ep = executionProvider.ToLowerInvariant();

        if (id.Contains("cuda") || ep.Contains("cuda"))   return 0;
        if (id.Contains("directml") || ep.Contains("directml")) return 1;
        if (id.Contains("generic"))                         return 2;
        return 3;
    }

    private static AiModelInfo? FindBestModel(
        IAiBackend backend,
        IReadOnlyList<AiModelInfo> allModels,
        string deviceLabel,
        string? aliasHint,
        bool strictAlias,
        IReadOnlySet<string>? excludeIds = null)
    {
        var candidates = allModels
            .Where(m => m.DeviceType.Equals(deviceLabel, StringComparison.OrdinalIgnoreCase))
            .Where(m => excludeIds is null || !excludeIds.Contains(m.Id))
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
            var aliasMatches = candidates
                .Where(m =>
                    m.Alias.Equals(aliasHint, StringComparison.OrdinalIgnoreCase)
                    || m.Id.Equals(aliasHint, StringComparison.OrdinalIgnoreCase)
                    || m.Id.Contains(aliasHint, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.IsCached)
                .ThenBy(m => m.FileSizeMb > 0 ? m.FileSizeMb : double.MaxValue)
                .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var match = aliasMatches.FirstOrDefault();
            if (match is not null)
            {
                TraceLog.AiModelSelected(
                    match.Id,
                    match.Alias,
                    deviceLabel,
                    match.IsCached ? "alias match cached" : "alias match");
                return match;
            }

            ConsoleOutput.WriteMarkup(
                $"[yellow][WARN][/] Model '{aliasHint}' not found for {deviceLabel}; falling back to preferred list.");

            if (strictAlias)
                return null;
        }

        var preferredAliases = backend.GetPreferredAliases(deviceLabel);

        // 1. Preferred aliases that are already cached (exact alias match first)
        //    For GPU, prefer CUDA > DirectML > generic execution providers.
        foreach (var alias in preferredAliases)
        {
            var cachedForAlias = candidates
                .Where(c => c.IsCached &&
                    (c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => RankExecutionProvider(c.Id, c.ExecutionProvider))
                .ThenBy(c => c.FileSizeMb > 0 ? c.FileSizeMb : double.MaxValue)
                .FirstOrDefault();
            if (cachedForAlias is not null)
            {
                TraceLog.AiModelSelected(cachedForAlias.Id, cachedForAlias.Alias, deviceLabel, "cached preferred");
                return cachedForAlias;
            }
        }

        // 2. Any cached model for this device (prefer optimized EP, then smaller)
        var anyCached = candidates
            .Where(c => c.IsCached)
            .OrderBy(c => RankExecutionProvider(c.Id, c.ExecutionProvider))
            .ThenBy(c => c.FileSizeMb > 0 ? c.FileSizeMb : double.MaxValue)
            .FirstOrDefault();
        if (anyCached is not null)
        {
            TraceLog.AiModelSelected(anyCached.Id, anyCached.Alias, deviceLabel, "cached best-ep");
            return anyCached;
        }

        // 3. Preferred aliases in configured order (download if needed)
        //    Prefer exact alias match to avoid e.g. "phi-4-mini" accidentally
        //    matching "phi-4-mini-reasoning" via Id.Contains.
        //    Within an alias, prefer CUDA > DirectML > generic.
        foreach (var alias in preferredAliases)
        {
            var v = candidates
                .Where(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.StartsWith(alias, StringComparison.OrdinalIgnoreCase)
                    || c.Id.Contains(alias, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => RankExecutionProvider(c.Id, c.ExecutionProvider))
                .ThenBy(c => c.FileSizeMb > 0 ? c.FileSizeMb : double.MaxValue)
                .FirstOrDefault();
            if (v is not null)
            {
                TraceLog.AiModelSelected(v.Id, v.Alias, deviceLabel, "preferred uncached");
                return v;
            }
        }

        // 4. Smallest available model (prefer optimized EP)
        var smallest = candidates
            .OrderBy(c => RankExecutionProvider(c.Id, c.ExecutionProvider))
            .ThenBy(c => c.FileSizeMb > 0 ? c.FileSizeMb : double.MaxValue)
            .FirstOrDefault();
        if (smallest is not null)
            TraceLog.AiModelSelected(smallest.Id, smallest.Alias, deviceLabel, "smallest available");
        return smallest;
    }

    private static void LogCatalogDeviceTypes(IReadOnlyList<AiModelInfo> allModels)
    {
        var counts = allModels
            .GroupBy(m => m.DeviceType.ToUpperInvariant())
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();
        string summary = string.Join(", ", counts);
        TraceLog.AiCatalogDeviceTypes(summary);
        ConsoleOutput.WriteMarkup($"[dim]  Catalog models by device: {summary}[/]");
    }

    private static List<string> SelectSharedAliasCandidates(
        IReadOnlyList<AiModelInfo> allModels,
        IReadOnlyList<string> targetDevices,
        IAiBackend backend)
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
                int cachedForTarget = 0;
                foreach (var m in g)
                {
                    deviceSet.Add(m.DeviceType.ToUpperInvariant());
                    if (m.IsCached && targetSet.Contains(m.DeviceType.ToUpperInvariant()))
                        cachedForTarget++;
                }

                int coverage = deviceSet.Count(d => targetSet.Contains(d));
                return new { Alias = g.Key, Coverage = coverage, CachedForTarget = cachedForTarget, Devices = deviceSet };
            })
            .Where(x => x.Coverage > 0)
            // Only consider aliases explicitly listed in the backend's shared priority —
            // this prevents junk/unknown aliases from bloating the shared pass.
            .Where(x =>
            {
                var sharedPriority = backend.GetSharedAliasPriority();
                return sharedPriority.Any(p => p.Equals(x.Alias, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (byAlias.Count == 0)
            return [];

        int PriorityIndex(string alias)
        {
            var sharedPriority = backend.GetSharedAliasPriority();
            for (int i = 0; i < sharedPriority.Count; i++)
            {
                if (sharedPriority[i].Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return int.MaxValue;
        }

        // Sort: highest coverage first, then most cached variants, then priority order
        return byAlias
            .OrderByDescending(x => x.Coverage)
            .ThenByDescending(x => x.CachedForTarget)
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

    private static LocalRelationDataset ReadLocalRelationDataset(
        string directoryPath,
        AiBenchmarkTwoPassResult? existingAiResults = null)
    {
        int memoryFileCount = 0;
        int aiFileCount = 0;
        var memorySamples = new List<MemorySample>();
        var aiSamples = new List<AiSample>();

        if (!Directory.Exists(directoryPath))
        {
            TraceLog.DiagnosticInfo($"Relation dataset directory does not exist: {directoryPath}");
            return new LocalRelationDataset(memoryFileCount, aiFileCount, memorySamples, aiSamples);
        }

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

        if (existingAiResults is not null)
            AppendAiSamples(existingAiResults, aiSamples);

        TraceLog.AiRelationDatasetLoaded(memoryFileCount, aiFileCount, memorySamples.Count, aiSamples.Count);
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

            // Old format: root is a flat array of device results
            if (root.ValueKind == JsonValueKind.Array)
            {
                using var entries = root.EnumerateArray();
                while (entries.MoveNext())
                    TryReadAiSampleEntry(entries.Current, aiSamples);
                return;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // New two-pass format: shared_results / best_per_device_results arrays
                // Prefer best_per_device when available; fall back to shared for
                // devices not covered, avoiding duplicate samples.
                bool foundTwoPass = false;
                var coveredDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (root.TryGetProperty("best_per_device_results", out var bpd) && bpd.ValueKind == JsonValueKind.Array)
                {
                    foundTwoPass = true;
                    using var entries = bpd.EnumerateArray();
                    while (entries.MoveNext())
                    {
                        var dt = TryGetString(entries.Current, "device_type");
                        if (dt is not null) coveredDevices.Add(dt);
                        TryReadAiSampleEntry(entries.Current, aiSamples);
                    }
                }

                if (root.TryGetProperty("shared_results", out var shared) && shared.ValueKind == JsonValueKind.Array)
                {
                    foundTwoPass = true;
                    using var entries = shared.EnumerateArray();
                    while (entries.MoveNext())
                    {
                        // Only add shared entries for devices not already covered by best-per-device
                        var dt = TryGetString(entries.Current, "device_type");
                        if (dt is not null && coveredDevices.Contains(dt)) continue;
                        TryReadAiSampleEntry(entries.Current, aiSamples);
                    }
                }
                if (foundTwoPass) return;

                // Legacy wrapper: { "results": [...] }
                if (root.TryGetProperty("results", out var results)
                    && results.ValueKind == JsonValueKind.Array)
                {
                    using var entries = results.EnumerateArray();
                    while (entries.MoveNext())
                        TryReadAiSampleEntry(entries.Current, aiSamples);
                    return;
                }

                // Single object entry
                TryReadAiSampleEntry(root, aiSamples);
            }
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

    private static void AppendAiSamples(
        AiBenchmarkTwoPassResult existingAiResults,
        List<AiSample> aiSamples)
    {
        var coveredDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in existingAiResults.BestPerDeviceResults)
        {
            coveredDevices.Add(result.DeviceType);
            aiSamples.Add(new AiSample(
                NormalizeDeviceType(result.DeviceType, null),
                result.Run2.TokensPerSecond > 0 ? result.Run2.TokensPerSecond : result.Run1.TokensPerSecond,
                result.ModelAlias,
                result.Timestamp));
        }

        foreach (var result in existingAiResults.SharedResults)
        {
            if (coveredDevices.Contains(result.DeviceType))
                continue;

            aiSamples.Add(new AiSample(
                NormalizeDeviceType(result.DeviceType, null),
                result.Run2.TokensPerSecond > 0 ? result.Run2.TokensPerSecond : result.Run1.TokensPerSecond,
                result.ModelAlias,
                result.Timestamp));
        }
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

    private static Dictionary<string, AiDeviceBenchmarkResult> BuildRelationSeedResults(
        AiBenchmarkTwoPassResult? existingAiResults)
    {
        var results = new Dictionary<string, AiDeviceBenchmarkResult>(StringComparer.OrdinalIgnoreCase);
        if (existingAiResults is null)
            return results;

        foreach (var result in existingAiResults.BestPerDeviceResults)
            results[result.DeviceType] = result;

        foreach (var result in existingAiResults.SharedResults)
            results.TryAdd(result.DeviceType, result);

        return results;
    }

    private static AiModelInfo? FindRelationSummaryModel(
        IAiBackend backend,
        IReadOnlyList<AiModelInfo> allModels,
        string deviceLabel,
        AiDeviceBenchmarkResult? existingResult,
        string? aliasHint,
        bool strictAlias,
        IReadOnlySet<string>? excludeIds = null)
    {
        var candidates = allModels
            .Where(m => m.DeviceType.Equals(deviceLabel, StringComparison.OrdinalIgnoreCase))
            .Where(m => excludeIds is null || !excludeIds.Contains(m.Id))
            .ToList();
        if (candidates.Count == 0)
            return null;

        if (existingResult is not null)
        {
            var exactMatch = candidates.FirstOrDefault(m =>
                m.Id.Equals(existingResult.ModelId, StringComparison.OrdinalIgnoreCase));
            if (exactMatch is not null)
            {
                TraceLog.DiagnosticInfo($"Relation summary reusing exact model for {deviceLabel}: {exactMatch.Id}");
                return exactMatch;
            }

            var aliasMatch = candidates
                .Where(m => m.Alias.Equals(existingResult.ModelAlias, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.IsCached)
                .ThenBy(m => RankExecutionProvider(m.Id, m.ExecutionProvider))
                .ThenBy(m => m.FileSizeMb > 0 ? m.FileSizeMb : double.MaxValue)
                .FirstOrDefault();
            if (aliasMatch is not null)
            {
                TraceLog.DiagnosticInfo($"Relation summary falling back to alias match for {deviceLabel}: {aliasMatch.Id}");
                return aliasMatch;
            }
        }

        return FindBestModel(backend, allModels, deviceLabel, aliasHint, strictAlias, excludeIds);
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
