# Copilot Instructions for STREAM Benchmark (Windows Port)

This repository is a Windows-focused STREAM benchmark port with optional GCC/MinGW paths.

## Build commands

Use a Visual Studio Developer Command Prompt that matches your target architecture.

```cmd
:: Baseline
cl.exe /O2 /openmp /Fe:stream.exe stream.c

:: Recommended tuned run
cl.exe /O2 /openmp /fp:fast /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /Fe:stream_tuned.exe stream.c

:: Optional if supported by your MSVC toolset
cl.exe /O2 /openmp:llvm /fp:fast /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /Fe:stream_tuned.exe stream.c

:: Range testing build
cl.exe /O2 /openmp /fp:fast /DTUNED /DSTART_SIZE=50000000 /DEND_SIZE=200000000 /DSTEP_SIZE=25000000 /DNTIMES=30 /Fe:stream_range.exe stream.c
```

Makefile-based alternatives:

```cmd
make stream_c.exe
make stream_f.exe
```

## Test/lint commands

- No dedicated lint or unit-test framework exists in this repository.
- Runtime correctness check: run the binary and confirm `Solution Validates`.
- Single benchmark check for Triad:

```powershell
.\stream_tuned.exe | Select-String "^Triad:"
```

## High-level architecture

- `stream.c` is the primary implementation:
  - `main()` selects single-size mode or range mode.
  - `run_stream_test()` allocates arrays, runs kernels (`Copy`, `Scale`, `Add`, `Triad`), validates output, and writes CSV.
  - Windows timing uses `QueryPerformanceCounter`; non-Windows uses `gettimeofday`.
  - Windows allocation uses 64-byte alignment and optional large-page allocation.
- `stream.f` is the Fortran reference variant.
- `mysecond.c` is a timing helper used for Fortran builds.

## Key conventions

- STREAM outputs bandwidth in MB/s; convert to GB/s by dividing by 1000.
- Reported bandwidth uses the best (minimum-time) iteration after skipping the first iteration.
- Keep arrays large enough to produce at least 20 clock ticks per kernel.
- For reproducible runs, set OpenMP placement (`OMP_PROC_BIND`, `OMP_PLACES`) and sweep `OMP_NUM_THREADS`.
- Do not compare CPU STREAM Triad directly to ROCm host/device copy benchmarks.
- On x86 with write-allocate (temporal stores), approximate hardware byte-count bandwidth is:
  - `Copy/Scale ~= STREAM * 1.5`
  - `Add/Triad ~= STREAM * 4/3`
- Label tuned/modified builds clearly when publishing results, per STREAM run rules.
- Keep `size_t` formatting as `%zu` for cross-architecture correctness.
