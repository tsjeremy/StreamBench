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

    // Preferred aliases by device — ordered by answer quality then speed.
    // phi-3.5-mini promoted to #1: qwen2.5-1.5b/0.5b are faster but produce
    // factually incorrect answers (wrong formulas, wrong math, hallucinations).
    // Large/slow models (14B+, reasoning, deepseek-r1) removed to avoid timeouts.
    private static readonly string[] PreferredAliasesCpu =
    [
        "phi-3.5-mini",        // 101s dl, 42s warm, 765 tok — correct answers
        "phi-4-mini",          // 208s dl, 48s warm, 740 tok — smart, NPU support
        "phi-3-mini-4k",       // 98s dl, 20s warm, 325 tok — compact, decent quality
        "phi-3-mini-128k",     // 113s dl, 21s warm, 320 tok — long context variant
        "qwen2.5-1.5b",        // 84s dl, 6s warm, 332 tok — fast but unreliable answers
        "qwen2.5-0.5b",        // 39s dl, 2s warm, 302 tok — fastest, low quality
        "qwen2.5-7b",          // 229s dl, 59s warm, 585 tok — larger, slower
    ];

    private static readonly string[] PreferredAliasesGpu =
    [
        "phi-3.5-mini",        // 88s dl, 8s warm, 710 tok — correct answers
        "phi-4-mini",          // 136s dl, 8s warm, 835 tok — smart, NPU support
        "phi-3-mini-4k",       // 80s dl, 3s warm, 246 tok — compact, decent quality
        "qwen2.5-1.5b",        // 59s dl, 2s warm, 360 tok — fast but unreliable answers
        "qwen2.5-0.5b",        // 29s dl, 4s warm, 784 tok — fastest, low quality
        "qwen2.5-7b",          // 188s dl, 12s warm, 659 tok — larger, slower
    ];

    private static readonly string[] PreferredAliasesNpu =
    [
        "qwen2.5-0.5b",        // lightest — most likely to succeed on NPU
        "phi-4-mini",
        "phi-3-mini-4k",
        "phi-3-mini-128k",
        "qwen2.5-7b",
        // Reasoning models (phi-4-mini-reasoning, deepseek-r1-*) intentionally
        // excluded — they generate internal chain-of-thought tokens that make
        // NPU inference extremely slow (>300 s), causing HttpClient timeouts.
    ];

    // Shared-model priorities used when benchmarking multiple devices side-by-side.
    // phi-3.5-mini promoted to #1 for answer correctness (qwen2.5 models produce
    // factual errors in Q2/Q3 answers). phi-4-mini #2 for NPU support.
    // Large/slow models (deepseek-r1-*, 14B+) removed — they caused service crashes
    // and inference timeouts in the 2026-03-05 benchmark trace.
    private static readonly string[] SharedAliasPriority =
    [
        "phi-3.5-mini",        // best quality, CPU+GPU coverage
        "phi-4-mini",          // smart, adds NPU coverage
        "phi-3-mini-4k",       // compact, decent quality
        "phi-3-mini-128k",     // same as 4k but with longer context
        "qwen2.5-1.5b",        // fast fallback — lower answer quality
        "qwen2.5-0.5b",        // tiny fallback — lowest quality
        "qwen2.5-7b",          // larger but quality results
        "phi-4-mini-reasoning", // reasoning model — slower but available on NPU
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Foundry CLI helpers ───────────────────────────────────────────────

    /// <summary>Default model alias used for bootstrapping when no models are cached.</summary>
    private const string DefaultBootstrapAlias = "phi-3.5-mini";

    /// <summary>Finds the foundry CLI executable.</summary>
    private static string? FindFoundryCli()
    {
        var triedNames = new[] { "foundry", "foundrylocal" };

        // 1. Try names on PATH (works when MSIX alias is visible)
        foreach (var name in triedNames)
        {
            if (TryProbeCli(name) is string found) return found;
        }

        // 2. Probe well-known MSIX app execution alias paths (Windows).
        //    After winget install the alias lives under WindowsApps but may
        //    not be visible in the current process's PATH until the terminal
        //    is restarted. Probing the full path works around this.
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
                foreach (var name in triedNames)
                {
                    var fullPath = Path.Combine(windowsApps, name + ".exe");
                    if (File.Exists(fullPath) && TryProbeCli(fullPath) is string found2) return found2;
                }
            }
        }

        TraceLog.AiCliNotFound(string.Join(", ", triedNames));
        return null;
    }

    /// <summary>Probes a single CLI name/path and returns it on success.</summary>
    private static string? TryProbeCli(string nameOrPath)
    {
        try
        {
            var psi = new ProcessStartInfo(nameOrPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit(5000);
            if (p.ExitCode == 0)
            {
                TraceLog.AiCliFound(nameOrPath);
                return nameOrPath;
            }
        }
        catch (Exception ex)
        {
            TraceLog.DiagnosticInfo($"CLI probe failed for '{nameOrPath}': {ex.Message}");
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

        var waitForExitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(timeoutMs));
        if (completedTask != waitForExitTask)
        {
            TraceLog.AiProcessTimeout($"{cli} {arguments}", timeoutMs);
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { TraceLog.DiagnosticInfo($"Process kill failed: {ex.Message}"); }
            await Task.WhenAny(waitForExitTask, Task.Delay(2_000));

            string command = $"{cli} {arguments}";
            string timedOutStdout = await ReadTextWithTimeoutAsync(stdoutTask, 500, command, "stdout");
            string timedOutStderr = await ReadTextWithTimeoutAsync(stderrTask, 500, command, "stderr");
            if (string.IsNullOrWhiteSpace(timedOutStderr))
                timedOutStderr = "Timeout waiting for foundry CLI";
            return (-1, timedOutStdout, timedOutStderr);
        }

        await waitForExitTask;
        string fullCommand = $"{cli} {arguments}";
        string stdout = await ReadTextWithTimeoutAsync(stdoutTask, 5_000, fullCommand, "stdout");
        string stderr = await ReadTextWithTimeoutAsync(stderrTask, 5_000, fullCommand, "stderr");
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadTextWithTimeoutAsync(
        Task<string> readTask,
        int timeoutMs,
        string command,
        string streamName)
    {
        var completedTask = await Task.WhenAny(readTask, Task.Delay(timeoutMs));
        if (completedTask == readTask)
            return await readTask;

        TraceLog.Warn(
            $"Timed out reading foundry {streamName} for command: {command} (timeout={timeoutMs}ms)");
        return "";
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

        // Some Foundry CLI versions return success without printing URL on start;
        // query status again to capture the actual bound endpoint.
        if (startExit == 0)
        {
            var (_, statusOutAfterStart, statusErrAfterStart) = await RunFoundryAsync(cli, "service status", 30_000);
            serviceUrl = ExtractServiceUrl(statusOutAfterStart + "\n" + statusErrAfterStart);
            if (serviceUrl is not null)
            {
                TraceLog.AiServiceStarted();
                return serviceUrl;
            }
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
            TraceLog.AiServiceStopping();
            await RunFoundryAsync(cli, "service stop", 15_000);
            TraceLog.AiServiceStopped();
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"StopService: {ex.Message}");
        }
    }

    // ── Model catalog via CLI ─────────────────────────────────────────────

    /// <summary>Model info parsed from foundry CLI JSON output.</summary>
    internal sealed record FoundryModel(
        string Id,
        string Alias,
        string DeviceType,
        string ExecutionProvider,
        double FileSizeMb,
        bool IsCached);

    /// <summary>Lists all available models from the foundry catalog.</summary>
    /// <param name="firstRunTimeoutMs">Timeout for first-run EP download (default 180 s).</param>
    private static async Task<List<FoundryModel>> ListModelsAsync(string cli, int firstRunTimeoutMs = 180_000)
    {
        var sw = Stopwatch.StartNew();
        var models = new List<FoundryModel>();

        // Try JSON output first
        var (exitCode, stdout, _) = await RunFoundryAsync(cli, "model list --json", firstRunTimeoutMs);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            var jsonModels = TryParseModelListJson(stdout);
            if (jsonModels.Count > 0)
            {
                TraceLog.AiCatalogLoaded(jsonModels.Count, sw.ElapsedMilliseconds);
                return jsonModels;
            }
            TraceLog.DiagnosticInfo("JSON model list parse returned 0 models; falling back to text format");
        }

        // Fallback: parse text output
        (exitCode, stdout, _) = await RunFoundryAsync(cli, "model list", firstRunTimeoutMs);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            models = ParseModelListText(stdout);
            TraceLog.AiCatalogLoaded(models.Count, sw.ElapsedMilliseconds);
            return models;
        }

        TraceLog.AiCatalogUnavailable($"model list failed (exit={exitCode})");
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

        // Reject junk catalog entries (e.g. "Autoregistration", "Valid", "🕛", "EPs...")
        // that sometimes appear in `foundry model list --json` output.
        if (alias.Length < 3
            || !alias.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_')
            || !alias.Any(char.IsLetterOrDigit))
        {
            DiagnosticHelper.LogWarning($"Skipping invalid alias '{alias}' (id={id})");
            return;
        }

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
    private static async Task<bool> LoadModelAsync(string cli, string modelId,
        bool noDownload = false, int timeoutMs = 300_000, string device = "")
    {
        TraceLog.AiModelLoading(modelId, device);
        var loadSw = Stopwatch.StartNew();

        // Try loading directly first (succeeds if model is already cached)
        var (exitCode, stdout, stderr) = await RunFoundryAsync(cli, $"model load \"{modelId}\"", timeoutMs);
        if (exitCode == 0)
        {
            TraceLog.AiModelLoaded(modelId, loadSw.ElapsedMilliseconds);
            return true;
        }

        // If load failed because model isn't downloaded, download it first
        string combined = (stdout + " " + stderr).ToLowerInvariant();
        if (combined.Contains("not found locally") || combined.Contains("download")
            || combined.Contains("bad request") || combined.Contains("not cached"))
        {
            if (noDownload)
            {
                TraceLog.AiModelDownloadSkipped(modelId, device);
                ConsoleOutput.WriteMarkup($"[yellow][SKIP][/] Model not cached and --ai-no-download is set: {modelId}");
                return false;
            }

            ConsoleOutput.WriteMarkup($"[dim]  Model not cached — downloading {modelId}...[/]");
            TraceLog.AiModelDownloadStarted(modelId, 0);

            var dlSw = Stopwatch.StartNew();
            var (dlExit, dlErr) = await DownloadWithProgressAsync(cli, modelId, 600_000);
            dlSw.Stop();

            if (dlExit != 0)
            {
                TraceLog.AiModelDownloadFailed(modelId, dlErr);
                ConsoleOutput.WriteMarkup($"[red]  Download failed for {modelId}[/]");
                return false;
            }

            TraceLog.AiModelDownloadCompleted(modelId, dlSw.ElapsedMilliseconds);
            ConsoleOutput.WriteMarkup($"[dim]  Download complete ({dlSw.Elapsed.TotalSeconds:F1}s) — loading {modelId}...[/]");

            // Retry load after download
            (exitCode, _, stderr) = await RunFoundryAsync(cli, $"model load \"{modelId}\"", timeoutMs);
            if (exitCode == 0)
            {
                TraceLog.AiModelLoaded(modelId, loadSw.ElapsedMilliseconds);
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

    /// <summary>
    /// Downloads a model via the foundry CLI, showing a live spinner and any output
    /// the CLI emits, with a carriage-return overwrite style matching the Foundry SDK
    /// <c>DownloadAsync(progress =&gt; Console.Write($"\rDownloading: {progress:F2}%"))</c> UX.
    /// </summary>
    private static async Task<(int ExitCode, string Stderr)> DownloadWithProgressAsync(
        string cli, string modelId, int timeoutMs = 600_000)
    {
        // Spinner characters — rotate every tick for a live "working" indicator.
        var spinChars = new[] { '|', '/', '-', '\\' };
        int spinIdx = 0;

        // lastInfo is written from event-handler threads and read from the spinner loop.
        // string assignment is atomic for reference types in .NET, so no lock needed.
        string lastInfo = $"Downloading {modelId}...";
        var stderrSb = new StringBuilder();
        var sw = Stopwatch.StartNew();

        // \r overwrites only work in interactive terminals; skip in CI/piped output.
        bool canOverwrite = !Console.IsOutputRedirected;
        bool hasSpinnerLine = false;

        var psi = new ProcessStartInfo(cli, $"model download \"{modelId}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };

        // Update lastInfo with each non-empty output line from the CLI.
        // The spinner loop owns all console writes — event handlers only update the string.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
                lastInfo = line.Trim();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                stderrSb.AppendLine(line);
                lastInfo = line.Trim();
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var processTask = process.WaitForExitAsync();

        // Spinner loop: update the display every ~350 ms until the process exits.
        while (!processTask.IsCompleted)
        {
            await Task.WhenAny(processTask, Task.Delay(350));

            if (canOverwrite)
            {
                char spin = spinChars[spinIdx++ % spinChars.Length];
                string info = lastInfo;   // snapshot — atomic read
                string elapsed = $"{sw.Elapsed.TotalSeconds:F0}s";
                string msg = $"  {spin} {info} ({elapsed})";
                if (msg.Length > 119) msg = msg[..116] + "...";
                Console.Write("\r" + msg.PadRight(120));
                hasSpinnerLine = true;
            }
            else if (sw.ElapsedMilliseconds % 10_000 < 350)
            {
                // Non-interactive fallback: print a heartbeat every ~10 s.
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  ... still downloading ({sw.Elapsed.TotalSeconds:F0}s)");
                Console.ResetColor();
            }

            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                TraceLog.AiProcessTimeout($"model download {modelId}", timeoutMs);
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAny(processTask, Task.Delay(2_000));
                break;
            }
        }

        // Ensure the process has fully exited so output buffers are flushed.
        await Task.WhenAny(processTask, Task.Delay(2_000));

        // Clear the spinner line so subsequent WriteMarkup starts on a clean line.
        if (hasSpinnerLine && canOverwrite)
            Console.Write("\r" + new string(' ', 121) + "\r");

        sw.Stop();
        return (process.HasExited ? process.ExitCode : -1, stderrSb.ToString());
    }

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
    /// Session context returned by RunAsync for reuse by the relation summary.
    /// Avoids redundant Foundry service start and catalog reload.
    /// Caller should call <see cref="StopSessionAsync"/> when done.
    /// </summary>
    internal sealed class AiSession
    {
        public string Cli { get; init; } = "";
        public string ServiceUrl { get; init; } = "";
        public List<FoundryModel> Catalog { get; init; } = [];

        public async Task StopAsync()
        {
            if (!string.IsNullOrEmpty(Cli))
                await StopServiceAsync(Cli);
        }
    }

    /// <summary>
    /// Runs the AI inference benchmark on the requested device(s).
    /// Returns a two-pass result: shared model comparison + best-per-device performance.
    /// The returned <see cref="AiSession"/> can be passed to <see cref="RunLocalRelationSummaryAsync"/>
    /// to avoid a redundant Foundry service restart.
    /// Caller should call <c>session.StopAsync()</c> after all AI work is complete.
    /// When sharedOnly is true, the best-per-device pass is skipped.
    /// When noDownload is true, only already-cached models are used.
    /// </summary>
    internal static async Task<(AiBenchmarkTwoPassResult Result, AiSession? Session)> RunAsync(
        IEnumerable<string>? devices = null,
        string? modelAlias = null,
        bool sharedOnly = false,
        bool noDownload = false,
        bool quickMode = false,
        CancellationToken cancellationToken = default)
    {
        // Defence-in-depth: prevent sleep even when called outside the normal Program.cs flow.
        using var _sleep = SleepPreventer.Acquire();

        var sharedResults = new List<AiDeviceBenchmarkResult>();
        var bestPerDeviceResults = new List<AiDeviceBenchmarkResult>();

        string? cli = FindFoundryCli();
        if (cli is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Foundry Local CLI not found (foundry / foundrylocal).");
            ConsoleOutput.WriteMarkup("[dim]  Install: winget install Microsoft.FoundryLocal[/]");
            ConsoleOutput.WriteMarkup("[dim]  If already installed, restart your terminal (MSIX alias requires a new session).[/]");
            return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), null);
        }

        string? serviceUrl = await StartServiceAsync(cli);
        if (serviceUrl is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Cannot start Foundry Local service.");
            ConsoleOutput.WriteMarkup("[dim]  Try: foundry service start[/]");
            return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), null);
        }

        TraceLog.DiagnosticInfo($"Foundry service URL: {serviceUrl}");
        ConsoleOutput.WriteMarkup($"[bold cyan]Starting Microsoft AI Foundry Local service...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Service URL: {serviceUrl}[/]");

        List<FoundryModel> allModels = [];
        try
        {
            // First-run note: 'foundry model list' may download execution providers (EPs)
            // for the user's hardware on first invocation, which can take several minutes.
            // ListModelsAsync uses a 180 s timeout per attempt; we use 120 s for the
            // initial call (reduced from 300 s — the original 5-min wait was too long
            // when the service is responsive but EP download simply isn't needed).
            ConsoleOutput.WriteMarkup("[dim]  Loading model catalog (first run may download execution providers)...[/]");
            allModels = await ListModelsAsync(cli, firstRunTimeoutMs: 120_000);

            // First-run: the catalog index may still be initialising — retry once after a short delay.
            if (allModels.Count == 0)
            {
                TraceLog.DiagnosticInfo("Catalog empty on first attempt; retrying after 8 s delay");
                ConsoleOutput.WriteMarkup("[dim]  Catalog empty — waiting for Foundry service to initialise...[/]");
                await Task.Delay(8_000, cancellationToken);
                allModels = await ListModelsAsync(cli);
            }

            // If still empty, attempt to bootstrap by downloading a well-known default model.
            if (allModels.Count == 0 && !noDownload)
            {
                TraceLog.DiagnosticInfo($"Catalog still empty; bootstrapping with default model '{DefaultBootstrapAlias}'");
                ConsoleOutput.WriteMarkup(
                    $"[yellow][INFO][/] No models in catalog — downloading default model [white]{DefaultBootstrapAlias}[/]...");
                ConsoleOutput.WriteMarkup("[dim]  This is a one-time download and may take several minutes.[/]");

                var (dlExit, dlErr) = await DownloadWithProgressAsync(cli, DefaultBootstrapAlias, 600_000);
                if (dlExit == 0)
                {
                    TraceLog.DiagnosticInfo($"Bootstrap download of '{DefaultBootstrapAlias}' succeeded; refreshing catalog");
                    ConsoleOutput.WriteMarkup($"[dim]  Download of {DefaultBootstrapAlias} complete — refreshing catalog...[/]");
                    allModels = await ListModelsAsync(cli);
                }
                else
                {
                    TraceLog.DiagnosticInfo($"Bootstrap download failed (exit={dlExit}): {dlErr}");
                    ConsoleOutput.WriteMarkup($"[yellow][WARN][/] Default model download failed: {dlErr}");
                }
            }

            if (allModels.Count == 0)
            {
                TraceLog.AiCatalogUnavailable("No models found in catalog after retry and bootstrap");
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No models found in catalog.");
                ConsoleOutput.WriteMarkup("[dim]  On a fresh install, try: foundry model run phi-3.5-mini[/]");
                ConsoleOutput.WriteMarkup("[dim]  Then re-run the AI benchmark.[/]");
                await StopServiceAsync(cli);
                return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), null);
            }

            // Log a summary of device types available in the catalog
            LogCatalogDeviceTypes(allModels);

            var targetDevices = ParseDeviceFilter(devices).ToList();
            var sharedPassDevices = targetDevices.ToList();

            // Quick mode (--quick-ai): cached models only, skip shared pass, 1 model per device.
            if (quickMode)
            {
                noDownload = true;
                sharedOnly = false; // we want per-device, not shared
                TraceLog.DiagnosticInfo("Quick mode: noDownload=true, skipping shared pass");
                ConsoleOutput.WriteMarkup("[dim]  Quick mode: using cached models only, 1 model per device.[/]");
            }

            TraceLog.DiagnosticInfo($"Target devices: {string.Join(", ", targetDevices)}, noDownload: {noDownload}, sharedOnly: {sharedOnly}, quickMode: {quickMode}");

            // Drop NPU from shared pass if no NPU models exist in the catalog —
            // avoids wasting time trying every shared alias on a device that has no EP.
            bool npuTargeted = sharedPassDevices.Any(d => d.Equals("NPU", StringComparison.OrdinalIgnoreCase));
            bool npuModelsExist = allModels.Any(m => m.DeviceType.Equals("NPU", StringComparison.OrdinalIgnoreCase));
            if (npuTargeted && !npuModelsExist)
            {
                sharedPassDevices.RemoveAll(d => d.Equals("NPU", StringComparison.OrdinalIgnoreCase));
                targetDevices.RemoveAll(d => d.Equals("NPU", StringComparison.OrdinalIgnoreCase));
                TraceLog.DiagnosticInfo("NPU removed from target devices — no NPU models in catalog");

                // Probe for NPU hardware to provide a better diagnostic message
                var npuHwInfo = SystemInfoDetector.DetectNpuHardware();
                if (npuHwInfo is not null)
                {
                    TraceLog.NpuHardwareDetected(npuHwInfo);
                    ConsoleOutput.WriteMarkup(
                        $"[yellow][WARN][/] NPU hardware detected ([white]{npuHwInfo}[/]) but no compatible AI models found in Foundry catalog.");
                    ConsoleOutput.WriteMarkup("[dim]  Try updating Foundry Local: winget upgrade Microsoft.FoundryLocal[/]");
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

            string? effectiveAlias = modelAlias;
            bool strictAlias = false;
            bool allowNpuFailFast = string.IsNullOrWhiteSpace(modelAlias) && targetDevices.Count > 1;

            // ── Pass 1: Multi-device side-by-side comparison with shared model ──
            if (!quickMode && string.IsNullOrWhiteSpace(effectiveAlias) && sharedPassDevices.Count > 1)
            {
                var sharedCandidates = SelectSharedAliasCandidates(allModels, sharedPassDevices);
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
                            var model = FindBestModel(allModels, deviceType, sharedAlias, strictAlias);
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
                                cli, serviceUrl, model, deviceType, sharedNoDownload, cancellationToken);
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
                                if (consecutiveFailures >= 2 && !serviceRestarted)
                                {
                                    TraceLog.Warn("2 consecutive failures in shared pass — attempting Foundry service restart");
                                    ConsoleOutput.WriteMarkup("[yellow][WARN][/] Multiple failures detected — restarting Foundry service...");
                                    await StopServiceAsync(cli);
                                    await Task.Delay(3_000, cancellationToken);
                                    var newUrl = await StartServiceAsync(cli);
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
                    var model = FindBestModel(allModels, deviceType, effectiveAlias, strictAlias);
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
                        cli, serviceUrl, model, deviceType, noDownload, cancellationToken);
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
                    var bestModel = FindBestModel(allModels, deviceType, aliasHint: null, strictAlias: false);
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
                        cli, serviceUrl, bestModel, deviceType, noDownload, cancellationToken);
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
            ConsoleOutput.WriteMarkup($"[red]Error:[/] Foundry Local service error: {ex.Message}");
            ConsoleOutput.WriteMarkup($"[dim]  {diag}[/]");
        }

        // Return session so caller can reuse it for relation summary and stop it when done.
        // Service is NOT stopped here — caller is responsible for calling session.StopAsync().
        var session = new AiSession { Cli = cli, ServiceUrl = serviceUrl, Catalog = allModels };
        return (new AiBenchmarkTwoPassResult(sharedResults, bestPerDeviceResults), session);
    }

    /// <summary>
    /// Runs local-AI relation questions over benchmark JSON files in the
    /// specified directory. Questions are executed per available target device.
    /// When <paramref name="existingCli"/>, <paramref name="existingServiceUrl"/>, and
    /// <paramref name="existingCatalog"/> are provided, the service and catalog are reused
    /// instead of starting a new Foundry session (saves ~200 s).
    /// When <paramref name="existingAiResults"/> is provided, Q1/Q2 answers are copied
    /// from the existing benchmark instead of re-running inference.
    /// </summary>
    internal static async Task<AiLocalRelationSummaryResult?> RunLocalRelationSummaryAsync(
        string directoryPath,
        string? modelAlias = null,
        IEnumerable<string>? devices = null,
        string? existingCli = null,
        string? existingServiceUrl = null,
        List<FoundryModel>? existingCatalog = null,
        AiBenchmarkTwoPassResult? existingAiResults = null,
        CancellationToken cancellationToken = default)
    {
        using var _sleep = SleepPreventer.Acquire();

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

        // Reuse existing Foundry session if provided
        bool ownsService = false;
        string? cli = existingCli ?? FindFoundryCli();
        if (cli is null)
        {
            ConsoleOutput.WriteMarkup("[red][FAIL][/] Foundry Local CLI not found for relation summary.");
            return null;
        }

        string? serviceUrl = existingServiceUrl;
        if (serviceUrl is null)
        {
            serviceUrl = await StartServiceAsync(cli);
            ownsService = true;
            if (serviceUrl is null)
            {
                ConsoleOutput.WriteMarkup("[red][FAIL][/] Cannot start Foundry Local service for relation summary.");
                return null;
            }
        }

        ConsoleOutput.WriteMarkup("[bold cyan]Running local-AI relation summary from saved JSON files...[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Source folder: {sourceDir}[/]");
        ConsoleOutput.WriteMarkup($"[dim]  Files: {dataset.MemoryFileCount} memory JSON, {dataset.AiFileCount} AI JSON[/]");
        if (existingServiceUrl is not null)
            ConsoleOutput.WriteMarkup("[dim]  Reusing existing Foundry service session[/]");

        try
        {
            var allModels = existingCatalog ?? await ListModelsAsync(cli);
            if (allModels.Count == 0)
            {
                ConsoleOutput.WriteMarkup("[yellow][WARN][/] No local AI models available for relation summary.");
                return null;
            }

            bool strictAlias = !string.IsNullOrWhiteSpace(modelAlias);
            var targetDevices = ParseDeviceFilter(devices);
            TraceLog.DiagnosticInfo(
                $"Relation summary target devices: {string.Join(", ", targetDevices)}");

            var answers = new List<AiRelationQuestionAnswer>();
            var selectedModels = new List<AiRelationModelSelection>();

            foreach (var deviceType in targetDevices)
            {
                var triedModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool deviceSucceeded = false;

                // Retry loop: if the first model fails (e.g. NPU timeout), try the
                // next preferred model for this device before giving up.
                const int maxRetries = 2;
                for (int attempt = 0; attempt < maxRetries && !deviceSucceeded; attempt++)
                {
                    var model = FindBestModel(allModels, deviceType, modelAlias, strictAlias,
                        excludeIds: triedModelIds);
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
                    if (!await LoadModelAsync(cli, model.Id, device: deviceType))
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
                            if (i < 2 && existingAiResults is not null)
                            {
                                var existingResult = existingAiResults.SharedResults
                                    .Concat(existingAiResults.BestPerDeviceResults)
                                    .FirstOrDefault(r =>
                                        r.DeviceType.Equals(deviceType, StringComparison.OrdinalIgnoreCase)
                                        && r.ModelAlias.Equals(model.Alias, StringComparison.OrdinalIgnoreCase));

                                if (existingResult is not null)
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
                        await UnloadModelAsync(cli, model.Id);
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
                Models: selectedModels);
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
                await StopServiceAsync(cli);
        }
    }

    // ── Core benchmark for a single model ─────────────────────────────────

    private static async Task<AiDeviceBenchmarkResult?> BenchmarkModelAsync(
        string cli, string serviceUrl, FoundryModel model,
        string deviceLabel, bool noDownload, CancellationToken ct)
    {
        // Load model
        ConsoleOutput.WriteMarkup("[dim]  Loading model...[/]");
        var loadSw = Stopwatch.StartNew();
        if (!await LoadModelAsync(cli, model.Id, noDownload, device: deviceLabel))
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
                Version:           VersionInfo.Version,
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

        var preferredAliases = GetPreferredAliases(deviceLabel);

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

    private static IReadOnlyList<string> GetPreferredAliases(string deviceLabel) =>
        deviceLabel.ToUpperInvariant() switch
        {
            "GPU" => PreferredAliasesGpu,
            "NPU" => PreferredAliasesNpu,
            _     => PreferredAliasesCpu,
        };

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

    /// <summary>
    /// Logs a summary of device types found in the model catalog.
    /// </summary>
    private static void LogCatalogDeviceTypes(IReadOnlyList<FoundryModel> allModels)
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
            // Only consider aliases explicitly listed in SharedAliasPriority —
            // this prevents junk/unknown aliases from bloating the shared pass.
            .Where(x => Array.Exists(SharedAliasPriority,
                p => p.Equals(x.Alias, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (byAlias.Count == 0)
            return [];

        int PriorityIndex(string alias)
        {
            int idx = Array.FindIndex(SharedAliasPriority,
                p => p.Equals(alias, StringComparison.OrdinalIgnoreCase));
            return idx < 0 ? int.MaxValue : idx;
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

    private static LocalRelationDataset ReadLocalRelationDataset(string directoryPath)
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
