#if ENABLE_AI
// FoundryAiBackend.cs
// IAiBackend implementation for Microsoft Foundry Local.
// Extracted from AiBenchmarkRunner — all Foundry CLI + REST lifecycle management.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StreamBench;

internal sealed class FoundryAiBackend : IAiBackend
{
    public string Name => "Foundry Local";
    public bool SupportsDeviceTargeting => true;

    private string? _cli;
    private string? _serviceUrl;

    // ── Preferred model aliases by device (from original AiBenchmarkRunner) ──

    private static readonly string[] PreferredAliasesCpu =
    [
        "phi-4-mini",
        "phi-3.5-mini",
        "phi-3-mini-4k",
        "phi-3-mini-128k",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
    ];

    private static readonly string[] PreferredAliasesGpu =
    [
        "phi-4-mini",
        "phi-3.5-mini",
        "phi-3-mini-4k",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
    ];

    private static readonly string[] PreferredAliasesNpu =
    [
        "qwen2.5-0.5b",
        "phi-4-mini",
        "phi-3-mini-4k",
        "phi-3-mini-128k",
        "qwen2.5-7b",
    ];

    private static readonly string[] SharedAliasPriorityList =
    [
        "phi-4-mini",
        "phi-3.5-mini",
        "phi-3-mini-4k",
        "phi-3-mini-128k",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
        "phi-4-mini-reasoning",
    ];

    private const string DefaultBootstrapAlias = "phi-3.5-mini";

    // ── IAiBackend implementation ───────────────────────────────────────────

    public bool IsAvailable()
    {
        _cli ??= FindFoundryCli();
        return _cli is not null;
    }

