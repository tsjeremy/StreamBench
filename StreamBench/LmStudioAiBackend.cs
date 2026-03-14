#if ENABLE_AI
// LmStudioAiBackend.cs
// IAiBackend implementation for LM Studio.
// Cross-platform (Windows, macOS, Linux) — uses lms CLI + OpenAI-compatible REST API.

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace StreamBench;

internal sealed class LmStudioAiBackend : IAiBackend
{
    public string Name => "LM Studio";
    public bool SupportsDeviceTargeting => false;

    private string? _cli;
    private string? _serviceUrl;
    private bool _ownedServer;  // true if we started the server ourselves

    private const int DefaultPort = 1234;
    private const string DefaultEndpoint = "http://127.0.0.1:1234";

    // LM Studio doesn't have device-specific model variants.
    // These are the recommended models for benchmarking, ordered by quality/speed balance.
    private static readonly string[] PreferredModels =
    [
        "phi-3.5-mini",
        "phi-4-mini",
        "phi-3-mini",
        "qwen2.5-1.5b",
        "qwen2.5-0.5b",
        "qwen2.5-7b",
    ];

    public LmStudioAiBackend(string? endpoint = null)
    {
        if (!string.IsNullOrEmpty(endpoint))
            _serviceUrl = endpoint.TrimEnd('/');
    }

    // ── IAiBackend implementation ───────────────────────────────────────────

    public bool IsAvailable()
    {
        // Check if CLI is available OR if the server is already running
        _cli ??= FindLmsCli();
        if (_cli is not null) return true;

        // No CLI but check if server is already running on default port
        return IsServerRunning(_serviceUrl ?? DefaultEndpoint);
    }

