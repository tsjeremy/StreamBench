#if ENABLE_AI
// OllamaAiBackend.cs
// IAiBackend implementation for Ollama.
// Cross-platform (Windows, macOS, Linux) — uses ollama CLI + OpenAI-compatible REST API.

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace StreamBench;

internal sealed class OllamaAiBackend : IAiBackend
{
    public string Name => "Ollama";
    public bool SupportsDeviceTargeting => false;

    private string _serviceUrl;

    private const string DefaultEndpoint = "http://127.0.0.1:11434";

    // Recommended models for benchmarking, ordered by quality/speed balance.
    private static readonly string[] PreferredModels =
    [
        "phi4-mini",
        "phi3.5",
        "phi3",
        "qwen2.5:1.5b",
        "qwen2.5:0.5b",
        "qwen2.5:7b",
        "llama3.2:3b",
        "llama3.2:1b",
        "gemma2:2b",
        "gemma4:26b",
        "mistral",
    ];

    public OllamaAiBackend(string? endpoint = null)
    {
        _serviceUrl = endpoint?.TrimEnd('/') ?? DefaultEndpoint;
    }

    // ── IAiBackend implementation ───────────────────────────────────────────

    public bool IsAvailable()
    {
        if (FindOllamaCli() is not null) return true;
        if (IsServerRunning()) return true;
        TraceLog.AiCliNotFound("ollama (PATH, /usr/local/bin, /opt/homebrew/bin)");
        return false;
    }

    public async Task<string?> StartAsync(CancellationToken ct = default)
    {
        // Check if server is already running
        if (IsServerRunning())
        {
            TraceLog.DiagnosticInfo($"Ollama server already running at {_serviceUrl}");
            return _serviceUrl;
        }

        // Try starting via CLI
        string? cli = FindOllamaCli();
        if (cli is null)
        {
            TraceLog.AiCliNotFound("ollama (PATH, /usr/local/bin, /opt/homebrew/bin)");
            TraceLog.AiServiceStartFailed("Ollama CLI not found and server not running", "OllamaAiBackend.cs", 0);
            return null;
        }

        TraceLog.AiServiceStarting();
        ConsoleOutput.WriteMarkup("[dim]  Starting Ollama server...[/]");

        try
        {
            var psi = new ProcessStartInfo(cli, "serve")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch (Exception ex)
        {
            TraceLog.DiagnosticWarning($"Failed to start ollama serve: {ex.Message}", "OllamaAiBackend.cs", 0, nameof(StartAsync));
        }

        // Poll for readiness — up to 30 seconds
        for (int i = 0; i < 30; i++)
        {
            if (IsServerRunning())
            {
                TraceLog.AiServiceStarted();
                TraceLog.DiagnosticInfo($"Ollama server started at {_serviceUrl}");
                return _serviceUrl;
            }
            await Task.Delay(1_000, ct);
        }

        TraceLog.AiServiceStartFailed("Ollama server failed to start", "OllamaAiBackend.cs", 0);
        return null;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        // Ollama runs as a persistent service — don't stop it
        TraceLog.DiagnosticInfo("Ollama stop requested — skipped (persistent service)");
        return Task.CompletedTask;
    }

    public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var models = new List<AiModelInfo>();
        string gpuBackendType = await DetectGpuBackendAsync(ct);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetFromJsonAsync<JsonElement>(
                $"{_serviceUrl}/api/tags", ct);

            if (response.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in modelsArr.EnumerateArray())
                {
                    string name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip embedding models
                    if (IsNonChatModel(name)) continue;

                    double sizeMb = 0;
                    if (model.TryGetProperty("size", out var sz))
                        sizeMb = sz.GetInt64() / (1024.0 * 1024.0);

                    string alias = ExtractAlias(name);
                    models.Add(new AiModelInfo(
                        Id: name,
                        Alias: alias,
                        DeviceType: "GPU",
                        ExecutionProvider: gpuBackendType,
                        FileSizeMb: sizeMb,
                        IsCached: true,
                        BackendName: Name));
                }
            }

