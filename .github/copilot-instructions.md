# Copilot Instructions — StreamBench

## Architecture

StreamBench is a **two-layer** benchmark:

| Layer | Technology | Role |
|-------|-----------|------|
| **C backends** (`stream.c`, `stream_gpu.c`) | C + OpenMP / OpenCL | Run memory bandwidth kernels, emit JSON to stdout |
| **.NET frontend** (`StreamBench/`) | .NET 10 | Parse JSON, display colored tables, save CSV/JSON, run AI benchmark |

The C backends are **headless** — they only write structured JSON to stdout.
The .NET app launches them as child processes, captures stdout, and does all user-facing I/O.

## Build Commands

```powershell
# Standard build (memory benchmarks only)
dotnet build StreamBench/StreamBench.csproj --configuration Release

# AI-enabled publish
dotnet publish StreamBench/StreamBench.csproj -c Release -p:EnableAI=true

# Full build (C backends + .NET)
.\build_all_windows.ps1    # Windows (auto-finds MSVC)
pwsh ./build_all_macos.ps1 # macOS (requires libomp: brew install libomp)
pwsh ./build_all_linux.ps1 # Linux
```

There is no test suite — validate by building and running the benchmark.

## Versioning

**Single source of truth:** the `VERSION` file at repo root (e.g. `5.10.37`).

Run `.\update-version.ps1 -NewVersion X.Y.Z` to propagate to all locations:
- `VERSION` — root file
- `stream_version.h` — C macros (`STREAM_VERSION`, `STREAM_VERSION_MAJOR/MINOR/PATCH`)
- `stream.c` line 3, `stream_gpu.c` line 3 — revision comments
- `README.md`, `BUILDING.md` — download links

The `.csproj` reads `VERSION` at build time via MSBuild — no manual edit needed there.

## C Backend Contracts

- **JSON purity:** C backends write *only* JSON to stdout. All diagnostics go to stderr. Never add `printf` to stdout — it breaks .NET JSON parsing.
- **Compiler flags:** `/O2 /openmp /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100` (Windows); equivalent `-O2 -fopenmp` on Unix
- **64-byte memory alignment** is mandatory — skipping it silently loses 10%+ bandwidth
- GPU backend loads OpenCL **dynamically** (`LoadLibrary`/`dlopen`) — no SDK dependency
- GPU auto-downgrades to `float` if device lacks `fp64` support
- macOS Metal-backed OpenCL has unreliable event profiling; GPU backend uses wall-clock timing as workaround
- shared headers: `stream_version.h` (version), `stream_hwinfo.h` (HW detect + JSON escape), `stream_output.h` (JSON output), `stream_colors.h` (ANSI codes)

## .NET Frontend Patterns

- **CLI parsing:** manual `switch`/`case` in `Program.cs` (no external CLI library)
  - Benchmark: `--cpu`, `--gpu`, `--array-size N`, `--range START:END:STEP`
  - AI: `--ai`, `--ai-only`, `--ai-backend foundry|lmstudio`, `--ai-model ALIAS`, `--ai-device cpu,gpu,npu`, `--quick-ai`, `--ai-no-download`
- **Embedded backends:** C binaries are bundled as `EmbeddedResource` items, extracted at runtime to `%TEMP%/StreamBench/<version>/` with SHA hash caching (`EmbeddedBackends.cs`)
- **System detection:** `SystemInfoDetector.cs` runs async detection (sysctl/WMI/procfs) in parallel with the benchmark subprocess
- **Console output:** custom markup parser (`[cyan]`, `[bold *]`, `[/]`) in `ConsoleOutput.cs` — no third-party library
- **AI conditional compilation:** all AI code guarded by `#if ENABLE_AI`; AI NuGet packages only included when `-p:EnableAI=true`

## AI Inference Benchmark

The AI benchmark uses **Foundry Local CLI + REST API** (not NuGet packages):
- `AiBenchmarkRunner.cs` finds the Foundry CLI (`foundry` or `foundrylocal`) on PATH
- Starts the Foundry service, then calls `POST /v1/chat/completions` via HttpClient
- AI support is compile-time opt-in: `-p:EnableAI=true` MSBuild property, guarded by `#if ENABLE_AI`
- **Backend factory:** `AiBackendFactory.cs` auto-detects Foundry → LM Studio priority; implements `IAiBackend`
- **Custom chat client:** `DirectOpenAiChatClient.cs` calls `/v1/chat/completions` directly because OpenAI SDK v2 throws on `"tool_calls": []` from non-OpenAI backends

## Diagnostics / Logging

All diagnostic logging goes through **`TraceLog.cs`** (simple file-based logger):
- Format: `[ISO-timestamp] [LEVEL] message`
- **`DiagnosticHelper.cs`** is the logging facade with `[CallerMemberName]` / `[CallerFilePath]` attributes
- **`CliLog.cs`** optionally tees console output to a transcript file (set `STREAMBENCH_CLI_LOG` env var)
- No ETW, no EventSource — just structured text files

## Key Conventions

- C backends output **JSON to stdout** — never add `printf` that breaks JSON parsing
- `.csproj` embeds pre-built C binaries as `EmbeddedResource` items
- Result files: `stream_cpu_results_{size}.csv/json`, `stream_gpu_results_{size}.csv/json`
- AI results: `ai_inference_benchmark_yyyyMMdd_HHmmss.json`
- GPU results include device tag: `stream_gpu_{device}_results_{size}.csv/json`
- Launcher scripts (`run_stream.ps1`, `run_stream_ai.ps1`) auto-detect source vs standalone mode
- `setup.ps1` handles first-time VC++ Redistributable, Foundry Local, and LM Studio installation
- macOS: `DYLD_LIBRARY_PATH` set at runtime to find bundled `libomp.dylib`
- Windows ARM64: builds target `win-x64` RID; requires x64 .NET runtime installed alongside ARM64
