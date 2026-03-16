#if ENABLE_AI
// AiBackendFactory.cs
// Creates the appropriate IAiBackend based on user configuration and system detection.

namespace StreamBench;

internal static class AiBackendFactory
{
    /// <summary>
    /// Creates an AI backend based on configuration and availability.
    /// Auto mode: tries Foundry first (Windows), then LM Studio.
    /// </summary>
    internal static IAiBackend Create(AiBackendConfig config)
    {
        if (config.Backend is AiBackendType.Foundry or AiBackendType.LmStudio)
        {
            var name = config.Backend == AiBackendType.Foundry ? "Foundry Local" : "LM Studio";
            TraceLog.AiBackendSelected(name, "User selected via --ai-backend");
        }

        return config.Backend switch
        {
            AiBackendType.Foundry => CreateFoundry(config),
            AiBackendType.LmStudio => CreateLmStudio(config),
            AiBackendType.Auto => AutoDetect(config),
            _ => AutoDetect(config),
        };
    }

    /// <summary>
    /// Auto-detects the best available backend.
    /// Priority: Foundry (Windows/macOS, supports NPU) → LM Studio (cross-platform).
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

        // Try LM Studio (cross-platform)
        var lmStudio = CreateLmStudio(config);
        if (lmStudio.IsAvailable())
        {
            TraceLog.DiagnosticInfo("Auto-detected: LM Studio");
            TraceLog.AiBackendAutoDetect("LM Studio", "LM Studio CLI found");
            ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]LM Studio[/][/]");
            return lmStudio;
        }

        // On Windows/macOS, still return Foundry as it gives the best error message
        // about how to install. On Linux, return LM Studio.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            TraceLog.DiagnosticInfo("No AI backend detected; defaulting to Foundry (Windows/macOS)");
            TraceLog.AiBackendAutoDetect("Foundry Local (default)", "No backend found, defaulting for Windows/macOS");
            return CreateFoundry(config);
        }

        TraceLog.DiagnosticInfo("No AI backend detected; defaulting to LM Studio");
        TraceLog.AiBackendAutoDetect("LM Studio (default)", "No backend found, defaulting for Linux");
        return CreateLmStudio(config);
    }

    private static FoundryAiBackend CreateFoundry(AiBackendConfig config)
    {
        return new FoundryAiBackend();
    }

    private static LmStudioAiBackend CreateLmStudio(AiBackendConfig config)
    {
        return new LmStudioAiBackend(config.LmStudioEndpoint);
    }

    /// <summary>
    /// Returns a user-friendly message about how to install an AI backend
    /// based on the current platform.
    /// </summary>
    internal static string GetInstallInstructions(AiBackendType preferredBackend)
    {
        if (preferredBackend == AiBackendType.Foundry || (preferredBackend == AiBackendType.Auto && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())))
        {
            if (OperatingSystem.IsMacOS())
            {
                return "Install Foundry Local: brew tap microsoft/foundrylocal && brew install foundrylocal\n" +
                       "  Or install LM Studio: brew install --cask lm-studio";
            }
            return "Install Foundry Local: winget install Microsoft.FoundryLocal\n" +
                   "  Or install LM Studio: https://lmstudio.ai/";
        }

        return "Install LM Studio: https://lmstudio.ai/\n" +
               "  macOS: brew install --cask lm-studio\n" +
               "  Then start the server and load a model.";
    }
}
#endif
