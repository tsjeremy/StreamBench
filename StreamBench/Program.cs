// Program.cs — STREAM Benchmark .NET 10 CLI entry point
//
// Usage:
//   StreamBench [--cpu] [--gpu] [--gpu-device N] [--array-size N] [--ntimes N]
//               [--range START:END:STEP] [--no-save] [--output-dir DIR]
//               [--exe PATH]
//               [--ai] [--ai-device cpu|gpu|npu] [--ai-model ALIAS]
//               [--ai-shared-only]
//               [--ai-no-download]
//
// If neither --cpu nor --gpu is specified, both benchmarks run automatically
// and all available GPUs are benchmarked.
//
// --ai runs the AI inference benchmark via Foundry Local CLI + REST API.
//
// Examples:
//   StreamBench                              # runs both CPU and GPU
//   StreamBench --cpu --array-size 200000000
//   StreamBench --gpu --array-size 100000000
//   StreamBench --cpu --range 50000000:200000000:50000000
//   StreamBench --cpu --no-save
//   StreamBench --ai                         # AI benchmark on all devices
//   StreamBench --ai --ai-device cpu,gpu     # AI benchmark on CPU and GPU only

using System.Text;
using StreamBench;
using StreamBench.Models;

Console.OutputEncoding = Encoding.UTF8;
CliLog.InitializeFromEnvironment();

// ── Global unhandled exception handler ─────────────────────────────────
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        TraceLog.UnhandledException(
            ex.ToString(),
            ex.TargetSite?.DeclaringType?.Name ?? "unknown",
            0,
            ex.TargetSite?.Name ?? "unknown");
        Console.Error.WriteLine($"[FATAL] Unhandled exception: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
    }
};

TraceLog.AppStarted(string.Join(" ", args));
ConsoleOutput.WriteMarkup($"[dim]StreamBench v{VersionInfo.Version}[/]");

int finalExitCode;
try
{
    finalExitCode = await RunMainAsync(args);
}
catch (Exception ex)
{
    DiagnosticHelper.LogException(ex);
    ConsoleOutput.WriteMarkup($"[red][FATAL][/] Unexpected error: {ex.Message}");
    ConsoleOutput.WriteMarkup($"[dim]  {ex.GetType().Name}: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}[/]");
    finalExitCode = 99;
}

TraceLog.AppExiting(finalExitCode);
if (finalExitCode != 0)
    ConsoleOutput.WriteMarkup($"[dim]  Trace log: {TraceLog.LogPath}[/]");
CliLog.Shutdown();
return finalExitCode;