    public async Task<string?> StartAsync(CancellationToken ct = default)
    {
        _cli ??= FindFoundryCli();
        if (_cli is null)
        {
            TraceLog.AiCliNotFound("foundry, foundrylocal");
            return null;
        }

        _serviceUrl = await StartServiceAsync(_cli);
        return _serviceUrl;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_cli))
            await StopServiceAsync(_cli);
    }

    public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        if (_cli is null) return [];
        var foundryModels = await ListFoundryModelsAsync(_cli);
        return foundryModels
            .Select(m => new AiModelInfo(m.Id, m.Alias, m.DeviceType, m.ExecutionProvider, m.FileSizeMb, m.IsCached, Name))
            .ToList();
    }

    public async Task<string?> LoadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
    {
        if (_cli is null) return null;
        bool ok = await LoadFoundryModelAsync(_cli, modelIdOrAlias, noDownload: false, timeoutMs: 600_000, device: "");
        return ok ? modelIdOrAlias : null;
    }

    public async Task UnloadModelAsync(string modelId, CancellationToken ct = default)
    {
        if (_cli is null) return;
        await UnloadFoundryModelAsync(_cli, modelId);
    }

    public async Task<bool> DownloadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
    {
        if (_cli is null) return false;
        var (exitCode, _) = await DownloadWithProgressAsync(_cli, modelIdOrAlias, 600_000);
        return exitCode == 0;
    }

    public IReadOnlyList<string> GetPreferredAliases(string deviceType) =>
        deviceType.ToUpperInvariant() switch
        {
            "GPU" => PreferredAliasesGpu,
            "NPU" => PreferredAliasesNpu,
            _ => PreferredAliasesCpu,
        };

    public IReadOnlyList<string> GetSharedAliasPriority() => SharedAliasPriorityList;

    // ── Foundry-specific public helpers (used by AiBenchmarkRunner) ─────────

    /// <summary>Returns the raw Foundry CLI path (for advanced callers).</summary>
    internal string? CliPath => _cli;

    /// <summary>Returns the service URL (for advanced callers).</summary>
    internal string? ServiceUrl => _serviceUrl;

    /// <summary>
    /// Bootstraps model catalog, including first-run EP download and default model download.
    /// Returns the full list of available models.
    /// </summary>
    internal async Task<List<AiModelInfo>> BootstrapCatalogAsync(bool noDownload, CancellationToken ct = default)
    {
        if (_cli is null) return [];

        ConsoleOutput.WriteMarkup("[dim]  Loading model catalog (first run may download execution providers)...[/]");
        var allModels = await ListFoundryModelsAsync(_cli, firstRunTimeoutMs: 120_000);

        // Retry once after delay
        if (allModels.Count == 0)
        {
            TraceLog.DiagnosticInfo("Catalog empty on first attempt; retrying after 8 s delay");
            ConsoleOutput.WriteMarkup("[dim]  Catalog empty — waiting for Foundry service to initialise...[/]");
            await Task.Delay(8_000, ct);
            allModels = await ListFoundryModelsAsync(_cli);
        }

        // Bootstrap with default model if still empty
        if (allModels.Count == 0 && !noDownload)
        {
            TraceLog.DiagnosticInfo($"Catalog still empty; bootstrapping with default model '{DefaultBootstrapAlias}'");
            ConsoleOutput.WriteMarkup(
                $"[yellow][INFO][/] No models in catalog — downloading default model [white]{DefaultBootstrapAlias}[/]...");
            ConsoleOutput.WriteMarkup("[dim]  This is a one-time download and may take several minutes.[/]");

            var (dlExit, _) = await DownloadWithProgressAsync(_cli, DefaultBootstrapAlias, 600_000);
            if (dlExit == 0)
            {
                TraceLog.DiagnosticInfo($"Bootstrap download of '{DefaultBootstrapAlias}' succeeded; refreshing catalog");
                ConsoleOutput.WriteMarkup($"[dim]  Download of {DefaultBootstrapAlias} complete — refreshing catalog...[/]");
                allModels = await ListFoundryModelsAsync(_cli);
            }
            else
            {
                ConsoleOutput.WriteMarkup($"[yellow][WARN][/] Default model download failed.");
            }
        }

        return allModels
            .Select(m => new AiModelInfo(m.Id, m.Alias, m.DeviceType, m.ExecutionProvider, m.FileSizeMb, m.IsCached, Name))
            .ToList();
    }

    /// <summary>Restart the Foundry service (for crash recovery).</summary>
    internal async Task<string?> RestartServiceAsync()
    {
        if (_cli is null) return null;
        await StopServiceAsync(_cli);
        await Task.Delay(3_000);
        _serviceUrl = await StartServiceAsync(_cli);
        return _serviceUrl;
    }

    // ── Foundry CLI helpers (extracted from AiBenchmarkRunner) ──────────────

    private static string? FindFoundryCli()
    {
        var triedNames = new[] { "foundry", "foundrylocal" };

        foreach (var name in triedNames)
        {
            if (TryProbeCli(name) is string found) return found;
        }

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
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: Homebrew installs to /opt/homebrew/bin (ARM64) or /usr/local/bin (x64)
            var macPaths = new[] { "/opt/homebrew/bin", "/usr/local/bin" };
            foreach (var dir in macPaths)
            {
                foreach (var name in triedNames)
                {
                    var fullPath = Path.Combine(dir, name);
                    if (File.Exists(fullPath) && TryProbeCli(fullPath) is string found3) return found3;
                }
            }
        }

        TraceLog.AiCliNotFound(string.Join(", ", triedNames));
        return null;
    }

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

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunFoundryAsync(
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
            await Task.WhenAny(waitForExitTask, Task.Delay(5_000));

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
        Task<string> readTask, int timeoutMs, string command, string streamName)
    {
        var completedTask = await Task.WhenAny(readTask, Task.Delay(timeoutMs));
        if (completedTask == readTask)
            return await readTask;

        TraceLog.Info($"Timed out reading foundry {streamName} for command: {command} (timeout={timeoutMs}ms)");
        return "";
    }

    private static async Task<string?> StartServiceAsync(string cli)
    {
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

        TraceLog.AiServiceStarting();
        ConsoleOutput.WriteMarkup("[dim]  Starting Foundry Local service (this may take up to 60 s on first run)...[/]");
        var (startExit, startOut, startErr) = await RunFoundryAsync(cli, "service start", 60_000);

        var serviceUrl = ExtractServiceUrl(startOut + "\n" + startErr);
        if (serviceUrl is not null)
        {
            TraceLog.AiServiceStarted();
            return serviceUrl;
        }

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

        if (startExit == 0)
        {
            TraceLog.AiServiceStarted();
            return "http://127.0.0.1:5273";
        }

        TraceLog.AiServiceStartFailed(
            $"foundry service start failed (exit={startExit}): {startErr}", "FoundryAiBackend.cs", 0);
        return null;
    }

    private static string? ExtractServiceUrl(string text)
    {
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

    // ── Foundry model record (internal to this backend) ─────────────────────

    internal sealed record FoundryModel(
        string Id, string Alias, string DeviceType,
        string ExecutionProvider, double FileSizeMb, bool IsCached);

    // ── Model catalog ──────────────────────────────────────────────────────

    private static async Task<List<FoundryModel>> ListFoundryModelsAsync(
        string cli, int firstRunTimeoutMs = 180_000)
    {
        var sw = Stopwatch.StartNew();
        var models = new List<FoundryModel>();

        // First-run EP downloads can take minutes — show a heartbeat so the user
        // knows we haven't frozen.
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(15_000, heartbeatCts.Token);
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    ConsoleOutput.WriteMarkup(
                        $"[dim]  ⏳ Still loading model catalog... ({sw.Elapsed.TotalSeconds:F0}s elapsed)[/]");
                    await Task.Delay(15_000, heartbeatCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
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
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }
    }

    private static List<FoundryModel> TryParseModelListJson(string json)
    {
        var models = new List<FoundryModel>();
        try
        {
            int arrayStart = json.IndexOf('[');
            if (arrayStart < 0) return models;
            string jsonTrimmed = json[arrayStart..];

            using var doc = JsonDocument.Parse(jsonTrimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return models;

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
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
        var models = new List<FoundryModel>();
        string lastAlias = "";

        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.StartsWith("Alias", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Device", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Model ID", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.StartsWith('-') || trimmed.StartsWith('─'))
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

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

            string device = "CPU";
            if (fields.Length > 0)
            {
                string firstField = fields[0];
                if (firstField.Equals("GPU", StringComparison.OrdinalIgnoreCase)) device = "GPU";
                else if (firstField.Equals("NPU", StringComparison.OrdinalIgnoreCase)) device = "NPU";
                else if (firstField.Equals("CPU", StringComparison.OrdinalIgnoreCase)) device = "CPU";
            }

            string id = fields[^1];
            if (id.Length < 4 || id.All(c => c == '-' || char.IsDigit(c) || c == '.'))
                continue;

            models.Add(new FoundryModel(id, alias, device, "", 0, IsCached: false));
        }
        return models;
    }

    // ── Model load/unload/download ─────────────────────────────────────────

    private static async Task<bool> LoadFoundryModelAsync(
        string cli, string modelId, bool noDownload = false, int timeoutMs = 600_000, string device = "")
    {
        TraceLog.AiModelLoading(modelId, device);
        ConsoleOutput.WriteMarkup($"[dim]  Loading model {modelId} (may take several minutes for large models)...[/]");
        var loadSw = Stopwatch.StartNew();

        var (exitCode, stdout, stderr) = await RunFoundryAsync(cli, $"model load \"{modelId}\"", timeoutMs);
        if (exitCode == 0)
        {
            TraceLog.AiModelLoaded(modelId, loadSw.ElapsedMilliseconds);
            return true;
        }

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

            (exitCode, _, stderr) = await RunFoundryAsync(cli, $"model load \"{modelId}\"", timeoutMs);
            if (exitCode == 0)
            {
                TraceLog.AiModelLoaded(modelId, loadSw.ElapsedMilliseconds);
                return true;
            }
        }

        TraceLog.AiModelLoadFailed(modelId, stderr, "FoundryAiBackend.cs", 0);
        return false;
    }

    private static async Task UnloadFoundryModelAsync(string cli, string modelId)
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

    internal static async Task<(int ExitCode, string Stderr)> DownloadWithProgressAsync(
        string cli, string modelId, int timeoutMs = 600_000)
    {
        var spinChars = new[] { '|', '/', '-', '\\' };
        int spinIdx = 0;
        string lastInfo = $"Downloading {modelId}...";
        var stderrSb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        bool canOverwrite = !Console.IsOutputRedirected;
        bool hasSpinnerLine = false;
        bool hasError = false;

        var psi = new ProcessStartInfo(cli, $"model download \"{modelId}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
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
                // Don't show error text on the spinner — it creates garbled output
                // with multiple spinner frames showing the error before process exits.
                hasError = true;
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var processTask = process.WaitForExitAsync();

        while (!processTask.IsCompleted)
        {
            await Task.WhenAny(processTask, Task.Delay(350));

            // Stop updating spinner once an error has been received
            if (hasError) continue;

            if (canOverwrite)
            {
                char spin = spinChars[spinIdx++ % spinChars.Length];
                string info = lastInfo;
                string elapsed = $"{sw.Elapsed.TotalSeconds:F0}s";
                string msg = $"  {spin} {info} ({elapsed})";
                if (msg.Length > 119) msg = msg[..116] + "...";
                Console.Write("\r" + msg.PadRight(120));
                hasSpinnerLine = true;
            }
            else if (sw.ElapsedMilliseconds % 10_000 < 350)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  ... still downloading ({sw.Elapsed.TotalSeconds:F0}s)");
                Console.ResetColor();
            }

            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                TraceLog.AiProcessTimeout($"model download {modelId}", timeoutMs);
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAny(processTask, Task.Delay(5_000));
                break;
            }
        }

        await Task.WhenAny(processTask, Task.Delay(2_000));

        if (hasSpinnerLine && canOverwrite)
            Console.Write("\r" + new string(' ', 121) + "\r");

        sw.Stop();
        return (process.HasExited ? process.ExitCode : -1, stderrSb.ToString());
    }
}
#endif
