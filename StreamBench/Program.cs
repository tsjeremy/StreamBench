// Program.cs — STREAM Benchmark .NET 10 CLI entry point
//
// Usage:
//   StreamBench [--cpu] [--gpu] [--gpu-device N] [--array-size N] [--ntimes N]
//               [--range START:END:STEP] [--no-save] [--output-dir DIR]
//               [--exe PATH]
//
// If neither --cpu nor --gpu is specified, both benchmarks run automatically
// and all available GPUs are benchmarked.
//
// Examples:
//   StreamBench                              # runs both CPU and GPU
//   StreamBench --cpu --array-size 200000000
//   StreamBench --gpu --array-size 100000000
//   StreamBench --cpu --range 50000000:200000000:50000000
//   StreamBench --cpu --no-save

using System.Text;
using StreamBench;
using StreamBench.Models;

Console.OutputEncoding = Encoding.UTF8;

// ── Parse arguments ────────────────────────────────────────────────────────
bool   wantCpu    = false;
bool   wantGpu    = false;
bool   modeSet    = false;   // true if user explicitly passed --cpu or --gpu
long?  arraySize  = null;
bool   noSave     = false;
string? outputDir = null;
string? exePath   = null;
int?   gpuDevice  = null;    // specific GPU device index (null = all)
long   rangeStart = 0, rangeEnd = 0, rangeStep = 50_000_000;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cpu":   wantCpu = true; modeSet = true; break;
        case "--gpu":   wantGpu = true; modeSet = true; break;
        case "--no-save": noSave = true; break;

        case "--array-size" when i + 1 < args.Length:
            arraySize = ParseSize(args[++i]);
            break;

        case "--output-dir" when i + 1 < args.Length:
            outputDir = args[++i];
            break;

        case "--exe" when i + 1 < args.Length:
            exePath = args[++i];
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

// Default: run both CPU and GPU when user didn't specify
if (!modeSet)
{
    wantCpu = true;
    wantGpu = true;
}

// ── Prepare output directory ───────────────────────────────────────────────
if (outputDir is not null)
    Directory.CreateDirectory(outputDir);

// ── Run benchmarks ─────────────────────────────────────────────────────────
int exitCode = 0;

if (wantCpu)
{
    exitCode = await RunBenchmarkAsync(isGpu: false, exePath, arraySize,
        rangeStart, rangeEnd, rangeStep, noSave, outputDir);
    if (wantGpu) Console.WriteLine();
}

if (wantGpu)
{
    int gpuCode = await RunGpuBenchmarksAsync(exePath, arraySize,
        rangeStart, rangeEnd, rangeStep, noSave, outputDir, gpuDevice);
    if (gpuCode != 0) exitCode = gpuCode;
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

    // Discover all GPUs
    var gpus = await BenchmarkRunner.ListGpusAsync(exe);

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
    int gpuCount = gpus.Count(g => g.DeviceKind == "GPU");
    int npuCount = gpus.Count(g => g.DeviceKind == "NPU");
    string deviceSummary = (gpuCount, npuCount) switch
    {
        ( > 0, > 0) => $"{gpuCount} GPU(s) + {npuCount} NPU(s)",
        (_, > 0)     => $"{npuCount} NPU(s)",
        _            => $"{gpuCount} GPU(s)",
    };
    ConsoleOutput.WriteMarkup($"[bold cyan]Discovered {deviceSummary}:[/]");
    for (int i = 0; i < gpus.Count; i++)
    {
        var g = gpus[i];
        double memGb = g.GlobalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        string displayName = g.DeviceKind == "NPU"
            ? (GpuDeviceInfo.InferNpuDisplayName(g.Name, g.Vendor) ?? g.Name)
            : g.Name;
        ConsoleOutput.WriteMarkup(
            $"  [white]#{g.Index}[/] [bold white]{displayName}[/] [dim]({g.DeviceKind}, {g.Vendor}, {memGb:F1} GB)[/]");
    }
    Console.WriteLine();

    int exitCode = 0;
    for (int i = 0; i < gpus.Count; i++)
    {
        if (i > 0) Console.WriteLine();
        var g = gpus[i];
        string displayName = g.DeviceKind == "NPU"
            ? (GpuDeviceInfo.InferNpuDisplayName(g.Name, g.Vendor) ?? g.Name)
            : g.Name;
        ConsoleOutput.WriteMarkup($"[bold cyan]── {g.DeviceKind} #{g.Index}: {displayName} ──[/]");

        int code = await RunSingleGpuAsync(exe, arraySize, rangeStart, rangeEnd, rangeStep,
            noSave, outputDir, g.Index, $"{g.DeviceKind} #{g.Index} ({displayName})");
        if (code != 0) exitCode = code;
    }
    return exitCode;
}

// ── Run a single GPU benchmark ─────────────────────────────────────────────
async Task<int> RunSingleGpuAsync(string exe, long? arraySize,
    long rangeStart, long rangeEnd, long rangeStep,
    bool noSave, string? outputDir, int? gpuDeviceIndex, string? gpuLabel)
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
            ConsoleOutput.WriteMarkup("[red]Error:[/] GPU benchmark failed or returned no output.");
            return 1;
        }
        DisplayAndSave(result, noSave, outputDir);
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
            ConsoleOutput.WriteMarkup("[red]Error:[/] Benchmark failed or returned no output.");
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
            ConsoleOutput.WriteMarkup($"[red]  Test {idx + 1} failed.[/]");
            continue;
        }

        ConsoleOutput.PrintResults(result);

        if (!noSave)
        {
            if (rangeCsv is not null)
                ResultSaver.AppendRangeCsv(rangeCsv, result);
            var jsonPath = ResultSaver.SaveJson(result, outputDir);
            if (jsonPath is not null)
                ConsoleOutput.PrintFileSaved(jsonPath);
        }

        success++;
    }

    rangeCsv?.Dispose();

    Console.WriteLine();
    ConsoleOutput.WriteMarkup(
        $"[bold white]Range complete:[/] [green]{success}/{sizes.Count}[/] tests passed.");
}

// ── Single result display + save ───────────────────────────────────────────
void DisplayAndSave(BenchmarkResult result, bool noSave, string? outputDir)
{
    ConsoleOutput.PrintBanner(result);
    ConsoleOutput.PrintSystemInfo(result);
    ConsoleOutput.PrintConfig(result);
    ConsoleOutput.PrintResults(result);

    if (!noSave)
    {
        ConsoleOutput.WriteMarkup("[bold white]Saving results...[/]");
        var csvPath  = ResultSaver.SaveCsv(result, outputDir);
        var jsonPath = ResultSaver.SaveJson(result, outputDir);
        if (csvPath  is not null) ConsoleOutput.PrintFileSaved(csvPath);
        if (jsonPath is not null) ConsoleOutput.PrintFileSaved(jsonPath);
        Console.WriteLine();
    }
}

// ── Helpers ────────────────────────────────────────────────────────────────
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
    ConsoleOutput.WriteMarkup("  [cyan]--help[/]                   Show this help");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]Examples:[/]");
    ConsoleOutput.WriteMarkup("  StreamBench                          Run CPU + all GPUs");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --array-size 200M");
    ConsoleOutput.WriteMarkup("  StreamBench --gpu --array-size 100M");
    ConsoleOutput.WriteMarkup("  StreamBench --gpu --gpu-device 0     Benchmark a specific GPU");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --range 50M:200M:50M");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --no-save");
}
