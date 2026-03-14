#if ENABLE_AI
namespace StreamBench;

internal sealed record AiExecutionOptions(
    IReadOnlyList<string> DeviceFilter,
    string? ModelAlias,
    bool SharedOnly,
    bool NoDownload,
    bool QuickMode,
    AiBackendType BackendType)
{
    internal static AiExecutionOptions FromCli(
        string? deviceArg,
        string? modelAlias,
        bool sharedOnly,
        bool noDownload,
        bool quickMode,
        string? backendArg)
    {
        return new AiExecutionOptions(
            DeviceFilter: ParseDeviceFilter(deviceArg),
            ModelAlias: NormalizeValue(modelAlias),
            SharedOnly: sharedOnly,
            NoDownload: noDownload,
            QuickMode: quickMode,
            BackendType: ParseBackendType(backendArg));
    }

    internal IEnumerable<string>? DevicesOrDefault =>
        DeviceFilter.Count == 0 ? null : DeviceFilter;

    internal bool HasExplicitSelection =>
        DeviceFilter.Count > 0
        || !string.IsNullOrWhiteSpace(ModelAlias)
        || SharedOnly
        || NoDownload
        || QuickMode
        || BackendType != AiBackendType.Auto;

    internal string BackendLabel =>
        BackendType switch
        {
            AiBackendType.Foundry => "Foundry Local",
            AiBackendType.LmStudio => "LM Studio",
            _ => "Auto-detect"
        };

    private static IReadOnlyList<string> ParseDeviceFilter(string? deviceArg)
    {
        if (string.IsNullOrWhiteSpace(deviceArg))
            return [];

        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CPU",
            "GPU",
            "NPU"
        };

        return deviceArg
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().ToUpperInvariant())
            .Where(d => supported.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AiBackendType ParseBackendType(string? backendArg)
    {
        return NormalizeValue(backendArg)?.ToLowerInvariant() switch
        {
            "foundry" => AiBackendType.Foundry,
            "lmstudio" or "lm-studio" => AiBackendType.LmStudio,
            _ => AiBackendType.Auto,
        };
    }

    private static string? NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
#endif
