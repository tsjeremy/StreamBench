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
        "white", "bold white",
        "cyan",  "bold cyan",
        "green", "bold green",
        "red",   "bold red",
        "dim",   "gray",
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
                Console.WriteLine($"╭{Dashes(lp)} {title} {Dashes(rp)}╮");
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
        WriteMarkup($"[cyan]  STREAM Benchmark v{r.Version} — {r.Type} Memory Bandwidth[/]");
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
                ? $"[white]{sys.CpuBaseMhz} MHz (max: {sys.CpuMaxMhz} MHz)[/]"
                : $"[white]{sys.CpuBaseMhz} MHz[/]";
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
                string speed = m.ConfiguredSpeedMts > 0 && m.ConfiguredSpeedMts != m.SpeedMts
                    ? $"{m.SpeedMts} / {m.ConfiguredSpeedMts} MT/s"
                    : $"{m.SpeedMts} MT/s";

                memTable.AddRow(
                    $"[dim]{m.Locator}[/]",
                    $"[white]{m.SizeMb} MB[/]",
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

        // GPU device info
        if (r.Device is not null)
        {
            var dev = r.Device;
            var gpuTable = new SimpleTable("[bold white]GPU Device[/]")
                .AddColumn("[cyan]Property[/]", 22)
                .AddColumn("[white]Value[/]");

            gpuTable.AddRow("[cyan]Device[/]",         $"[bold cyan]{dev.Name}[/]");
            gpuTable.AddRow("[cyan]Vendor[/]",          $"[white]{dev.Vendor}[/]");
            gpuTable.AddRow("[cyan]Compute Units[/]",   $"[white]{dev.ComputeUnits}[/]");
            gpuTable.AddRow("[cyan]Max Frequency[/]",   $"[white]{dev.MaxFrequencyMhz} MHz[/]");
            gpuTable.AddRow("[cyan]Global Memory[/]",   $"[white]{dev.GlobalMemoryGib:F1} GiB[/]");
            gpuTable.AddRow("[cyan]Max Work Group[/]",  $"[white]{dev.MaxWorkGroupSize}[/]");

            gpuTable.Render();
        }

        Console.WriteLine();
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
        WriteMarkup("[bold white]══ Benchmark Results ══[/]");
        Console.WriteLine();

        var table = new SimpleTable()
            .AddColumn("[bold white]Kernel[/]",         10)
            .AddColumn("[bold green]Best Rate MB/s[/]", 16, rightAlign: true)
            .AddColumn("[white]Avg Time (s)[/]",        14, rightAlign: true)
            .AddColumn("[white]Min Time (s)[/]",        14, rightAlign: true)
            .AddColumn("[white]Max Time (s)[/]",        14, rightAlign: true);

        var kernels = new[]
        {
            ("Copy",  r.Results.Copy),
            ("Scale", r.Results.Scale),
            ("Add",   r.Results.Add),
            ("Triad", r.Results.Triad),
        };

        foreach (var (name, k) in kernels)
        {
            table.AddRow(
                $"[cyan]{name}[/]",
                $"[bold green]{k.BestRateMbps:N1}[/]",
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
        int totalTests = 1)
    {
        string label = isGpu ? "GPU" : "CPU";
        string sizeLabel = arraySize.HasValue ? $"{arraySize.Value / 1_000_000.0:F0}M elements" : "default size";
        string progressText = totalTests > 1
            ? $"Running {label} STREAM [{testIndex + 1}/{totalTests}] — {sizeLabel}"
            : $"Running {label} STREAM — {sizeLabel}";

        using var cts = new CancellationTokenSource();
        Task spinnerTask = SpinAsync(progressText, cts.Token);

        BenchmarkResult? result = await BenchmarkRunner.RunAsync(executablePath, arraySize);

        await cts.CancelAsync();
        await spinnerTask;

        return result;
    }

    // ── File saved notification ───────────────────────────────────────────

    public static void PrintFileSaved(string path)
    {
        WriteMarkup($"[cyan]  -> Saved:[/] [white]{path}[/]");
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static string FormatKb(int kb) =>
        kb >= 1024 ? $"{kb / 1024} MB" : $"{kb} KB";
}
