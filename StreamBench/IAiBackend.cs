#if ENABLE_AI
// IAiBackend.cs
// Abstraction layer for AI inference backends (Foundry Local, LM Studio, etc.).
// Each backend manages its own service lifecycle and model catalog while sharing
// the OpenAI-compatible REST API for actual inference via IChatClient (MEAI).

namespace StreamBench;

/// <summary>
/// Backend-agnostic model metadata. Replaces the Foundry-specific FoundryModel
/// record so that AiBenchmarkRunner can work with any backend.
/// </summary>
internal sealed record AiModelInfo(
    string Id,
    string Alias,
    string DeviceType,
    string ExecutionProvider,
    double FileSizeMb,
    bool IsCached,
    string BackendName);

/// <summary>
/// Abstraction for AI inference backends. Each backend handles its own
/// service lifecycle (start/stop), model management, and endpoint discovery.
/// Inference itself goes through the shared OpenAI-compatible REST API
/// via IChatClient from Microsoft.Extensions.AI.
/// </summary>
internal interface IAiBackend
{
    /// <summary>Display name for logging and UI (e.g. "Foundry Local", "LM Studio").</summary>
    string Name { get; }

    /// <summary>
    /// Checks whether this backend is available on the current system
    /// (CLI found, service reachable, etc.). Fast, synchronous probe.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Starts the backend service and returns the base URL (scheme://host:port).
    /// Returns null if the service cannot be started.
    /// </summary>
    Task<string?> StartAsync(CancellationToken ct = default);

    /// <summary>Stops the backend service gracefully.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Lists all models available through this backend.</summary>
    Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads a specific model so it is ready for inference.
    /// Returns the model ID as recognized by the backend's /v1/chat/completions endpoint.
    /// Returns null if loading failed.
    /// </summary>
    Task<string?> LoadModelAsync(string modelIdOrAlias, CancellationToken ct = default);

    /// <summary>
    /// Unloads a model to free resources. No-op if the backend doesn't support it.
    /// </summary>
    Task UnloadModelAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// Downloads a model if not already cached. Returns true on success.
    /// Implementations should respect cancellation and provide progress via ConsoleOutput.
    /// </summary>
    Task<bool> DownloadModelAsync(string modelIdOrAlias, CancellationToken ct = default);

    /// <summary>
    /// Returns the preferred model aliases for a given device type, ordered by priority.
    /// Used by the benchmark runner for automatic model selection.
    /// </summary>
    IReadOnlyList<string> GetPreferredAliases(string deviceType);

    /// <summary>
    /// Returns the shared model alias priority list for multi-device comparison.
    /// </summary>
    IReadOnlyList<string> GetSharedAliasPriority();

    /// <summary>
    /// Whether this backend supports device-specific model variants (CPU/GPU/NPU).
    /// Foundry does; LM Studio does not.
    /// </summary>
    bool SupportsDeviceTargeting { get; }
}
#endif