            TraceLog.AiCatalogLoaded(models.Count, 0);
        }
        catch (Exception ex)
        {
            TraceLog.Warn($"Ollama API query failed: {ex.Message}");
        }

        return models;
    }

    public async Task<string?> LoadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
    {
        TraceLog.AiModelLoading(modelIdOrAlias, "");

        // Check if model is already available
        var models = await ListModelsAsync(ct);
        var match = models.FirstOrDefault(m =>
            m.Id.Equals(modelIdOrAlias, StringComparison.OrdinalIgnoreCase) ||
            m.Id.StartsWith(modelIdOrAlias + ":", StringComparison.OrdinalIgnoreCase) ||
            m.Alias.Equals(modelIdOrAlias, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            TraceLog.AiModelLoaded(match.Id, 0);
            return match.Id;
        }

        // Model not found locally — try pulling it
        TraceLog.DiagnosticInfo($"Model {modelIdOrAlias} not found locally; attempting pull");
        ConsoleOutput.WriteMarkup($"[dim]  Model not cached — pulling {modelIdOrAlias}...[/]");

        bool downloaded = await DownloadModelAsync(modelIdOrAlias, ct);
        if (!downloaded)
            return null;

        // Re-query to get exact model ID
        models = await ListModelsAsync(ct);
        match = models.FirstOrDefault(m =>
            m.Id.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase) ||
            m.Alias.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            TraceLog.AiModelLoaded(match.Id, 0);
            return match.Id;
        }

        TraceLog.AiModelLoadFailed(modelIdOrAlias, "Model not found after pull", "OllamaAiBackend.cs", 0);
        return null;
    }

    public Task UnloadModelAsync(string modelId, CancellationToken ct = default)
    {
        // Ollama manages model lifecycle automatically
        TraceLog.DiagnosticInfo($"Ollama model unload requested for {modelId} — skipped (auto-managed)");
        return Task.CompletedTask;
    }

    public async Task<bool> DownloadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
    {
        TraceLog.AiModelDownloadStarted(modelIdOrAlias, 0);
        ConsoleOutput.WriteMarkup($"[dim]  Pulling {modelIdOrAlias} via Ollama...[/]");

        // Try REST API streaming pull first (provides progress reporting)
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var requestBody = new { name = modelIdOrAlias, stream = true };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_serviceUrl}/api/pull")
            {
                Content = JsonContent.Create(requestBody)
            };

            var sw = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                TraceLog.DiagnosticInfo($"Ollama REST pull returned {response.StatusCode}; falling back to CLI");
                return await DownloadModelViaCliAsync(modelIdOrAlias, ct);
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

            string lastStatus = "";
            long lastReportedPct = -1;
            string? line;

            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(line);
                    string status = json.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

                    // Report download progress percentage
                    if (json.TryGetProperty("total", out var total) &&
                        json.TryGetProperty("completed", out var completed) &&
                        total.GetInt64() > 0)
                    {
                        long pct = completed.GetInt64() * 100 / total.GetInt64();
                        if (pct != lastReportedPct && pct % 10 == 0)
                        {
                            ConsoleOutput.WriteMarkup($"[dim]  {status}: {pct}%[/]");
                            lastReportedPct = pct;
                        }
                    }
                    else if (status != lastStatus && !string.IsNullOrEmpty(status))
                    {
                        ConsoleOutput.WriteMarkup($"[dim]  {status}[/]");
                        lastStatus = status;
                    }

                    // Check for error
                    if (json.TryGetProperty("error", out var err))
                    {
                        string errMsg = err.GetString() ?? "unknown error";
                        TraceLog.AiModelDownloadFailed(modelIdOrAlias, errMsg);
                        ConsoleOutput.WriteMarkup($"[red]  Pull failed: {errMsg}[/]");
                        return false;
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }

            sw.Stop();
            TraceLog.AiModelDownloadCompleted(modelIdOrAlias, sw.ElapsedMilliseconds);
            ConsoleOutput.WriteMarkup($"[dim]  Pull complete ({sw.Elapsed.TotalSeconds:F1}s)[/]");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceLog.DiagnosticInfo($"Ollama REST pull failed ({ex.Message}); falling back to CLI");
            return await DownloadModelViaCliAsync(modelIdOrAlias, ct);
        }
    }

    private async Task<bool> DownloadModelViaCliAsync(string modelIdOrAlias, CancellationToken ct)
    {
        string? cli = FindOllamaCli();
        if (cli is null)
        {
            TraceLog.AiModelDownloadSkipped(modelIdOrAlias, "Ollama CLI not available");
            ConsoleOutput.WriteMarkup(
                $"[yellow][INFO][/] Ollama CLI not available — cannot pull {modelIdOrAlias}.");
            ConsoleOutput.WriteMarkup(
                "[dim]  Run: ollama pull <model-name>[/]");
            return false;
        }

        TraceLog.DiagnosticInfo($"Pulling model via ollama pull: {modelIdOrAlias}");
        ConsoleOutput.WriteMarkup($"[dim]  Pulling {modelIdOrAlias} via CLI (this may take several minutes)...[/]");

        var sw = Stopwatch.StartNew();
        var (exitCode, _, stderr) = await RunOllamaAsync(cli, $"pull {modelIdOrAlias}", 600_000);
        sw.Stop();

        if (exitCode == 0)
        {
            TraceLog.AiModelDownloadCompleted(modelIdOrAlias, sw.ElapsedMilliseconds);
            ConsoleOutput.WriteMarkup($"[dim]  Pull complete ({sw.Elapsed.TotalSeconds:F1}s)[/]");
            return true;
        }

        TraceLog.AiModelDownloadFailed(modelIdOrAlias, stderr);
        ConsoleOutput.WriteMarkup($"[red]  Pull failed for {modelIdOrAlias}[/]");
        return false;
    }

    public IReadOnlyList<string> GetPreferredAliases(string deviceType) => PreferredModels;

    public IReadOnlyList<string> GetSharedAliasPriority() => PreferredModels;

    // ── Internal accessors ──────────────────────────────────────────────────

    internal string ServiceUrl => _serviceUrl;

    // ── Ollama CLI helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Detects the GPU acceleration backend Ollama is using (Metal, CUDA, ROCm, or CPU).
    /// Uses platform heuristics since Ollama doesn't expose this directly via REST.
    /// </summary>
    private async Task<string> DetectGpuBackendAsync(CancellationToken ct)
    {
        // macOS always uses Metal on Apple Silicon
        if (OperatingSystem.IsMacOS())
        {
            TraceLog.DiagnosticInfo("Ollama GPU backend: Metal (macOS Apple Silicon)");
            return "ollama-metal";
        }

        // Check if Ollama reports GPU usage via /api/ps (running models show GPU layers)
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var psResp = await http.GetAsync($"{_serviceUrl}/api/ps", ct);
            if (psResp.IsSuccessStatusCode)
            {
                var psJson = await psResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (psJson.TryGetProperty("models", out var runningModels) &&
                    runningModels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in runningModels.EnumerateArray())
                    {
                        if (m.TryGetProperty("size_vram", out var vram) && vram.GetInt64() > 0)
                        {
                            // GPU is being used — determine CUDA vs ROCm
                            string backend = OperatingSystem.IsLinux() ? "ollama-gpu" : "ollama-cuda";
                            TraceLog.DiagnosticInfo($"Ollama GPU backend detected: {backend} (VRAM in use)");
                            return backend;
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort detection
        }

        // Fallback: check platform for likely backend
        if (OperatingSystem.IsWindows())
        {
            TraceLog.DiagnosticInfo("Ollama GPU backend: likely CUDA (Windows)");
            return "ollama-cuda";
        }

        TraceLog.DiagnosticInfo("Ollama GPU backend: unknown (defaulting to ollama)");
        return "ollama";
    }

    private static string? FindOllamaCli()
    {
        try
        {
            var psi = new ProcessStartInfo("ollama", "--version")
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
                TraceLog.DiagnosticInfo("Ollama CLI found on PATH");
                return "ollama";
            }
        }
        catch (Exception ex)
        {
            TraceLog.DiagnosticInfo($"Ollama CLI probe failed: {ex.Message}");
        }

        // Platform-specific well-known paths
        var wellKnownPaths = new List<string>();

        if (OperatingSystem.IsMacOS())
        {
            wellKnownPaths.Add("/usr/local/bin/ollama");
            wellKnownPaths.Add("/opt/homebrew/bin/ollama");
        }
        else if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(localAppData))
                wellKnownPaths.Add(Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"));
            if (!string.IsNullOrEmpty(programFiles))
                wellKnownPaths.Add(Path.Combine(programFiles, "Ollama", "ollama.exe"));
        }

        foreach (var path in wellKnownPaths)
        {
            if (File.Exists(path))
            {
                TraceLog.DiagnosticInfo($"Ollama CLI found at {path}");
                return path;
            }
        }

        TraceLog.DiagnosticInfo($"Ollama CLI not found in well-known paths: {string.Join(", ", wellKnownPaths)}");
        return null;
    }

    private bool IsServerRunning()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            // Check server reachability via /api/tags
            var response = http.GetAsync($"{_serviceUrl}/api/tags").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;

            // Query server version for diagnostics
            try
            {
                var versionResp = http.GetAsync($"{_serviceUrl}/api/version").GetAwaiter().GetResult();
                if (versionResp.IsSuccessStatusCode)
                {
                    var versionJson = versionResp.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
                    if (versionJson.TryGetProperty("version", out var ver))
                        TraceLog.DiagnosticInfo($"Ollama server version: {ver.GetString()}");
                }
            }
            catch
            {
                // Version query is best-effort — don't fail the availability check
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOllamaAsync(
        string cli, string arguments, int timeoutMs = 30_000)
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

        var waitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(waitTask, Task.Delay(timeoutMs));
        if (completedTask != waitTask)
        {
            TraceLog.AiProcessTimeout($"ollama {arguments}", timeoutMs);
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "Timeout");
        }

        await waitTask;
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Extracts a short alias from an Ollama model name.
    /// E.g. "phi3.5:latest" → "phi3.5", "llama3.2:3b" → "llama3.2-3b"
    /// </summary>
    private static string ExtractAlias(string modelName)
    {
        // Strip ":latest" tag
        if (modelName.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
            return modelName[..^":latest".Length];

        // Replace ':' with '-' for tag variants like "qwen2.5:1.5b"
        return modelName.Replace(':', '-');
    }

    /// <summary>
    /// Returns true if the model name indicates a non-chat model (embedding, etc.).
    /// </summary>
    private static bool IsNonChatModel(string modelName)
    {
        ReadOnlySpan<string> markers =
        [
            "embed", "nomic-embed", "mxbai-embed",
            "all-minilm", "snowflake-arctic-embed",
        ];

        foreach (var marker in markers)
        {
            if (modelName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
#endif
