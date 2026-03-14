// ConsoleOutput.cs
// Displays benchmark results with colored formatting using System.Console.
// Uses box-drawing characters and the .NET Console color API — no third-party dependencies.

using System.Text;
using StreamBench.Models;

namespace StreamBench;

public static class ConsoleOutput
{
    // ── Markup helpers ────────────────────────────────────────────────────

    // Tags recognized inside [tag]...[/] markup strings.
    private static readonly HashSet<string> KnownTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "white",  "bold white",
        "cyan",   "bold cyan",
        "green",  "bold green",
        "yellow", "bold yellow",
        "red",    "bold red",
        "dim",    "gray",
    };

    /// <summary>
    /// Writes a string containing optional color markup tags ([cyan], [white], [green],
    /// [red], [dim], [bold *], [/]) using Console.ForegroundColor.
    /// Unknown bracket sequences are treated as literal text.
    /// </summary>
    public static void WriteMarkup(string markup, bool newLine = true)
    {
        int i = 0;
        while (i < markup.Length)
        {
            int open = markup.IndexOf('[', i);
            if (open < 0) { Console.Write(markup[i..]); break; }
            if (open > i) Console.Write(markup[i..open]);

            int close = markup.IndexOf(']', open + 1);
            if (close < 0) { Console.Write(markup[open..]); break; }

            string tag = markup[(open + 1)..close];
            if (KnownTags.Contains(tag)) { ApplyTag(tag); i = close + 1; }
            else                         { Console.Write('['); i = open + 1; }
        }
        Console.ResetColor();
        if (newLine) Console.WriteLine();
    }

    /// <summary>Returns the plain-text content of a markup string (tags stripped).</summary>
    public static string StripMarkup(string markup)
    {
        var sb = new StringBuilder(markup.Length);
        int i = 0;
        while (i < markup.Length)
        {
            int open = markup.IndexOf('[', i);
            if (open < 0) { sb.Append(markup[i..]); break; }
            if (open > i) sb.Append(markup[i..open]);

            int close = markup.IndexOf(']', open + 1);
            if (close < 0) { sb.Append(markup[open..]); break; }

            string tag = markup[(open + 1)..close];
            if (KnownTags.Contains(tag)) i = close + 1;
            else                         { sb.Append('['); i = open + 1; }
        }
        return sb.ToString();
    }

    private static void ApplyTag(string tag)
    {
        switch (tag.ToLowerInvariant())
        {
            case "/":                              Console.ResetColor();                             break;
            case "cyan":   case "bold cyan":       Console.ForegroundColor = ConsoleColor.Cyan;     break;
            case "white":  case "bold white":      Console.ForegroundColor = ConsoleColor.White;    break;
            case "green":  case "bold green":      Console.ForegroundColor = ConsoleColor.Green;    break;
            case "yellow": case "bold yellow":     Console.ForegroundColor = ConsoleColor.Yellow;   break;
            case "red":    case "bold red":        Console.ForegroundColor = ConsoleColor.Red;      break;
            case "dim":    case "gray":            Console.ForegroundColor = ConsoleColor.DarkGray; break;
        }
    }

    // ── Simple table renderer ─────────────────────────────────────────────

    private sealed class SimpleTable
    {
        private sealed record ColumnDef(string HeaderMarkup, int MinWidth, bool RightAlign);

        private readonly string?         _titleMarkup;
        private readonly List<ColumnDef> _columns = [];
        private readonly List<string[]>  _rows    = [];

        public SimpleTable(string? titleMarkup = null) { _titleMarkup = titleMarkup; }

        public SimpleTable AddColumn(string headerMarkup, int minWidth = 0, bool rightAlign = false)
        {
            int len = StripMarkup(headerMarkup).Length;
            _columns.Add(new ColumnDef(headerMarkup, Math.Max(minWidth, len), rightAlign));
            return this;
        }

        public SimpleTable AddRow(params string[] cells)
        {
            _rows.Add(cells);
            return this;
        }

        public void Render()
        {
            int n = _columns.Count;
            int[] w = _columns.Select(c => c.MinWidth).ToArray();

            // Expand widths to fit all cell content
            foreach (var row in _rows)
                for (int i = 0; i < Math.Min(row.Length, n); i++)
                    w[i] = Math.Max(w[i], StripMarkup(row[i]).Length);

            // Total inner width (between ╭ and ╮):
            //   each column: w[i] + 2 padding  |  (n-1) inner ─ separators
            int inner = w.Sum() + n * 2 + (n - 1);

            // ── Top border ──────────────────────────────────────
            BorderColor();
            if (_titleMarkup is not null)
            {
                string title   = StripMarkup(_titleMarkup);
                int    padAll  = Math.Max(0, inner - title.Length - 2); // 2 for flanking spaces
                int    lp      = padAll / 2;
                int    rp      = padAll - lp;
                Console.Write($"╭{Dashes(lp)} ");
                Console.ResetColor();
                WriteMarkup(_titleMarkup, newLine: false);
                BorderColor();
                Console.WriteLine($" {Dashes(rp)}╮");
            }
            else
            {
                Console.Write("╭");
                for (int i = 0; i < n; i++) { Console.Write(Dashes(w[i] + 2)); Console.Write(i < n - 1 ? "┬" : "╮"); }
                Console.WriteLine();
            }
            Console.ResetColor();

            // ── Header row ───────────────────────────────────────
            WriteRow(_columns.Select(c => c.HeaderMarkup).ToArray(), w, n);

            // ── Header / data separator ──────────────────────────
            BorderColor();
            Console.Write("├");
            for (int i = 0; i < n; i++) { Console.Write(Dashes(w[i] + 2)); Console.Write(i < n - 1 ? "┼" : "┤"); }
            Console.WriteLine();
            Console.ResetColor();

            // ── Data rows ────────────────────────────────────────
            foreach (var row in _rows)
                WriteRow(row, w, n);

            // ── Bottom border ────────────────────────────────────
            BorderColor();
            Console.Write("╰");
            for (int i = 0; i < n; i++) { Console.Write(Dashes(w[i] + 2)); Console.Write(i < n - 1 ? "┴" : "╯"); }
            Console.WriteLine();
            Console.ResetColor();
        }

        private void WriteRow(string[] cells, int[] w, int n)
        {
            BorderColor(); Console.Write("│"); Console.ResetColor();
            for (int i = 0; i < n; i++)
            {
                string markup = i < cells.Length ? cells[i] : "";
                string plain  = StripMarkup(markup);
                int    pad    = w[i] - plain.Length;

                Console.Write(' ');
                if (_columns[i].RightAlign && pad > 0) Console.Write(new string(' ', pad));
                WriteMarkup(markup, newLine: false);
                if (!_columns[i].RightAlign && pad > 0) Console.Write(new string(' ', pad));
                Console.Write(' ');

                BorderColor(); Console.Write("│"); Console.ResetColor();
            }
            Console.WriteLine();
        }

        private static void   BorderColor()    => Console.ForegroundColor = ConsoleColor.DarkGray;
        private static string Dashes(int count) => new('─', count);
    }

    // ── Spinner ───────────────────────────────────────────────────────────

    private static readonly char[] SpinFrames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private static async Task SpinAsync(string label, CancellationToken token)
    {
        int  frame       = 0;
        bool savedVisible = true;
        if (OperatingSystem.IsWindows())
            try   { savedVisible = Console.CursorVisible; Console.CursorVisible = false; }
            catch { /* ignore on unsupported consoles */ }

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
            if (OperatingSystem.IsWindows())
                try { Console.CursorVisible = savedVisible; } catch { }
        }
    }

    // ── Banner ────────────────────────────────────────────────────────────

    public static void PrintBanner(BenchmarkResult r)
    {
        Console.WriteLine();
        WriteMarkup("[cyan]══════════════════════════════════════════════════════════════[/]");
        string typeLabel = r.Type;
        WriteMarkup($"[cyan]  STREAM Benchmark v{r.Version} — {typeLabel} Memory Bandwidth[/]");
        WriteMarkup("[cyan]══════════════════════════════════════════════════════════════[/]");
        WriteMarkup($"[dim]  Timestamp : {r.Timestamp}[/]");
        Console.WriteLine();
    }

    // ── System information panel ──────────────────────────────────────────

    public static void PrintSystemInfo(BenchmarkResult r)
    {
        if (r.System is null) return;
        var sys = r.System;

        var table = new SimpleTable("[bold white]System Information[/]")
            .AddColumn("[cyan]Property[/]", 22)
            .AddColumn("[white]Value[/]");

        table.AddRow("[cyan]Hostname[/]",     $"[white]{sys.Hostname}[/]");
        table.AddRow("[cyan]OS[/]",           $"[white]{sys.Os}[/]");
        table.AddRow("[cyan]Architecture[/]", $"[white]{sys.Architecture}[/]");
        table.AddRow("[cyan]CPU Model[/]",    $"[bold cyan]{sys.CpuModel}[/]");
        table.AddRow("[cyan]Logical CPUs[/]", $"[white]{sys.LogicalCpus}[/]");
        table.AddRow("[cyan]Total RAM[/]",    $"[white]{sys.TotalRamGb:F1} GB[/]");

        if (sys.CpuBaseMhz > 0)
        {
            string freq = sys.CpuMaxMhz.HasValue && sys.CpuMaxMhz.Value != sys.CpuBaseMhz
                ? $"[white]{sys.CpuBaseMhz} MHz (boost: {sys.CpuMaxMhz} MHz)[/]"
                : $"[white]{sys.CpuBaseMhz} MHz (base)[/]";
            table.AddRow("[cyan]CPU Frequency[/]", freq);
        }

        if (sys.NumaNodes > 1)
            table.AddRow("[cyan]NUMA Nodes[/]", $"[white]{sys.NumaNodes}[/]");

        table.Render();

        // Memory details
        if (r.Memory?.Modules is { Count: > 0 } modules)
        {
            var memTable = new SimpleTable("[bold white]Memory Modules[/]")
                .AddColumn("[cyan]Slot[/]",          14)
                .AddColumn("[white]Size[/]",          10)
                .AddColumn("[white]Type[/]",          10)
                .AddColumn("[white]Speed[/]",         12)
                .AddColumn("[white]Manufacturer[/]")
                .AddColumn("[dim]Part Number[/]");

            foreach (var m in modules)
            {
                string speed = m.SpeedMts == 0 ? "N/A"
                    : m.ConfiguredSpeedMts > 0 && m.ConfiguredSpeedMts != m.SpeedMts
                        ? $"{m.SpeedMts} / {m.ConfiguredSpeedMts} MT/s"
                        : $"{m.SpeedMts} MT/s";

                memTable.AddRow(
                    $"[dim]{m.Locator}[/]",
                    $"[white]{FormatSizeMb(m.SizeMb)}[/]",
                    $"[white]{m.Type}[/]",
                    $"[white]{speed}[/]",
                    $"[white]{m.Manufacturer}[/]",
                    $"[dim]{m.PartNumber}[/]");
            }

            memTable.Render();
        }

        // Cache
        var cache = r.Cache;
        if (cache is not null && (cache.L1dPerCoreKb > 0 || cache.L2PerCoreKb > 0 || cache.L3TotalKb > 0))
        {
            var cacheTable = new SimpleTable("[bold white]Cache Hierarchy[/]")
                .AddColumn("[cyan]Level[/]", 14)
                .AddColumn("[white]Size[/]");

            if (cache.L1dPerCoreKb > 0) cacheTable.AddRow("[cyan]L1d (per core)[/]", $"[white]{FormatKb(cache.L1dPerCoreKb)}[/]");
            if (cache.L1iPerCoreKb > 0) cacheTable.AddRow("[cyan]L1i (per core)[/]", $"[white]{FormatKb(cache.L1iPerCoreKb)}[/]");
            if (cache.L2PerCoreKb > 0)  cacheTable.AddRow("[cyan]L2  (per core)[/]", $"[white]{FormatKb(cache.L2PerCoreKb)}[/]");
            if (cache.L3TotalKb > 0)    cacheTable.AddRow("[cyan]L3  (total)[/]",    $"[white]{FormatKb(cache.L3TotalKb)}[/]");

            cacheTable.Render();
        }

        PrintDeviceInfo(r);

        Console.WriteLine();
    }

    /// <summary>
    /// Prints the GPU device info box. Called separately so it can be
    /// shown for every device even when system info is printed only once.
    /// </summary>
    public static void PrintDeviceInfo(BenchmarkResult r)
    {
        if (r.Device is null) return;

        var dev = r.Device;
        string displayName = GpuDeviceInfo.InferGpuDisplayName(dev.Name, dev.Vendor) ?? dev.Name;

        var gpuTable = new SimpleTable("[bold white]GPU Device[/]")
            .AddColumn("[cyan]Property[/]", 22)
            .AddColumn("[white]Value[/]");

        gpuTable.AddRow("[cyan]Device[/]",         $"[bold cyan]{displayName}[/]");
        if (!displayName.Equals(dev.Name, StringComparison.Ordinal))
            gpuTable.AddRow("[cyan]OpenCL Name[/]", $"[dim]{dev.Name}[/]");
        gpuTable.AddRow("[cyan]Type[/]",            $"[white]GPU[/]");
        gpuTable.AddRow("[cyan]Vendor[/]",          $"[white]{dev.Vendor}[/]");
        gpuTable.AddRow("[cyan]Compute Units[/]",   $"[white]{dev.ComputeUnits}[/]");
        gpuTable.AddRow("[cyan]Max Frequency[/]",   $"[white]{dev.MaxFrequencyMhz} MHz[/]");
        gpuTable.AddRow("[cyan]Global Memory[/]",   $"[white]{dev.GlobalMemoryGib:F1} GiB[/]");
        gpuTable.AddRow("[cyan]Max Work Group[/]",  $"[white]{dev.MaxWorkGroupSize}[/]");

        gpuTable.Render();
    }

    // ── Benchmark configuration summary ───────────────────────────────────

    public static void PrintConfig(BenchmarkResult r)
    {
        var cfg = r.Config;
        WriteMarkup(
            $"[cyan]Array size  :[/] [white]{cfg.ArraySizeElements:N0} elements " +
            $"({cfg.ArraySizeMib:F0} MiB / array, {cfg.TotalMemoryMib:F0} MiB total)[/]");
        WriteMarkup(
            $"[cyan]Iterations  :[/] [white]{cfg.Ntimes} (best result reported)[/]");
        WriteMarkup(
            $"[cyan]Precision   :[/] [white]{cfg.BytesPerElement * 8}-bit ({(cfg.BytesPerElement == 8 ? "double" : "float")})[/]");
        Console.WriteLine();
    }

    // ── Results table ─────────────────────────────────────────────────────

    public static void PrintResults(BenchmarkResult r)
    {
        var kernels = new[]
        {
            ("Copy",  r.Results.Copy,  false),
            ("Scale", r.Results.Scale, false),
            ("Add",   r.Results.Add,   false),
            ("Triad", r.Results.Triad, true),   // key result — highlighted
        };

        // Auto-detect unit: use GB/s if all rates >= 1000 MB/s (i.e. >= 1 GB/s)
        double maxRate = kernels.Max(k => k.Item2.BestRateMbps);
        bool useGbps = maxRate >= 1000.0;
        string unitLabel = useGbps ? "Best Rate GB/s" : "Best Rate MB/s";
        double divisor = useGbps ? 1000.0 : 1.0;

        var table = new SimpleTable("[bold white]Benchmark Results[/]")
            .AddColumn("[bold white]Kernel[/]",              10)
            .AddColumn($"[bold green]{unitLabel}[/]",        16, rightAlign: true)
            .AddColumn("[white]Avg Time (s)[/]",             14, rightAlign: true)
            .AddColumn("[white]Min Time (s)[/]",             14, rightAlign: true)
            .AddColumn("[white]Max Time (s)[/]",             14, rightAlign: true);

        foreach (var (name, k, isKey) in kernels)
        {
            double displayRate = k.BestRateMbps / divisor;
            string rateFormat = useGbps ? "N2" : "N1";

            // Triad is the key bandwidth number; use bold yellow to make it stand out
            string nameCol = isKey ? $"[bold yellow]{name}[/]" : $"[cyan]{name}[/]";
            string rateCol = isKey
                ? $"[bold yellow]{displayRate.ToString(rateFormat)}[/]"
                : $"[bold green]{displayRate.ToString(rateFormat)}[/]";

            table.AddRow(
                nameCol,
                rateCol,
                $"[white]{k.AvgTimeSec:F6}[/]",
                $"[white]{k.MinTimeSec:F6}[/]",
                $"[white]{k.MaxTimeSec:F6}[/]");
        }

        table.Render();
        Console.WriteLine();

        bool passed = r.Validation == "passed";
        if (passed)
            WriteMarkup("[green][PASS] Solution validated: average error within tolerance on all arrays.[/]");
        else
            WriteMarkup("[red][FAIL] Validation FAILED: results may be incorrect.[/]");

        Console.WriteLine();
    }

    // ── Progress display while C backend is running ───────────────────────

    public static async Task<BenchmarkResult?> RunWithSpinnerAsync(
        string executablePath,
        long? arraySize,
        bool isGpu,
        int testIndex = 0,
        int totalTests = 1,
        int? gpuDeviceIndex = null,
        string? gpuLabel = null)
    {
        string label = gpuLabel ?? (isGpu ? "GPU" : "CPU");
        string sizeLabel = arraySize.HasValue ? $"{arraySize.Value / 1_000_000.0:F0}M elements" : "default size";
        string progressText = totalTests > 1
            ? $"Running {label} STREAM [{testIndex + 1}/{totalTests}] — {sizeLabel}"
            : $"Running {label} STREAM — {sizeLabel}";

        using var cts = new CancellationTokenSource();
        Task spinnerTask = SpinAsync(progressText, cts.Token);

        BenchmarkResult? result = await BenchmarkRunner.RunAsync(
            executablePath, arraySize, gpuDeviceIndex);

        await cts.CancelAsync();
        await spinnerTask;

        return result;
    }

    // ── File saved notification ───────────────────────────────────────────

    public static void PrintFileSaved(string path)
    {
        WriteMarkup($"[cyan]  -> Saved:[/] [white]{path}[/]");
    }

    // ── AI benchmark results ──────────────────────────────────────────────

    /// <summary>
    /// Prints a formatted table for one AI device benchmark result.
    /// Shows model info, then a two-row table for Q1 (cold) and Q2 (warm).
    /// </summary>
    public static void PrintAiResult(StreamBench.Models.AiDeviceBenchmarkResult r)
    {
        Console.WriteLine();
        WriteMarkup("[cyan]──────────────────────────────────────────────────────────────[/]");
        WriteMarkup($"[cyan]  AI Inference Benchmark — {r.DeviceType}[/]");
        WriteMarkup("[cyan]──────────────────────────────────────────────────────────────[/]");

        var infoTable = new SimpleTable("[bold white]Model Info[/]")
            .AddColumn("[cyan]Property[/]", 22)
            .AddColumn("[white]Value[/]");
        infoTable.AddRow("[cyan]Device[/]",             $"[bold white]{r.DeviceType}[/]");
        infoTable.AddRow("[cyan]Model ID[/]",            $"[white]{r.ModelId}[/]");
        infoTable.AddRow("[cyan]Alias[/]",               $"[white]{r.ModelAlias}[/]");
        if (!string.IsNullOrWhiteSpace(r.ExecutionProvider))
            infoTable.AddRow("[cyan]Execution Provider[/]",  $"[white]{r.ExecutionProvider}[/]");
        infoTable.AddRow("[cyan]Timestamp[/]",           $"[dim]{r.Timestamp}[/]");
        infoTable.Render();

        var table = new SimpleTable("[bold white]Inference Timing[/]")
            .AddColumn("[bold white]Run[/]",            26)
            .AddColumn("[white]Model Load (s)[/]",       16, rightAlign: true)
            .AddColumn("[white]Response (s)[/]",         14, rightAlign: true)
            .AddColumn("[bold green]Total (s)[/]",        11, rightAlign: true)
            .AddColumn("[white]Tokens Out[/]",            12, rightAlign: true)
            .AddColumn("[bold cyan]Tok/sec[/]",           10, rightAlign: true);

        table.AddRow(
            $"[cyan]Q1 (cold, incl. load)[/]",
            $"[white]{r.Run1.ModelLoadSec:F3}[/]",
            $"[white]{r.Run1.ResponseTimeSec:F3}[/]",
            $"[bold green]{r.Run1.TotalTimeSec:F3}[/]",
            $"[white]{r.Run1.CompletionTokens}[/]",
            $"[bold cyan]{r.Run1.TokensPerSecond:F1}[/]");

        table.AddRow(
            $"[cyan]Q2 (warm)[/]",
            $"[dim]—[/]",
            $"[white]{r.Run2.ResponseTimeSec:F3}[/]",
            $"[bold green]{r.Run2.ResponseTimeSec:F3}[/]",
            $"[white]{r.Run2.CompletionTokens}[/]",
            $"[bold cyan]{r.Run2.TokensPerSecond:F1}[/]");

        table.Render();

        WriteMarkup($"[bold yellow]  Q1 (cold):[/] [white]{r.Question1}[/]");
        WriteMultilineAnswer(r.Run1.ResponseText);
        Console.WriteLine();
        WriteMarkup($"[bold yellow]  Q2 (warm):[/] [white]{r.Question2}[/]");
        WriteMultilineAnswer(r.Run2.ResponseText);
        Console.WriteLine();
    }

    /// <summary>
    /// Prints summary comparison tables for the two-pass AI benchmark.
    /// Pass 1: shared model comparison. Pass 2: best-per-device performance.
    /// When a relation summary is provided, extra relation questions (Q3+) are shown.
    /// </summary>
    public static void PrintAiSummary(
        AiBenchmarkTwoPassResult twoPassResult,
        AiLocalRelationSummaryResult? relationSummary = null)
    {
        var relationQuestions = relationSummary?.Questions
            .Where(q => q.Index >= 3 && q.Run is not null)
            .OrderBy(q => q.Index)
            .ThenBy(q => q.DeviceType, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        // Best-per-device first: shows peak throughput each device can achieve
        if (twoPassResult.BestPerDeviceResults.Count > 0)
        {
            // Check if any model actually differs from the shared pass
            bool hasDifferentModels = twoPassResult.SharedResults.Count > 0
                && twoPassResult.BestPerDeviceResults.Any(bpd =>
                {
                    var shared = twoPassResult.SharedResults.FirstOrDefault(
                        s => s.DeviceType.Equals(bpd.DeviceType, StringComparison.OrdinalIgnoreCase));
                    return shared is null || !shared.ModelId.Equals(bpd.ModelId, StringComparison.OrdinalIgnoreCase);
                });

            Console.WriteLine();
            WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
            WriteMarkup("[bold cyan]  Best-Per-Device Performance (fastest model per device)[/]");
            WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
            if (!hasDifferentModels && twoPassResult.SharedResults.Count > 0)
                WriteMarkup("[dim]  (All devices used the same model as the shared comparison)[/]");
            else if (hasDifferentModels)
                WriteMarkup("[dim]  Note: Different models per device — not a direct device comparison.[/]");
            Console.WriteLine();

            PrintAiComparisonTable(twoPassResult.BestPerDeviceResults);
        }

        // Shared model last: the fair apples-to-apples device comparison (final result)
        if (twoPassResult.SharedResults.Count > 0)
        {
            Console.WriteLine();
            WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
            WriteMarkup("[bold cyan]  Shared Model Device Comparison (same model, fair comparison)[/]");
            WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
            Console.WriteLine();

            PrintAiComparisonTable(twoPassResult.SharedResults);
        }

        // Relation analysis questions (Q3+): timing + answer preview
        if (relationQuestions.Count > 0)
        {
            string summaryDevice = string.IsNullOrWhiteSpace(relationSummary!.SummaryDeviceType)
                ? "unknown"
                : relationSummary.SummaryDeviceType!;

            Console.WriteLine();
            WriteMarkup("[bold cyan]── Cross-Device Relation Analysis Questions ──[/]");
            if (relationSummary.Models is { Count: > 0 })
            {
                WriteMarkup("[dim]  Models by device:[/]");
                foreach (var model in relationSummary.Models)
                {
                    string ep = string.IsNullOrWhiteSpace(model.ExecutionProvider)
                        ? "unknown"
                        : model.ExecutionProvider;
                    WriteMarkup($"[dim]    {model.DeviceType}: {model.ModelAlias} ({ep})[/]");
                }
            }
            else
            {
                WriteMarkup($"[dim]  Model: {relationSummary.ModelAlias} ({summaryDevice})[/]");
            }
            Console.WriteLine();
            foreach (var q in relationQuestions)
            {
                string questionDevice = string.IsNullOrWhiteSpace(q.DeviceType) ? summaryDevice : q.DeviceType!;
                WriteMarkup($"[bold green]  Q{q.Index} [{questionDevice}] Response:[/] {q.Run!.ResponseTimeSec:F3}s  " +
                    $"[bold cyan]{q.Run.TokensPerSecond:F1} tok/s[/]  " +
                    $"[white]{q.Run.CompletionTokens} tokens[/]");
                WriteMarkup($"[bold yellow]  Q{q.Index} [{questionDevice}]:[/] [white]{q.Question}[/]");
                WriteMultilineAnswer(q.Answer);
                Console.WriteLine();
            }
        }
    }

    /// <summary>Renders one AI device comparison table (Q1/Q2 only).</summary>
    private static void PrintAiComparisonTable(
        IReadOnlyList<AiDeviceBenchmarkResult> results)
    {
        if (results.Count == 0) return;

        // Find the fastest tok/s in Q1 and Q2 to highlight
        double maxQ1Tps = results.Max(r => r.Run1.TokensPerSecond);
        double maxQ2Tps = results.Max(r => r.Run2.TokensPerSecond);

        var table = new SimpleTable("[bold white]Device Comparison[/]")
            .AddColumn("[bold white]Device[/]",     8)
            .AddColumn("[white]Model[/]",           30)
            .AddColumn("[white]Load (s)[/]",         10, rightAlign: true)
            .AddColumn("[white]Q1 Total (s)[/]",     14, rightAlign: true)
            .AddColumn("[bold cyan]Q1 Tok/s[/]",     10, rightAlign: true)
            .AddColumn("[white]Q2 Total (s)[/]",     14, rightAlign: true)
            .AddColumn("[bold cyan]Q2 Tok/s[/]",     10, rightAlign: true);

        foreach (var r in results)
        {
            // Highlight the fastest tok/s in bold yellow
            string q1TpsMarkup = r.Run1.TokensPerSecond >= maxQ1Tps && results.Count > 1
                ? $"[bold yellow]{r.Run1.TokensPerSecond:F1}[/]"
                : $"[bold cyan]{r.Run1.TokensPerSecond:F1}[/]";
            string q2TpsMarkup = r.Run2.TokensPerSecond >= maxQ2Tps && results.Count > 1
                ? $"[bold yellow]{r.Run2.TokensPerSecond:F1}[/]"
                : $"[bold cyan]{r.Run2.TokensPerSecond:F1}[/]";

            table.AddRow(
                $"[bold white]{r.DeviceType}[/]",
                $"[dim]{r.ModelAlias}[/]",
                $"[white]{r.Run1.ModelLoadSec:F3}[/]",
                $"[bold green]{r.Run1.TotalTimeSec:F3}[/]",
                q1TpsMarkup,
                $"[bold green]{r.Run2.ResponseTimeSec:F3}[/]",
                q2TpsMarkup);
        }

        table.Render();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints local-AI relation summary generated from local JSON files.
    /// </summary>
    public static void PrintAiRelationSummary(AiLocalRelationSummaryResult summary)
    {
        Console.WriteLine();
        WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
        WriteMarkup("[bold cyan]  Local JSON Relation Summary — Memory Bandwidth vs AI[/]");
        WriteMarkup("[bold cyan]══════════════════════════════════════════════════════════════[/]");
        WriteMarkup($"[dim]  Source folder: {summary.SourceDirectory}[/]");
        WriteMarkup($"[dim]  Files parsed: memory {summary.MemoryJsonFiles}, AI {summary.AiJsonFiles}[/]");
        WriteMarkup($"[dim]  Samples parsed: memory {summary.MemorySamples}, AI {summary.AiSamples}[/]");
        string summaryDevice = string.IsNullOrWhiteSpace(summary.SummaryDeviceType)
            ? "unknown"
            : summary.SummaryDeviceType!;
        if (summary.Models is { Count: > 0 })
        {
            WriteMarkup("[dim]  Models by device:[/]");
            foreach (var model in summary.Models)
            {
                string ep = string.IsNullOrWhiteSpace(model.ExecutionProvider)
                    ? "unknown"
                    : model.ExecutionProvider;
                WriteMarkup($"[dim]    {model.DeviceType}: {model.ModelAlias} ({ep})[/]");
            }
        }
        else
        {
            string modelMeta = string.IsNullOrWhiteSpace(summary.ExecutionProvider)
                ? summaryDevice
                : $"{summaryDevice}, {summary.ExecutionProvider}";
            WriteMarkup($"[dim]  Model: {summary.ModelAlias} ({modelMeta})[/]");
        }
        Console.WriteLine();

        if (summary.DeviceAggregates.Count > 0)
        {
            var table = new SimpleTable("[bold white]Device Aggregates[/]")
                .AddColumn("[bold white]Device[/]",            8)
                .AddColumn("[white]Mem Samples[/]",            13, rightAlign: true)
                .AddColumn("[white]Avg Triad GB/s[/]",         16, rightAlign: true)
                .AddColumn("[white]AI Samples[/]",             11, rightAlign: true)
                .AddColumn("[bold cyan]Avg Warm Tok/s[/]",     16, rightAlign: true);

            foreach (var d in summary.DeviceAggregates)
            {
                table.AddRow(
                    $"[bold white]{d.DeviceType}[/]",
                    $"[white]{d.MemorySamples}[/]",
                    $"[white]{d.AvgMemoryTriadGbps:F2}[/]",
                    $"[white]{d.AiSamples}[/]",
                    $"[bold cyan]{d.AvgAiWarmTokensPerSecond:F2}[/]");
            }

            table.Render();
            Console.WriteLine();
        }

        if (summary.DeviceLevelCorrelation.HasValue)
            WriteMarkup($"[bold white]Device-level correlation:[/] [bold cyan]{summary.DeviceLevelCorrelation.Value:F3}[/]");
        else
            WriteMarkup("[bold white]Device-level correlation:[/] [dim]insufficient paired device data[/]");

        foreach (var qa in summary.Questions
            .OrderBy(q => q.Index)
            .ThenBy(q => q.DeviceType, StringComparer.OrdinalIgnoreCase))
        {
            string questionDevice = string.IsNullOrWhiteSpace(qa.DeviceType) ? summaryDevice : qa.DeviceType!;
            Console.WriteLine();
            WriteMarkup($"[bold yellow]  Q{qa.Index} [{questionDevice}]:[/] [white]{qa.Question}[/]");
            WriteMultilineAnswer(qa.Answer);
        }

        // ── Relation question inference timing table (Q1+) ──
        var qWithRun = summary.Questions
            .Where(q => q.Run is not null)
            .OrderBy(q => q.Index)
            .ThenBy(q => q.DeviceType, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (qWithRun.Count > 0)
        {
            Console.WriteLine();
            var qTable = new SimpleTable("[bold white]Relation Summary — Inference Timing[/]")
                .AddColumn("[bold white]Run[/]",        34)
                .AddColumn("[white]Response (s)[/]",    14, rightAlign: true)
                .AddColumn("[bold cyan]Tok/s[/]",       10, rightAlign: true)
                .AddColumn("[white]Tokens Out[/]",      12, rightAlign: true);

            foreach (var qa in qWithRun)
            {
                string questionDevice = string.IsNullOrWhiteSpace(qa.DeviceType) ? summaryDevice : qa.DeviceType!;
                string phase = qa.Index switch
                {
                    1 => "cold",
                    2 => "warm",
                    _ => "relation summary",
                };
                string label = $"Q{qa.Index} [{questionDevice}] ({phase})";
                qTable.AddRow(
                    $"[bold white]{label}[/]",
                    $"[white]{qa.Run!.ResponseTimeSec:F3}[/]",
                    $"[bold cyan]{qa.Run.TokensPerSecond:F1}[/]",
                    $"[white]{qa.Run.CompletionTokens}[/]");
            }

            qTable.Render();
        }

        Console.WriteLine();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static void WriteMultilineAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            WriteMarkup("[gray]  │ (no response)[/]");
            return;
        }

        // Determine max content width for word-wrapping
        int maxWidth;
        try { maxWidth = Math.Max(60, Console.WindowWidth - 6); } // 6 = "  │ " prefix + margin
        catch { maxWidth = 114; } // fallback for redirected output

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                WriteMarkup("[gray]  │[/]");
                continue;
            }

            // Word-wrap long lines
            if (line.Length <= maxWidth)
            {
                WriteMarkup($"[gray]  │[/] [white]{line}[/]");
            }
            else
            {
                foreach (var wrappedLine in WordWrap(line, maxWidth))
                    WriteMarkup($"[gray]  │[/] [white]{wrappedLine}[/]");
            }
        }
    }

    /// <summary>Wraps text at word boundaries to fit within maxWidth characters.</summary>
    private static IEnumerable<string> WordWrap(string text, int maxWidth)
    {
        if (maxWidth <= 0 || text.Length <= maxWidth)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int remaining = text.Length - start;
            if (remaining <= maxWidth)
            {
                yield return text[start..];
                break;
            }

            // Find the last space within maxWidth
            int breakAt = text.LastIndexOf(' ', start + maxWidth, maxWidth);
            if (breakAt <= start)
            {
                // No space found — hard break
                yield return text.Substring(start, maxWidth);
                start += maxWidth;
            }
            else
            {
                yield return text[start..breakAt];
                start = breakAt + 1; // skip the space
            }
        }
    }

    private static string FormatKb(int kb) =>
        kb >= 1024 ? $"{kb / 1024} MB" : $"{kb} KB";

    private static string FormatSizeMb(int mb) =>
        mb >= 1024 ? $"{mb / 1024} GB" : $"{mb} MB";
}
