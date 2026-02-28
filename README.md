# STREAM Benchmark for Windows

This repository contains a Windows-focused port of the STREAM memory bandwidth benchmark by John D. McCalpin.
Use the STREAM run rules when publishing numbers so results stay comparable and industry-standard.

## Features

- Native Windows timing (`QueryPerformanceCounter`)
- x64 and ARM64 build support with MSVC
- OpenMP parallel kernels (Copy, Scale, Add, Triad)
- CSV output for each run
- Optional range testing across multiple array sizes

## Build (Windows)

### Prerequisites

- Visual Studio with "Desktop development with C++"
- Run commands from the matching Developer Command Prompt:
  - x64 Native Tools Command Prompt (x64 targets)
  - ARM64 Native Tools or x64_arm64 Cross Tools (ARM64 targets)

### Build commands

```cmd
:: Baseline
cl.exe /O2 /openmp /Fe:stream.exe stream.c

:: Recommended for stable bandwidth numbers
cl.exe /O2 /openmp /fp:fast /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /Fe:stream_tuned.exe stream.c

:: Optional if supported by your MSVC toolset
cl.exe /O2 /openmp:llvm /fp:fast /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /Fe:stream_tuned.exe stream.c

:: Range testing build
cl.exe /O2 /openmp /fp:fast /DTUNED /DSTART_SIZE=50000000 /DEND_SIZE=200000000 /DSTEP_SIZE=25000000 /DNTIMES=30 /Fe:stream_range.exe stream.c
```

## Run for accurate, reproducible bandwidth

1. Use AC power and a performance-oriented Windows power profile.
2. Close heavy background workloads.
3. Pin OpenMP placement and sweep thread counts to find the best configuration on each processor.
4. Keep arrays large enough that each kernel reports at least 20 clock ticks.

PowerShell example:

```powershell
$env:OMP_PROC_BIND = "close"
$env:OMP_PLACES = "cores"
foreach ($t in 4, 6, 8, 10, 12, 16, 24) {
  $env:OMP_NUM_THREADS = "$t"
  Write-Host "=== OMP_NUM_THREADS=$t ==="
  .\stream_tuned.exe | Select-String "^Triad:"
}
Remove-Item Env:OMP_NUM_THREADS,Env:OMP_PROC_BIND,Env:OMP_PLACES -ErrorAction SilentlyContinue
```

For range mode, run `stream_range.exe`; it writes one consolidated CSV file for all tested sizes.

## Interpreting output correctly

- STREAM reports bandwidth in **MB/s** (decimal: 1 MB = 10⁶ bytes).
- Convert to **GB/s** with `GB/s = MB/s / 1000`.
- Memory sizes in the console output and CSV use **MiB/GiB** (binary: 1 MiB = 2²⁰ bytes).
- Triad is typically used as the primary sustained-memory metric.
- Bandwidth efficiency can be estimated with:
  - `efficiency = measured_bandwidth / theoretical_peak_bandwidth`

## Comparing numbers correctly

- This repository runs **CPU STREAM** kernels; do not compare directly to GPU-focused copy tools.
- `rocm_bandwidth_test` primarily measures host/device and device/device transfer paths, not CPU STREAM Triad.
- On AMD Strix Halo (Ryzen AI Max), CPU STREAM is limited by the CCD-to-SoC fabric links
  (~128 GB/s aggregate for 16 cores), **not** by DRAM bandwidth (256 GB/s with LPDDR5x-8000).
  Expect CPU STREAM Triad around **100–120 GB/s**; GPU memory tests can reach 215–234 GB/s.
- On x86 with write-allocate and temporal stores, memory-interface traffic is higher than STREAM byte counting:
  - `Copy/Scale`: approximate hardware traffic is `STREAM * 1.5`
  - `Add/Triad`: approximate hardware traffic is `STREAM * 4/3`
- Example: `Triad = 100000 MB/s` in STREAM corresponds to about `133000 MB/s` hardware traffic for the same run.

## If your result is lower than expected

1. Confirm you are comparing against **CPU STREAM** numbers, not GPU transfer benchmarks.
2. Keep AC power enabled and use a performance-oriented power profile.
3. Sweep `OMP_NUM_THREADS` with fixed placement (`OMP_PROC_BIND`, `OMP_PLACES`).
4. Build with the tuned command and verify `Solution Validates`.

When publishing tuned or modified builds, label results clearly (for example, "tuned STREAM benchmark results") per STREAM run rules.

## Original project and license

- STREAM reference: http://www.cs.virginia.edu/stream/ref.html
- Original source lineage: `stream.c`, `stream.f`
- License: see [LICENSE.txt](LICENSE.txt) and license header in `stream.c`