    public async Task<string?> StartAsync(CancellationToken ct = default)
    {
        // If endpoint was specified, just check if it's reachable
        if (_serviceUrl is not null && IsServerRunning(_serviceUrl))
        {
            TraceLog.DiagnosticInfo($"LM Studio server already running at {_serviceUrl}");
            TraceLog.LmStudioServerAlreadyRunning(_serviceUrl);
            return _serviceUrl;
        }

        // Check default endpoint
        if (_serviceUrl is null && IsServerRunning(DefaultEndpoint))
        {
            _serviceUrl = DefaultEndpoint;
            TraceLog.DiagnosticInfo($"LM Studio server already running at {_serviceUrl}");
            TraceLog.LmStudioServerAlreadyRunning(_serviceUrl);
            return _serviceUrl;
        }

        // Try starting via CLI
        _cli ??= FindLmsCli();
        if (_cli is null)
        {
            TraceLog.DiagnosticInfo("LM Studio CLI not found and server not running");
            TraceLog.LmStudioCliNotFound();
            return null;
        }

        TraceLog.LmStudioServerStarting(DefaultPort);
        TraceLog.AiServiceStarting();
        ConsoleOutput.WriteMarkup("[dim]  Starting LM Studio server (may take up to 60 s)...[/]");

        var (exitCode, stdout, stderr) = await RunLmsAsync(_cli, "server start", 60_000);

        // Wait briefly for server to become ready
        _serviceUrl ??= DefaultEndpoint;
        for (int i = 0; i < 10; i++)
        {
            if (IsServerRunning(_serviceUrl))
            {
                _ownedServer = true;
                TraceLog.AiServiceStarted();
                TraceLog.DiagnosticInfo($"LM Studio server started at {_serviceUrl}");
                TraceLog.LmStudioServerStarted(_serviceUrl);
                return _serviceUrl;
            }
            await Task.Delay(1_000, ct);
        }

        // Try extracting URL from output
        var url = ExtractUrl(stdout + "\n" + stderr);
        if (url is not null)
        {
            _serviceUrl = url;
            _ownedServer = true;
            TraceLog.AiServiceStarted();
            TraceLog.LmStudioServerStarted(_serviceUrl);
            return _serviceUrl;
        }

        TraceLog.AiServiceStartFailed("LM Studio server failed to start", "LmStudioAiBackend.cs", 0);
        return null;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // Only stop if we started it ourselves
        if (!_ownedServer || _cli is null) return;

        try
        {
            TraceLog.AiServiceStopping();
            await RunLmsAsync(_cli, "server stop", 10_000);
            TraceLog.AiServiceStopped();
            TraceLog.LmStudioServerStopped();
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"LM Studio stop: {ex.Message}");
        }
    }

    public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var models = new List<AiModelInfo>();

        // Strategy 1: Try REST API /v1/models (works if server is running)
        string endpoint = _serviceUrl ?? DefaultEndpoint;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetFromJsonAsync<JsonElement>(
                $"{endpoint}/v1/models", ct);

            if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in data.EnumerateArray())
                {
                    string id = model.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;

                    // Skip non-chat models (embedding, rerank, vision-only, TTS, etc.)
                    if (IsNonChatModel(id)) continue;

                    // Check the "type" field if present — some LM Studio versions
                    // annotate embedding vs text-generation models.
                    string? modelType = model.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (modelType is not null
                        && modelType.Contains("embedding", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string alias = ExtractAlias(id);
                    models.Add(new AiModelInfo(
                        Id: id,
                        Alias: alias,
                        DeviceType: "GPU", // LM Studio primarily uses GPU
                        ExecutionProvider: "llama.cpp",
                        FileSizeMb: 0,
                        IsCached: true,
                        BackendName: Name));
                }
            }

            if (models.Count > 0)
            {
                TraceLog.AiCatalogLoaded(models.Count, 0);
                TraceLog.DiagnosticInfo($"LM Studio model catalog loaded via REST API ({models.Count} models)");
                TraceLog.LmStudioModelListed(models.Count);
                return models;
            }
        }
        catch (Exception ex)
        {
            TraceLog.Warn($"LM Studio REST API query failed: {ex.Message}");
        }

        // Strategy 2: Try CLI ls command
        if (_cli is not null)
        {
            var (exitCode, stdout, _) = await RunLmsAsync(_cli, "ls", 15_000);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                models = ParseLmsLsOutput(stdout);
                TraceLog.AiCatalogLoaded(models.Count, 0);
                TraceLog.LmStudioModelListed(models.Count);
                TraceLog.DiagnosticInfo($"LM Studio model catalog loaded via CLI fallback ({models.Count} models)");
            }
        }

        return models;
    }

    public async Task<string?> LoadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
    {
        TraceLog.AiModelLoading(modelIdOrAlias, "");
        TraceLog.LmStudioModelLoading(modelIdOrAlias);

        // If the model is already loaded (via /v1/models), return immediately
        var loadedModels = await ListModelsAsync(ct);
        var match = loadedModels.FirstOrDefault(m =>
            m.Id.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase) ||
            m.Alias.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            TraceLog.AiModelLoaded(match.Id, 0);
            return match.Id;
        }

        if (_cli is null)
        {
            TraceLog.DiagnosticInfo($"Cannot load model {modelIdOrAlias}: no CLI available");
            return null;
        }

        // Use `lms ls` to find an exact model key on disk — avoids interactive prompts
        string loadKey = modelIdOrAlias;
        var (lsExit, lsOut, _) = await RunLmsAsync(_cli, "ls", 15_000);
        if (lsExit == 0 && !string.IsNullOrWhiteSpace(lsOut))
        {
            var onDisk = ParseLmsLsOutput(lsOut);
            var diskMatch = onDisk.FirstOrDefault(m =>
                m.Id.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase) ||
                m.Alias.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase));

            if (diskMatch is not null)
            {
                loadKey = diskMatch.Id; // exact key, no interactive prompt
                TraceLog.DiagnosticInfo($"Resolved '{modelIdOrAlias}' to exact key '{loadKey}' from lms ls");
            }
            else if (onDisk.Count > 0)
            {
                // No match for requested alias but there are other chat models
                loadKey = onDisk[0].Id;
                TraceLog.DiagnosticInfo($"Model '{modelIdOrAlias}' not found on disk; using '{loadKey}' instead");
            }
            else
            {
                // No chat models on disk — try downloading
                TraceLog.DiagnosticInfo($"No chat models on disk; attempting download of {modelIdOrAlias}");
                ConsoleOutput.WriteMarkup($"[dim]  No chat models found on disk. Downloading {modelIdOrAlias} (this may take several minutes)...[/]");
                var (dlExit, _, dlErr) = await RunLmsAsync(_cli, $"get \"{modelIdOrAlias}\" --yes", 600_000);
                if (dlExit != 0)
                {
                    TraceLog.Warn($"Failed to download model: {dlErr}");
                    return null;
                }

                // Re-scan to get exact key
                var (lsExit2, lsOut2, _) = await RunLmsAsync(_cli, "ls", 15_000);
                if (lsExit2 == 0 && !string.IsNullOrWhiteSpace(lsOut2))
                {
                    var downloaded = ParseLmsLsOutput(lsOut2);
                    var dlMatch = downloaded.FirstOrDefault(m =>
                        m.Id.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase) ||
                        m.Alias.Contains(modelIdOrAlias, StringComparison.OrdinalIgnoreCase));
                    loadKey = dlMatch?.Id ?? modelIdOrAlias;
                }
            }
        }

        var sw = Stopwatch.StartNew();
        ConsoleOutput.WriteMarkup($"[dim]  Loading model {loadKey} via LM Studio CLI (may take several minutes)...[/]");
        var (exitCode, stdout, stderr) = await RunLmsAsync(_cli, $"load \"{loadKey}\"", 300_000);
        sw.Stop();

        if (exitCode == 0)
        {
            TraceLog.AiModelLoaded(loadKey, sw.ElapsedMilliseconds);
            TraceLog.LmStudioModelLoaded(loadKey, sw.ElapsedMilliseconds);
            // Re-query to get the actual model ID
            loadedModels = await ListModelsAsync(ct);
            match = loadedModels.FirstOrDefault(m =>
                m.Id.Contains(loadKey, StringComparison.OrdinalIgnoreCase) ||
                m.Alias.Contains(loadKey, StringComparison.OrdinalIgnoreCase));
            return match?.Id ?? loadKey;
        }

        TraceLog.AiModelLoadFailed(loadKey, stderr, "LmStudioAiBackend.cs", 0);
        TraceLog.LmStudioModelLoadFailed(loadKey, stderr);
        return null;
    }

    public async Task UnloadModelAsync(string modelId, CancellationToken ct = default)
    {
        if (_cli is null) return;
        try
        {
            await RunLmsAsync(_cli, $"unload \"{modelId}\"", 15_000);
            TraceLog.AiModelUnloaded(modelId);
        }
        catch (Exception ex)
        {
            DiagnosticHelper.LogWarning($"LM Studio model unload failed for {modelId}: {ex.Message}");
            TraceLog.Warn($"LM Studio model unload failed: {modelId} — {ex.Message}");
        }
    }

    public async Task<bool> DownloadModelAsync(string modelIdOrAlias, CancellationToken ct = default)
    {
        // LM Studio doesn't have CLI-based download like Foundry.
        // Users must download models through the LM Studio GUI.
        TraceLog.AiModelDownloadSkipped(modelIdOrAlias, "LM Studio requires manual download via GUI");
        ConsoleOutput.WriteMarkup(
            $"[yellow][INFO][/] LM Studio requires manual model download through the GUI.");
        ConsoleOutput.WriteMarkup(
            $"[dim]  Suggested model: {modelIdOrAlias}[/]");
        ConsoleOutput.WriteMarkup(
            "[dim]  Open LM Studio → Search tab → search for the model → Download[/]");
        return false;
    }

    public IReadOnlyList<string> GetPreferredAliases(string deviceType) => PreferredModels;

    public IReadOnlyList<string> GetSharedAliasPriority() => PreferredModels;

    // ── Internal accessors ──────────────────────────────────────────────────

    internal string? ServiceUrl => _serviceUrl;

    // ── LM Studio CLI helpers ──────────────────────────────────────────────

    private static string? FindLmsCli()
    {
        // Try 'lms' on PATH first
        if (TryProbeLmsCli("lms") is string found) return found;

        // Platform-specific well-known paths
        if (OperatingSystem.IsWindows())
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LM Studio", "resources", "app", ".webpack", "lms.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LM Studio", "resources", "bin", "lms.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LM Studio", "lms.exe"),
            };
            foreach (var path in paths)
            {
                if (File.Exists(path) && TryProbeLmsCli(path) is string f) return f;
            }
        }
        else
        {
            // macOS and Linux
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new[]
            {
                Path.Combine(home, ".lmstudio", "bin", "lms"),
                "/usr/local/bin/lms",
            };
            foreach (var path in paths)
            {
                if (File.Exists(path) && TryProbeLmsCli(path) is string f) return f;
            }
        }

        TraceLog.DiagnosticInfo("LM Studio CLI (lms) not found");
        TraceLog.LmStudioCliNotFound();
        return null;
    }

    private static string? TryProbeLmsCli(string nameOrPath)
    {
        try
        {
            var psi = new ProcessStartInfo(nameOrPath, "version")
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
                TraceLog.DiagnosticInfo($"LM Studio CLI found: {nameOrPath}");
                TraceLog.LmStudioCliFound(nameOrPath);
                return nameOrPath;
            }
        }
        catch (Exception ex)
        {
            TraceLog.DiagnosticInfo($"LM Studio CLI probe failed for '{nameOrPath}': {ex.Message}");
        }
        return null;
    }

    private static bool IsServerRunning(string endpoint)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = http.GetAsync($"{endpoint}/v1/models").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunLmsAsync(
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

        var waitForExitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(timeoutMs));
        if (completedTask != waitForExitTask)
        {
            TraceLog.AiProcessTimeout($"lms {arguments}", timeoutMs);
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "Timeout");
        }

        await waitForExitTask;
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static string? ExtractUrl(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            int idx = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            int end = idx;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
                end++;

            string url = line[idx..end].TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return $"{uri.Scheme}://{uri.Authority}";
        }
        return null;
    }

    // ── Model listing helpers ──────────────────────────────────────────────

    private List<AiModelInfo> ParseLmsLsOutput(string output)
    {
        var models = new List<AiModelInfo>();
        bool inEmbeddingSection = false;

        foreach (var line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Skip decorative lines
            if (trimmed.StartsWith("─") || trimmed.StartsWith("-") || trimmed.StartsWith("=")) continue;

            // Skip summary lines like "You have N models..."
            if (trimmed.StartsWith("You have ", StringComparison.OrdinalIgnoreCase)) continue;

            // Detect section headers: "LLM", "EMBEDDING", column headers
            string upper = trimmed.ToUpperInvariant();
            if (upper.StartsWith("EMBEDDING"))
            {
                inEmbeddingSection = true;
                continue;
            }
            if (upper.StartsWith("LLM"))
            {
                inEmbeddingSection = false;
                continue;
            }

            // Skip column header lines (PARAMS, ARCH, SIZE, PATH, etc.)
            if (upper.Contains("PARAMS") && (upper.Contains("ARCH") || upper.Contains("SIZE"))) continue;

            // Skip everything in the EMBEDDING section
            if (inEmbeddingSection) continue;

            // First token is the model key
            string id = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
            if (id.Length < 3) continue;
            if (IsNonChatModel(id)) continue;

            string alias = ExtractAlias(id);
            models.Add(new AiModelInfo(
                Id: id,
                Alias: alias,
                DeviceType: "GPU",
                ExecutionProvider: "llama.cpp",
                FileSizeMb: 0,
                IsCached: true,
                BackendName: Name));
        }
        return models;
    }

    /// <summary>
    /// Extracts a human-friendly alias from an LM Studio model ID.
    /// E.g. "lmstudio-community/phi-3.5-mini-instruct-GGUF" → "phi-3.5-mini"
    /// </summary>
    private static string ExtractAlias(string modelId)
    {
        // Strip owner prefix (e.g. "lmstudio-community/")
        string name = modelId.Contains('/') ? modelId.Split('/').Last() : modelId;

        // Strip GGUF suffix and quantization
        name = name.Replace("-GGUF", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(".gguf", "", StringComparison.OrdinalIgnoreCase);

        // Strip common suffixes
        foreach (var suffix in new[] { "-instruct", "-chat", "-it", "-q4_k_m", "-q4_0", "-q5_k_m", "-q8_0", "-fp16" })
        {
            int idx = name.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) name = name[..idx];
        }

        return name.Trim('-').Trim('_');
    }

    /// <summary>
    /// Returns true if the model ID indicates a non-chat model (embedding, rerank, TTS,
    /// whisper, vision-encoder-only, etc.) or a virtual/system entry that cannot handle
    /// /v1/chat/completions.
    /// </summary>
    private static bool IsNonChatModel(string modelId)
    {
        // Very short IDs are likely virtual/system entries, not real models
        if (modelId.Length < 4) return true;

        // Known LM Studio virtual model / persona names
        ReadOnlySpan<string> systemNames = ["You", "Assistant", "System", "Default"];
        foreach (var name in systemNames)
        {
            if (modelId.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Common non-chat model indicators in LM Studio model IDs
        ReadOnlySpan<string> markers =
        [
            "embedding", "embed-", "rerank", "reranker",
            "whisper", "tts", "text-to-speech",
            "clip", "vision-encoder", "image-encoder"
        ];

        foreach (var marker in markers)
        {
            if (modelId.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Model IDs starting with "text-embedding-" are always embedding models
        if (modelId.StartsWith("text-embedding-", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
#endif