// ── Main logic wrapped in a function for top-level error handling ──────
async Task<int> RunMainAsync(string[] args)
{
    // Prevent system sleep for the duration of the benchmark run.
    // Screen-off timeout is unaffected — only unattended sleep is blocked.
    using var _sleep = SleepPreventer.Acquire();

    // ── Parse arguments ────────────────────────────────────────────────────────
    bool   wantCpu    = false;
    bool   wantGpu    = false;
    bool   modeSet    = false;   // true if user explicitly passed --cpu or --gpu
    bool   wantAi     = false;   // --ai flag
    bool   aiSharedOnly = false;   // --ai-shared-only (skip best-per-device pass)
    bool   aiNoDownload = false;   // --ai-no-download (cached models only)
    bool   aiQuick     = false;   // --quick-ai / --ai-quick (CI: skip shared, cached only, 1 model/device)
    bool   aiOnly      = false;   // --ai-only (skip default CPU/GPU passes)
    string? aiModel   = null;    // --ai-model ALIAS
    string? aiDevices = null;    // --ai-device cpu,gpu,npu (comma-separated)
    string? aiBackend = null;    // --ai-backend foundry|lmstudio|auto
    long?  arraySize  = null;
    bool   noSave     = false;
    string? outputDir = null;
    string? exePath   = null;
    string? cpuExePath = null;
    string? gpuExePath = null;
    int?   gpuDevice  = null;    // specific GPU device index (null = all)
    long   rangeStart = 0, rangeEnd = 0, rangeStep = 50_000_000;

    for (int i = 0; i < args.Length; i++)
    {
        try
        {
            switch (args[i])
            {
                case "--cpu":   wantCpu = true; modeSet = true; break;
                case "--gpu":   wantGpu = true; modeSet = true; break;
                case "--ai":    wantAi  = true; break;
                case "--ai-only": wantAi = true; aiOnly = true; break;
                case "--ai-shared-only": aiSharedOnly = true; break;
                case "--ai-no-download": aiNoDownload = true; break;
                case "--quick-ai": case "--ai-quick": aiQuick = true; break;
                case "--no-save": noSave = true; break;

                case "--ai-model" when i + 1 < args.Length:
                    aiModel = args[++i];
                    break;

                case "--ai-device" when i + 1 < args.Length:
                    aiDevices = args[++i];
                    break;

                case "--ai-backend" when i + 1 < args.Length:
                    aiBackend = args[++i];
                    break;

                case "--array-size"when i + 1 < args.Length:
                    arraySize = ParseSize(args[++i]);
                    break;

                case "--output-dir" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;

                case "--exe" when i + 1 < args.Length:
                    exePath = args[++i];
                    break;

                case "--cpu-exe" when i + 1 < args.Length:
                    cpuExePath = args[++i];
                    break;

                case "--gpu-exe" when i + 1 < args.Length:
                    gpuExePath = args[++i];
                    break;

                case "--gpu-device" when i + 1 < args.Length:
                    gpuDevice = int.Parse(args[++i]);
                    break;

                case "--range" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split(':');
                    if (parts.Length >= 2)
                    {
                        rangeStart = ParseSize(parts[0]);
                        rangeEnd   = ParseSize(parts[1]);
                        rangeStep  = parts.Length >= 3 ? ParseSize(parts[2]) : 50_000_000;
                    }
                    break;
                }
                case "--help": case "-h":
                    PrintHelp();
                    return 0;
            }
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"Failed to parse argument '{args[i]}': {ex.Message}");
        }
    }

    // Keep these flags referenced in non-AI builds to avoid dead-code warnings.
#if ENABLE_AI
    var aiOptions = AiExecutionOptions.FromCli(
        aiDevices,
        aiModel,
        aiSharedOnly,
        aiNoDownload,
        aiQuick,
        aiBackend);

    if (!wantAi && aiOptions.HasExplicitSelection)
        wantAi = true;
#else
    _ = aiSharedOnly;
    _ = aiNoDownload;
    _ = aiQuick;

    // If user provided AI-specific options, enable AI mode automatically.
    if (!wantAi
        && (!string.IsNullOrWhiteSpace(aiModel)
            || !string.IsNullOrWhiteSpace(aiDevices)
            || !string.IsNullOrWhiteSpace(aiBackend)
            || aiSharedOnly
            || aiNoDownload
            || aiQuick))
    {
        wantAi = true;
    }
#endif

#if ENABLE_AI
    // Auto-enable AI when the binary name contains "_ai" and user didn't explicitly set flags
    if (!wantAi && !modeSet)
    {
        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") ?? "";
        if (exeName.Contains("_ai", StringComparison.OrdinalIgnoreCase))
            wantAi = true;
    }
#endif

    // Default: run both CPU and GPU when user didn't specify
    if (!modeSet && !aiOnly)
    {
        wantCpu = true;
        wantGpu = true;
    }
    // ── Prepare output directory ───────────────────────────────────────────────
    if (outputDir is not null)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[red]Error:[/] Cannot create output directory: {outputDir}");
            return 1;
        }
    }

    // ── Run benchmarks ─────────────────────────────────────────────────────────
    int exitCode = 0;
    bool systemInfoPrinted = false;
    var savedMemoryJsonPaths = new List<string>();

    if (wantCpu)
    {
        exitCode = await RunBenchmarkAsync(isGpu: false, cpuExePath ?? exePath, arraySize,
            rangeStart, rangeEnd, rangeStep, noSave, outputDir);
        if (wantGpu) Console.WriteLine();
    }

    if (wantGpu)
    {
        int gpuCode = await RunGpuBenchmarksAsync(gpuExePath ?? exePath, arraySize,
            rangeStart, rangeEnd, rangeStep, noSave, outputDir, gpuDevice);
        if (gpuCode != 0) exitCode = gpuCode;
    }

    if (wantAi)
    {
#if ENABLE_AI
        if (wantCpu || wantGpu) Console.WriteLine();
        int aiCode = await RunAiBenchmarkAsync(
            aiOptions,
            noSave,
            outputDir,
            savedMemoryJsonPaths);
        if (aiCode != 0)
        {
            // AI failure is non-fatal when CPU/GPU benchmarks already ran successfully
            if ((wantCpu || wantGpu) && exitCode == 0)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] AI benchmark failed but memory benchmarks completed successfully.");
                TraceLog.DiagnosticInfo("AI benchmark failed (non-fatal); memory benchmarks succeeded.");
            }
            else
            {
                exitCode = aiCode;
            }
        }
