// Models/BenchmarkResult.cs
// C# record types matching the JSON contract produced by stream_cpu / stream_gpu backends.

using System.Text.Json.Serialization;

namespace StreamBench.Models;

public record SystemInfo(
    [property: JsonPropertyName("hostname")]    string Hostname,
    [property: JsonPropertyName("os")]          string Os,
    [property: JsonPropertyName("architecture")]string Architecture,
    [property: JsonPropertyName("cpu_model")]   string CpuModel,
    [property: JsonPropertyName("logical_cpus")]int LogicalCpus,
    [property: JsonPropertyName("cpu_base_mhz")]int CpuBaseMhz,
    [property: JsonPropertyName("cpu_max_mhz")] int? CpuMaxMhz,
    [property: JsonPropertyName("total_ram_gb")]double TotalRamGb,
    [property: JsonPropertyName("numa_nodes")]  int NumaNodes
);

public record MemoryModule(
    [property: JsonPropertyName("locator")]              string Locator,
    [property: JsonPropertyName("size_mb")]              int SizeMb,
    [property: JsonPropertyName("type")]                 string Type,
    [property: JsonPropertyName("form_factor")]          string FormFactor,
    [property: JsonPropertyName("speed_mts")]            int SpeedMts,
    [property: JsonPropertyName("configured_speed_mts")] int ConfiguredSpeedMts,
    [property: JsonPropertyName("data_width_bits")]      int DataWidthBits,
    [property: JsonPropertyName("total_width_bits")]     int TotalWidthBits,
    [property: JsonPropertyName("rank")]                 int Rank,
    [property: JsonPropertyName("manufacturer")]         string Manufacturer,
    [property: JsonPropertyName("part_number")]          string PartNumber
);

public record MemoryInfo(
    [property: JsonPropertyName("type")]                 string? Type,
    [property: JsonPropertyName("speed_mts")]            int SpeedMts,
    [property: JsonPropertyName("configured_speed_mts")] int ConfiguredSpeedMts,
    [property: JsonPropertyName("modules_populated")]    int ModulesPopulated,
    [property: JsonPropertyName("total_slots")]          int TotalSlots,
    [property: JsonPropertyName("modules")]              List<MemoryModule>? Modules,
    [property: JsonPropertyName("available")]            bool? Available
);

public record CacheInfo(
    [property: JsonPropertyName("l1d_per_core_kb")] int L1dPerCoreKb,
    [property: JsonPropertyName("l1i_per_core_kb")] int L1iPerCoreKb,
    [property: JsonPropertyName("l2_per_core_kb")]  int L2PerCoreKb,
    [property: JsonPropertyName("l3_total_kb")]     int L3TotalKb
);

public record ConfigInfo(
    [property: JsonPropertyName("array_size_elements")] long ArraySizeElements,
    [property: JsonPropertyName("array_size_mib")]      double ArraySizeMib,
    [property: JsonPropertyName("bytes_per_element")]   int BytesPerElement,
    [property: JsonPropertyName("total_memory_mib")]    double TotalMemoryMib,
    [property: JsonPropertyName("ntimes")]              int Ntimes
);

public record KernelResult(
    [property: JsonPropertyName("best_rate_mbps")] double BestRateMbps,
    [property: JsonPropertyName("avg_time_sec")]   double AvgTimeSec,
    [property: JsonPropertyName("min_time_sec")]   double MinTimeSec,
    [property: JsonPropertyName("max_time_sec")]   double MaxTimeSec
);

public record BenchmarkResults(
    [property: JsonPropertyName("copy")]  KernelResult Copy,
    [property: JsonPropertyName("scale")] KernelResult Scale,
    [property: JsonPropertyName("add")]   KernelResult Add,
    [property: JsonPropertyName("triad")] KernelResult Triad
);

public record GpuDevice(
    [property: JsonPropertyName("name")]               string Name,
    [property: JsonPropertyName("vendor")]             string Vendor,
    [property: JsonPropertyName("compute_units")]      uint ComputeUnits,
    [property: JsonPropertyName("max_frequency_mhz")]  uint MaxFrequencyMhz,
    [property: JsonPropertyName("global_memory_gib")]  double GlobalMemoryGib,
    [property: JsonPropertyName("max_work_group_size")] ulong MaxWorkGroupSize
);

public record BenchmarkResult(
    [property: JsonPropertyName("benchmark")]   string Benchmark,
    [property: JsonPropertyName("version")]     string Version,
    [property: JsonPropertyName("type")]        string Type,
    [property: JsonPropertyName("timestamp")]   string Timestamp,
    [property: JsonPropertyName("system")]      SystemInfo? System,    // populated by .NET after C run
    [property: JsonPropertyName("device")]      GpuDevice? Device,
    [property: JsonPropertyName("memory")]      MemoryInfo? Memory,    // populated by .NET after C run
    [property: JsonPropertyName("cache")]       CacheInfo? Cache,      // populated by .NET after C run
    [property: JsonPropertyName("config")]      ConfigInfo Config,
    [property: JsonPropertyName("results")]     BenchmarkResults Results,
    [property: JsonPropertyName("validation")]  string Validation
);
