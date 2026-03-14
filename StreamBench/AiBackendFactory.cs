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
    /// Priority: Foundry (Windows, supports NPU) → LM Studio (cross-platform).
    /// </summary>
    private static IAiBackend AutoDetect(AiBackendConfig config)
    {
        // Try Foundry first on Windows (it supports NPU)
        if (OperatingSystem.IsWindows())
        {
            var foundry = CreateFoundry(config);
            if (foundry.IsAvailable())
            {
                TraceLog.DiagnosticInfo("Auto-detected: Foundry Local");
                ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]Foundry Local[/][/]");
                return foundry;
            }
        }

        // Try LM Studio (cross-platform)
        var lmStudio = CreateLmStudio(config);
        if (lmStudio.IsAvailable())
        {
            TraceLog.DiagnosticInfo("Auto-detected: LM Studio");
            ConsoleOutput.WriteMarkup("[dim]  Auto-detected AI backend: [white]LM Studio[/][/]");
            return lmStudio;
        }

        // On Windows, still return Foundry as it gives the best error message
        // about how to install. On other platforms, return LM Studio.
        if (OperatingSystem.IsWindows())
        {
            TraceLog.DiagnosticInfo("No AI backend detected; defaulting to Foundry (Windows)");
            return CreateFoundry(config);
        }

        TraceLog.DiagnosticInfo("No AI backend detected; defaulting to LM Studio");
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
        if (preferredBackend == AiBackendType.Foundry || (preferredBackend == AiBackendType.Auto && OperatingSystem.IsWindows()))
        {
            return "Install Foundry Local: winget install Microsoft.FoundryLocal\n" +
                   "  Or install LM Studio: https://lmstudio.ai/";
        }

        return "Install LM Studio: https://lmstudio.ai/\n" +
               "  macOS: brew install --cask lm-studio\n" +
               "  Then start the server and load a model.";
    }
}
#endif
