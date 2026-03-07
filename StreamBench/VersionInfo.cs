// VersionInfo.cs — Centralized runtime version accessor for StreamBench.
// Reads the assembly version (sourced from the VERSION file at build time).

using System.Reflection;

namespace StreamBench;

public static class VersionInfo
{
    /// <summary>
    /// The StreamBench version string (e.g., "5.10.20").
    /// Falls back to "dev" if the assembly version is unavailable.
    /// </summary>
    public static readonly string Version =
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?.Split('+')[0]   // strip build metadata if present
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "dev";
}
