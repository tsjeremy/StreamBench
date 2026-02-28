// Program.cs — STREAM Benchmark .NET 10 CLI entry point
//
// Usage:
//   StreamBench [--cpu | --gpu] [--array-size N] [--ntimes N]
//               [--range START:END:STEP] [--no-save] [--output-dir DIR]
//               [--exe PATH]
//
// Examples:
//   StreamBench --cpu --array-size 200000000
//   StreamBench --gpu --array-size 100000000
//   StreamBench --cpu --range 50000000:200000000:50000000
//   StreamBench --cpu --no-save

using System.Text;
using StreamBench;
using StreamBench.Models;

Console.OutputEncoding = Encoding.UTF8;

// ── Parse arguments ────────────────────────────────────────────────────────
bool   isGpu      = false;
long?  arraySize  = null;
bool   noSave     = false;
string? outputDir = null;
string? exePath   = null;
long   rangeStart = 0, rangeEnd = 0, rangeStep = 50_000_000;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cpu":   isGpu = false; break;
        case "--gpu":   isGpu = true;  break;
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

// ── Locate the C backend executable ───────────────────────────────────────
string? exe = exePath ?? BenchmarkRunner.FindExecutable(isGpu);
if (exe is null)
{
    ConsoleOutput.WriteMarkup("[red]Error:[/] Could not find the C benchmark executable.");
    ConsoleOutput.WriteMarkup("[dim]Build it first, then place it in the same directory as StreamBench.[/]");
    ConsoleOutput.WriteMarkup("[dim]Expected names: stream_cpu_<os>_<arch>  or  stream_gpu_<os>_<arch>[/]");
    return 1;
}

// ── Prepare output directory ───────────────────────────────────────────────
if (outputDir is not null)
    Directory.CreateDirectory(outputDir);

// ── Single run or range testing ────────────────────────────────────────────
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

// ── Range testing loop ─────────────────────────────────────────────────────
async Task RunRangeAsync(string exe, bool isGpu,
    long start, long end, long step,
    bool noSave, string? outputDir)
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
            exe, sizes[idx], isGpu, idx, sizes.Count);

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
    ConsoleOutput.WriteMarkup("  [cyan]--cpu[/]                    Run CPU benchmark (default)");
    ConsoleOutput.WriteMarkup("  [cyan]--gpu[/]                    Run GPU benchmark");
    ConsoleOutput.WriteMarkup("  [cyan]--array-size[/] N           Array size in elements (e.g. 200M, 100000000)");
    ConsoleOutput.WriteMarkup("  [cyan]--range[/] START:END:STEP   Range test multiple array sizes (e.g. 50M:200M:50M)");
    ConsoleOutput.WriteMarkup("  [cyan]--no-save[/]                Don't write CSV/JSON files");
    ConsoleOutput.WriteMarkup("  [cyan]--output-dir[/] DIR         Directory for output files (default: current dir)");
    ConsoleOutput.WriteMarkup("  [cyan]--exe[/] PATH               Explicit path to the C backend executable");
    ConsoleOutput.WriteMarkup("  [cyan]--help[/]                   Show this help");
    Console.WriteLine();
    ConsoleOutput.WriteMarkup("[bold white]Examples:[/]");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --array-size 200M");
    ConsoleOutput.WriteMarkup("  StreamBench --gpu --array-size 100M");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --range 50M:200M:50M");
    ConsoleOutput.WriteMarkup("  StreamBench --cpu --no-save");
}
