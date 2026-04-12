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

    // TODO: Detect Ollama GPU backend type (Metal, CUDA, ROCm, CPU-only fallback)
    //       and report it in DeviceType/ExecutionProvider for more accurate device comparison.
    public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var models = new List<AiModelInfo>();

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
                        DeviceType: "GPU", // Ollama uses GPU by default on macOS/CUDA
                        ExecutionProvider: "ollama",
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

    // TODO: Add download progress reporting via Ollama REST API (/api/pull with streaming)
    //       to give users a progress bar instead of a silent wait during large model pulls.
    public async Task<bool> DownloadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
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

        TraceLog.AiModelDownloadStarted(modelIdOrAlias, 0);
        TraceLog.DiagnosticInfo($"Pulling model via ollama pull: {modelIdOrAlias}");
        ConsoleOutput.WriteMarkup($"[dim]  Pulling {modelIdOrAlias} via Ollama (this may take several minutes)...[/]");

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

    // TODO: Add Windows well-known paths for Ollama CLI detection
    //       (e.g. %LOCALAPPDATA%\Programs\Ollama\ollama.exe, %ProgramFiles%\Ollama\ollama.exe)
    //       to improve first-run UX when PATH is not refreshed after install.
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

        // macOS well-known paths
        if (OperatingSystem.IsMacOS())
        {
            var paths = new[] { "/usr/local/bin/ollama", "/opt/homebrew/bin/ollama" };
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    TraceLog.DiagnosticInfo($"Ollama CLI found at {path}");
                    return path;
                }
            }
        }

        return null;
    }

    // TODO: Surface the Ollama server version in diagnostics via /api/version
    //       to help debug backend-specific issues in trace logs.
    private bool IsServerRunning()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = http.GetAsync($"{_serviceUrl}/api/tags").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
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