#else
        ConsoleOutput.WriteMarkup("[yellow][SKIP][/] AI benchmark is not available in this build.");
        ConsoleOutput.WriteMarkup("[dim]  Rebuild with -p:EnableAI=true or use the pre-built binary with --ai.[/]");
#endif
    }

    return exitCode;

    // ── Run all GPU benchmarks (discovers GPUs, loops over each) ───────────────
    async Task<int> RunGpuBenchmarksAsync(string? exePath, long? arraySize,
        long rangeStart, long rangeEnd, long rangeStep,
        bool noSave, string? outputDir, int? gpuDevice)
    {
        string? exe = exePath ?? BenchmarkRunner.FindExecutable(isGpu: true);
        if (exe is null)
        {
            ConsoleOutput.WriteMarkup("[yellow][SKIP][/] GPU benchmark backend not found.");
            ConsoleOutput.WriteMarkup("[dim]No embedded backend found and no external binary in the current directory.[/]");
            return modeSet ? 1 : 0;
        }

        // If user specified a single GPU device, run just that one
        if (gpuDevice.HasValue)
        {
            return await RunSingleGpuAsync(exe, arraySize, rangeStart, rangeEnd, rangeStep,
                noSave, outputDir, gpuDevice.Value, $"GPU #{gpuDevice.Value}");
        }

        // Discover all GPUs (exclude NPU devices — memory benchmark is GPU-only)
        var allDevices = await BenchmarkRunner.ListGpusAsync(exe);
        var gpus = allDevices.Where(g => g.DeviceKind != "NPU").ToList();

        if (gpus.Count == 0)
        {
            // Fallback: no --list-gpus support or no GPUs found — run without device index
            return await RunSingleGpuAsync(exe, arraySize, rangeStart, rangeEnd, rangeStep,
                noSave, outputDir, null, null);
        }

        if (gpus.Count == 1)
        {
            // Single GPU — run directly
            return await RunSingleGpuAsync(exe, arraySize, rangeStart, rangeEnd, rangeStep,
                noSave, outputDir, gpus[0].Index, null);
        }

        // Multiple devices — enumerate and run each
        ConsoleOutput.WriteMarkup($"[bold cyan]Discovered {gpus.Count} GPU(s):[/]");
        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            double memGb = g.GlobalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            string displayName = GpuDeviceInfo.InferGpuDisplayName(g.Name, g.Vendor) ?? g.Name;
            ConsoleOutput.WriteMarkup(
                $"  [white]#{g.Index}[/] [bold white]{displayName}[/] [dim](GPU, {g.Vendor}, {memGb:F1} GB)[/]");
        }
        Console.WriteLine();

        int exitCode = 0;
        for (int i = 0; i < gpus.Count; i++)
        {
            if (i > 0) Console.WriteLine();
            var g = gpus[i];
            string displayName = GpuDeviceInfo.InferGpuDisplayName(g.Name, g.Vendor) ?? g.Name;
            ConsoleOutput.WriteMarkup($"[bold cyan]── GPU #{g.Index}: {displayName} ──[/]");

            // Only print system info for the first device (it's the same for all)
            int code = await RunSingleGpuAsync(exe, arraySize, rangeStart, rangeEnd, rangeStep,
                noSave, outputDir, g.Index, $"GPU #{g.Index} ({displayName})",
                printSystemInfo: i == 0);
            if (code != 0) exitCode = code;
        }
        return exitCode;
    }

    // ── Run a single GPU benchmark ─────────────────────────────────────────────
    async Task<int> RunSingleGpuAsync(string exe, long? arraySize,
        long rangeStart, long rangeEnd, long rangeStep,
        bool noSave, string? outputDir, int? gpuDeviceIndex, string? gpuLabel,
        bool printSystemInfo = true)
    {
        if (rangeStart > 0 && rangeEnd > rangeStart)
        {
            await RunRangeAsync(exe, isGpu: true, rangeStart, rangeEnd, rangeStep,
                noSave, outputDir, gpuDeviceIndex, gpuLabel);
        }
        else
        {
            var result = await ConsoleOutput.RunWithSpinnerAsync(
                exe, arraySize, isGpu: true,
                gpuDeviceIndex: gpuDeviceIndex, gpuLabel: gpuLabel);
            if (result is null)
            {
                ConsoleOutput.WriteMarkup("[red][FAIL][/] GPU benchmark failed or returned no output.");
                return 1;
            }
            DisplayAndSave(result, noSave, outputDir, printSystemInfo);
        }
        return 0;
    }

    // ── Run a single benchmark (CPU or GPU) ────────────────────────────────────
    async Task<int> RunBenchmarkAsync(bool isGpu, string? exePath, long? arraySize,
        long rangeStart, long rangeEnd, long rangeStep,
        bool noSave, string? outputDir)
    {
        string? exe = exePath ?? BenchmarkRunner.FindExecutable(isGpu);
        if (exe is null)
        {
            string kind = isGpu ? "GPU" : "CPU";
            ConsoleOutput.WriteMarkup($"[yellow][SKIP][/] {kind} benchmark backend not found.");
            ConsoleOutput.WriteMarkup("[dim]No embedded backend found and no external binary in the current directory.[/]");
            // Only fail if user explicitly requested this mode
            return modeSet ? 1 : 0;
        }

        if (rangeStart > 0 && rangeEnd > rangeStart)
        {
            await RunRangeAsync(exe, isGpu, rangeStart, rangeEnd, rangeStep, noSave, outputDir);
        }
        else
        {
            var result = await ConsoleOutput.RunWithSpinnerAsync(exe, arraySize, isGpu);
            if (result is null)
            {
                ConsoleOutput.WriteMarkup("[red][FAIL][/] Benchmark failed or returned no output.");
                return 1;
            }
            DisplayAndSave(result, noSave, outputDir);
        }
        return 0;
    }

    // ── Range testing loop ─────────────────────────────────────────────────────
    async Task RunRangeAsync(string exe, bool isGpu,
        long start, long end, long step,
        bool noSave, string? outputDir,
        int? gpuDeviceIndex = null, string? gpuLabel = null)
    {
        var sizes = new List<long>();
        for (long s = start; s <= end; s += step)
            sizes.Add(s);

        ConsoleOutput.WriteMarkup(
            $"[bold cyan]Range testing:[/] [white]{sizes.Count} sizes from " +
            $"{start / 1_000_000}M to {end / 1_000_000}M (step {step / 1_000_000}M)[/]");
        Console.WriteLine();

        // Open consolidated CSV
        string? rangeCsvPath = null;
        System.IO.StreamWriter? rangeCsv = null;
        if (!noSave)
        {
            rangeCsvPath = Path.Combine(outputDir ?? ".",
                $"stream_{(isGpu ? "gpu" : "cpu")}_range_{start / 1_000_000}M" +
                $"_to_{end / 1_000_000}M_step_{step / 1_000_000}M.csv");
            rangeCsv = ResultSaver.OpenRangeCsv(rangeCsvPath);
            if (rangeCsv is not null)
                ConsoleOutput.PrintFileSaved(rangeCsvPath);
        }

        int success = 0;
        for (int idx = 0; idx < sizes.Count; idx++)
        {
            var result = await ConsoleOutput.RunWithSpinnerAsync(
                exe, sizes[idx], isGpu, idx, sizes.Count,
                gpuDeviceIndex, gpuLabel);

            if (result is null)
            {
                ConsoleOutput.WriteMarkup($"[red][FAIL][/] Test {idx + 1} failed.");
                continue;
            }

            ConsoleOutput.PrintResults(result);

            if (!noSave)
            {
                if (rangeCsv is not null)
                    ResultSaver.AppendRangeCsv(rangeCsv, result);
                var jsonPath = ResultSaver.SaveJson(result, outputDir);
                if (jsonPath is not null)
                {
                    savedMemoryJsonPaths.Add(jsonPath);
                    ConsoleOutput.PrintFileSaved(jsonPath);
                }
            }

            success++;
        }

        rangeCsv?.Dispose();

        Console.WriteLine();
        ConsoleOutput.WriteMarkup(
            $"[bold white]Range complete:[/] [green]{success}/{sizes.Count}[/] tests passed.");
    }

    // ── Single result display + save ───────────────────────────────────────────
    void DisplayAndSave(BenchmarkResult result, bool noSave, string? outputDir, bool printSystemInfo = true)
    {
        ConsoleOutput.PrintBanner(result);
        if (printSystemInfo && !systemInfoPrinted)
        {
            ConsoleOutput.PrintSystemInfo(result);
            systemInfoPrinted = true;
        }
        else
            ConsoleOutput.PrintDeviceInfo(result);   // always show device box
        ConsoleOutput.PrintConfig(result);
        ConsoleOutput.PrintResults(result);

        if (!noSave)
        {
            ConsoleOutput.WriteMarkup("[bold white]Saving results...[/]");
            var csvPath  = ResultSaver.SaveCsv(result, outputDir);
            var jsonPath = ResultSaver.SaveJson(result, outputDir);
            if (csvPath  is not null) ConsoleOutput.PrintFileSaved(csvPath);
            if (jsonPath is not null)
            {
                savedMemoryJsonPaths.Add(jsonPath);
                ConsoleOutput.PrintFileSaved(jsonPath);
            }
            Console.WriteLine();
        }
    }
}

