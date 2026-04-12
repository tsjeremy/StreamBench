#if ENABLE_AI
// AiBackendFactory.cs
// Creates the appropriate IAiBackend based on user configuration and system detection.

namespace StreamBench;

internal static class AiBackendFactory
{
    /// <summary>
    /// Creates an AI backend based on configuration and availability.
    /// Auto mode: tries Foundry first (Windows/macOS), then Ollama (Linux), then LM Studio.
    /// </summary>
    internal static IAiBackend Create(AiBackendConfig config)
    {
        if (config.Backend is AiBackendType.Foundry or AiBackendType.LmStudio or AiBackendType.Ollama)
        {
            var name = config.Backend switch
            {
                AiBackendType.Foundry => "Foundry Local",
                AiBackendType.LmStudio => "LM Studio",
                AiBackendType.Ollama => "Ollama",
                _ => "Unknown"
            };
            TraceLog.AiBackendSelected(name, "User selected via --ai-backend");
        }

        return config.Backend switch
        {
            AiBackendType.Foundry => CreateFoundry(config),
            AiBackendType.LmStudio => CreateLmStudio(config),
            AiBackendType.Ollama => CreateOllama(config),
            AiBackendType.Auto => AutoDetect(config),
            _ => AutoDetect(config),
        };
    }

    /// <summary>
    /// Auto-detects the best available backend.
    /// Priority: Foundry (Windows/macOS) → Ollama (Linux) → LM Studio → Ollama (other).
    /// TODO: Let users persist auto-detect preference in streambench_ai_config.json
    /// so repeated runs skip the detection waterfall.
    /// </summary>
    private static IAiBackend AutoDetect(AiBackendConfig config)
    {
        // Try Foundry first on Windows and macOS (it supports NPU on Windows,
        // and provides optimized CPU/GPU inference on both platforms).
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var foundry = CreateFoundry(config);
            if (foundry.IsAvailable())
            {
                TraceLog.DiagnosticInfo("Auto-detected: Foundry Local");
                TraceLog.AiBackendAutoDetect("Foundry Local", "Foundry CLI found on PATH");
                ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]Foundry Local[/][/]");
                return foundry;
            }
        }

        // On Linux, try Ollama before LM Studio (simpler install, better headless support)
        if (OperatingSystem.IsLinux())
        {
            var ollama = CreateOllama(config);
            if (ollama.IsAvailable())
            {
                TraceLog.DiagnosticInfo("Auto-detected: Ollama (Linux priority)");
                TraceLog.AiBackendAutoDetect("Ollama", "Ollama CLI found on PATH (Linux priority)");
                ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]Ollama[/][/]");
                return ollama;
            }
        }

        // Try LM Studio (cross-platform)
        var lmStudio = CreateLmStudio(config);
        if (lmStudio.IsAvailable())
        {
            TraceLog.DiagnosticInfo("Auto-detected: LM Studio");
            TraceLog.AiBackendAutoDetect("LM Studio", "LM Studio CLI found");
            ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]LM Studio[/][/]");
            return lmStudio;
        }

        // Try Ollama (cross-platform, fallback for Windows/macOS)
        {
            var ollama = CreateOllama(config);
            if (ollama.IsAvailable())
            {
                TraceLog.DiagnosticInfo("Auto-detected: Ollama");
                TraceLog.AiBackendAutoDetect("Ollama", "Ollama CLI found on PATH");
                ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]Ollama[/][/]");
                return ollama;
            }
        }

        // No backend found — return sensible default with install instructions
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            TraceLog.DiagnosticInfo("No AI backend detected; defaulting to Foundry (Windows/macOS)");
            TraceLog.AiBackendAutoDetect("Foundry Local (default)", "No backend found, defaulting for Windows/macOS");
            return CreateFoundry(config);
        }

        TraceLog.DiagnosticInfo("No AI backend detected; defaulting to Ollama (Linux)");
        TraceLog.AiBackendAutoDetect("Ollama (default)", "No backend found, defaulting for Linux");
        return CreateOllama(config);
    }

    private static FoundryAiBackend CreateFoundry(AiBackendConfig config)
    {
        return new FoundryAiBackend();
    }

    private static LmStudioAiBackend CreateLmStudio(AiBackendConfig config)
    {
        return new LmStudioAiBackend(config.LmStudioEndpoint);
    }

    private static OllamaAiBackend CreateOllama(AiBackendConfig config)
    {
        return new OllamaAiBackend(config.OllamaEndpoint);
    }

    /// <summary>
    /// Returns a user-friendly message about how to install an AI backend
    /// based on the current platform.
    /// </summary>
    internal static string GetInstallInstructions(AiBackendType preferredBackend)
    {
        if (preferredBackend == AiBackendType.Ollama)
        {
            return "Install Ollama: https://ollama.com/\n" +
                   "  macOS: brew install ollama\n" +
                   "  Windows: winget install Ollama.Ollama\n" +
                   "  Then: ollama pull gemma4:26b";
        }

        if (preferredBackend == AiBackendType.Foundry || (preferredBackend == AiBackendType.Auto && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())))
        {
            if (OperatingSystem.IsMacOS())
            {
                return "Install Foundry Local: brew tap microsoft/foundrylocal && brew install foundrylocal\n" +
                       "  Or install LM Studio: brew install --cask lm-studio\n" +
                       "  Or install Ollama: brew install ollama";
            }
            return "Install Foundry Local: winget install Microsoft.FoundryLocal\n" +
                   "  Or install LM Studio: https://lmstudio.ai/\n" +
                   "  Or install Ollama: winget install Ollama.Ollama";
        }

        return "Install LM Studio: https://lmstudio.ai/\n" +
               "  macOS: brew install --cask lm-studio\n" +
               "  Or install Ollama: https://ollama.com/\n" +
               "  Then start the server and load a model.";
    }
}
#endif