// ── Helpers ────────────────────────────────────────────────────────────

// ── AI inference benchmark ─────────────────────────────────────────────
#if ENABLE_AI
async Task<int> RunAiBenchmarkAsync(
    AiExecutionOptions aiOptions,
    bool noSave,
    string? outputDir,
    IReadOnlyCollection<string>? memoryJsonPaths = null)
{
    ConsoleOutput.WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
    ConsoleOutput.WriteMarkup($"[bold cyan]  AI Inference Benchmark — {aiOptions.BackendLabel}[/]");
    ConsoleOutput.WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
    ConsoleOutput.WriteMarkup($"[dim]  Q1 (cold): {AiBenchmarkRunner.Q1}[/]");
    ConsoleOutput.WriteMarkup($"[dim]  Q2 (warm): {AiBenchmarkRunner.Q2}[/]");
    ConsoleOutput.WriteMarkup($"[dim]  Q3 (relation): {AiBenchmarkRunner.Q3Label}[/]");

    AiBenchmarkTwoPassResult twoPassResult;
    AiBenchmarkRunner.AiSession? aiSession = null;
    try
    {
        (twoPassResult, aiSession) = await AiBenchmarkRunner.RunAsync(aiOptions);
    }
    catch (Exception ex)
    {
        var diag = DiagnosticHelper.LogException(ex);
        ConsoleOutput.WriteMarkup($"[red][FAIL][/] AI benchmark failed: {ex.Message}");
        ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
        ConsoleOutput.WriteMarkup("[dim]  Ensure an AI backend is installed:[/]");
        ConsoleOutput.WriteMarkup("[dim]  Foundry: winget install Microsoft.FoundryLocal[/]");
        ConsoleOutput.WriteMarkup("[dim]  LM Studio: https://lmstudio.ai[/]");
        return 1;
    }

    var allResults = twoPassResult.SharedResults.ToList();

    if (allResults.Count == 0)
    {
        ConsoleOutput.WriteMarkup("[yellow][WARN][/] No AI benchmark results were produced.");
        if (aiOptions.NoDownload || aiOptions.QuickMode)
        {
            ConsoleOutput.WriteMarkup($"[dim]  Cached-only mode was enabled, but no requested model was available in the local {(aiSession?.BackendName ?? aiOptions.BackendLabel)} cache.[/]");
            ConsoleOutput.WriteMarkup("[dim]  Re-run without --quick-ai / --ai-no-download, or pre-download a model first:[/]");
            ConsoleOutput.WriteMarkup("[dim]  foundry model run phi-3.5-mini[/]");
        }
        else if (aiOptions.DeviceFilter.Count == 1 && aiOptions.DeviceFilter[0].Equals("NPU", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleOutput.WriteMarkup("[dim]  All NPU models failed to load. Possible causes:[/]");
            ConsoleOutput.WriteMarkup("[dim]  • NPU driver may not be installed or may need updating[/]");
            ConsoleOutput.WriteMarkup("[dim]  • Foundry Local NPU execution provider may not support this NPU hardware yet[/]");
            ConsoleOutput.WriteMarkup("[dim]  Try: foundry --version  and check for Foundry/driver updates[/]");
            ConsoleOutput.WriteMarkup("[dim]  Or run without --ai-device npu to benchmark CPU/GPU instead[/]");
        }
        else
        {
            ConsoleOutput.WriteMarkup("[dim]  Ensure an AI backend is installed:[/]");
            ConsoleOutput.WriteMarkup("[dim]  Foundry: winget install Microsoft.FoundryLocal[/]");
            ConsoleOutput.WriteMarkup("[dim]  LM Studio: https://lmstudio.ai[/]");
        }
        if (aiSession is not null) await aiSession.StopAsync();
        return 1;
    }

    foreach (var r in allResults.DistinctBy(r => (r.DeviceType, r.ModelId)))
        ConsoleOutput.PrintAiResult(r);

    AiLocalRelationSummaryResult? relationSummary = null;

    // Run Q3 when memory JSON files exist from this run or in the output directory.
    bool runSummary = false;
    bool hasMemoryJsonFromRun = (memoryJsonPaths ?? [])
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(Path.GetFullPath)
        .Any(File.Exists);
    string checkDir = Path.GetFullPath(outputDir ?? ".");
    bool hasMemoryJsonInOutputDir = Directory.Exists(checkDir)
        && Directory.EnumerateFiles(checkDir, "stream_*_results_*.json", SearchOption.TopDirectoryOnly).Any();

    if (hasMemoryJsonFromRun || hasMemoryJsonInOutputDir)
    {
        runSummary = true;
        TraceLog.AiRelationAutoEnabled("Q3 relation summary enabled: memory JSON files found in output directory");
        ConsoleOutput.WriteMarkup("[dim]  Q3 relation summary enabled (memory JSON files found)[/]");
    }
    else
    {
        TraceLog.AiRelationSkipped("no memory JSON files found in output directory");
        ConsoleOutput.WriteMarkup("[dim]  Q3 relation summary skipped (no memory JSON files found)[/]");
    }

    if (runSummary)
    {
        string summaryDir = Path.GetFullPath(outputDir ?? ".");
        try
        {
            relationSummary = await AiBenchmarkRunner.RunLocalRelationSummaryAsync(
                summaryDir,
                aiOptions,
                existingSession: aiSession,
                existingAiResults: twoPassResult);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogException(ex);
            ConsoleOutput.WriteMarkup($"[yellow][WARN][/] Local relation summary failed: {ex.Message}");
        }
    }

    // Print Device Comparison after relation summary so Q3 data is available
    ConsoleOutput.PrintAiSummary(twoPassResult, relationSummary);

    if (relationSummary is not null)
    {
        ConsoleOutput.PrintAiRelationSummary(relationSummary);

        if (!noSave)
        {
            ConsoleOutput.WriteMarkup("[bold white]Saving AI relation summary...[/]");
            var summaryPath = ResultSaver.SaveAiRelationSummaryJson(relationSummary, outputDir);
            if (summaryPath is not null) ConsoleOutput.PrintFileSaved(summaryPath);
            Console.WriteLine();
        }
    }

    if (!noSave)
    {
        ConsoleOutput.WriteMarkup("[bold white]Saving AI benchmark results...[/]");
        var jsonPath = ResultSaver.SaveAiJson(twoPassResult, relationSummary, outputDir);
        if (jsonPath is not null) ConsoleOutput.PrintFileSaved(jsonPath);
        Console.WriteLine();
    }

    if (!noSave)
    {
        var mergedPaths = ResultSaver.MergeAiIntoMemoryJson(
            memoryJsonPaths, twoPassResult, relationSummary, outputDir);
        if (mergedPaths.Count > 0)
        {
            ConsoleOutput.WriteMarkup("[bold white]Embedding AI questions into memory benchmark JSON...[/]");
            foreach (var path in mergedPaths)
                ConsoleOutput.PrintFileSaved(path);
            Console.WriteLine();
        }
    }

    // Stop the Foundry service now that all AI work is done
    if (aiSession is not null)
        await aiSession.StopAsync();

    return 0;
}
#endif

static long ParseSize(string s)
{
    s = s.Trim().ToUpperInvariant();
    if (s.EndsWith("G")) return (long)(double.Parse(s[..^1]) * 1_000_000_000);
    if (s.EndsWith("M")) return (long)(double.Parse(s[..^1]) * 1_000_000);
    if (s.EndsWith("K")) return (long)(double.Parse(s[..^1]) * 1_000);
    return long.Parse(s);
}

static void PrintHelp()
{
    ConsoleOutput.WriteMarkup("[bold cyan]StreamBench[/] — STREAM Benchmark .NET 10 frontend");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]Usage:[/]");
    ConsoleOutput.WriteMarkup("  StreamBench [options]");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]Options:[/]");
    ConsoleOutput.WriteMarkup("  [cyan]--cpu[/]                    Run CPU benchmark only");
    ConsoleOutput.WriteMarkup("  [cyan]--gpu[/]                    Run GPU benchmark only");
    ConsoleOutput.WriteMarkup("  [cyan]--gpu-device[/] N           Select a specific GPU by index (use with --gpu)");
    ConsoleOutput.WriteMarkup("  [dim](no flag)[/]                 Run CPU + all GPUs (default)");
    ConsoleOutput.WriteMarkup("  [cyan]--array-size[/] N           Array size in elements (e.g. 200M, 100000000)");
    ConsoleOutput.WriteMarkup("  [cyan]--range[/] START:END:STEP   Range test multiple array sizes (e.g. 50M:200M:50M)");
    ConsoleOutput.WriteMarkup("  [cyan]--no-save[/]                Don't write CSV/JSON files");
    ConsoleOutput.WriteMarkup("  [cyan]--output-dir[/] DIR         Directory for output files (default: current dir)");
    ConsoleOutput.WriteMarkup("  [cyan]--exe[/] PATH               Explicit path to the C backend executable");
    ConsoleOutput.WriteMarkup("  [cyan]--cpu-exe[/] PATH           Explicit CPU backend path (advanced)");
    ConsoleOutput.WriteMarkup("  [cyan]--gpu-exe[/] PATH           Explicit GPU backend path (advanced)");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]AI Inference Benchmark:[/]");
    ConsoleOutput.WriteMarkup("  [cyan]--ai[/]                     Run AI inference benchmark on all available devices");
    ConsoleOutput.WriteMarkup("  [cyan]--ai-only[/]                Run AI inference only without default CPU/GPU memory passes");
    ConsoleOutput.WriteMarkup("  [cyan]--ai-device[/] LIST         Comma-separated devices: cpu, gpu, npu (default: all)");
    ConsoleOutput.WriteMarkup("  [cyan]--ai-model[/] ALIAS         Model alias to use (e.g. phi-3.5-mini, phi-4-mini)");
    ConsoleOutput.WriteMarkup("  [cyan]--ai-no-download[/]         Only use cached models (skip downloads for fast repeat runs)");
    ConsoleOutput.WriteMarkup("  [cyan]--quick-ai[/]               Fast CI mode: cached models only, 1 model per device");
    ConsoleOutput.WriteMarkup("  [cyan]--ai-backend[/] TYPE        AI backend: auto (default), foundry, lmstudio");
    ConsoleOutput.WriteMarkup("  [cyan]--help[/]                   Show this help");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]Diagnostics:[/]");
    ConsoleOutput.WriteMarkup("  Trace log: [cyan]StreamBench_trace_<timestamp>.log[/] (auto-created next to exe)");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]Examples:[/]");
    ConsoleOutput.WriteMarkup("  StreamBench                          Run CPU + all GPUs");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --array-size 200M");
    ConsoleOutput.WriteMarkup("  StreamBench --gpu --array-size 100M");
    ConsoleOutput.WriteMarkup("  StreamBench --gpu --gpu-device 0     Benchmark a specific GPU");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --range 50M:200M:50M");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --no-save");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]AI benchmark examples:[/]");
    ConsoleOutput.WriteMarkup("  StreamBench --ai                          AI benchmark on all devices (CPU/GPU/NPU)");
    ConsoleOutput.WriteMarkup("  StreamBench --ai --ai-device cpu,npu      AI benchmark on CPU and NPU only");
    ConsoleOutput.WriteMarkup("  StreamBench --ai --ai-model phi-3.5-mini  Use a specific model");
    ConsoleOutput.WriteMarkup("  StreamBench --ai --no-save                Run without saving JSON");
    ConsoleOutput.WriteMarkup("  StreamBench --ai --quick-ai               CI: fast, cached models only");
}
