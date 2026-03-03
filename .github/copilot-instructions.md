# Copilot Instructions — StreamBench

## Architecture

StreamBench is a **two-layer** benchmark:

| Layer | Technology | Role |
|-------|-----------|------|
| **C backends** (`stream.c`, `stream_gpu.c`) | C + OpenMP / OpenCL | Run memory bandwidth kernels, emit JSON to stdout |
| **.NET frontend** (`StreamBench/`) | .NET 10 | Parse JSON, display colored tables, save CSV/JSON, run AI benchmark |

The C backends are **headless** — they only write structured JSON to stdout.
The .NET app launches them as child processes, captures stdout, and does all user-facing I/O.

## AI Inference Benchmark

The AI benchmark uses **Foundry Local CLI + REST API** (not NuGet packages):
- `AiBenchmarkRunner.cs` finds the Foundry CLI (`foundry` or `foundrylocal`) on PATH
- Starts the Foundry service, then calls `POST /v1/chat/completions` via HttpClient
- AI support is compile-time opt-in: `-p:EnableAI=true` MSBuild property, guarded by `#if ENABLE_AI`

## Diagnostics / Logging

All diagnostic logging goes through **`TraceLog.cs`** (simple file-based logger):
- Format: `[ISO-timestamp] [LEVEL] message`
- **`DiagnosticHelper.cs`** is the logging facade with `[CallerMemberName]` / `[CallerFilePath]` attributes
- No ETW, no EventSource — just structured text files

## Build Commands

```powershell
# Standard build (memory benchmarks only)
dotnet build StreamBench/StreamBench.csproj --configuration Release

# AI-enabled publish
dotnet publish StreamBench/StreamBench.csproj -c Release -p:EnableAI=true

# Full build (C backends + .NET, Windows)
.\build_all_windows.ps1
```

## Key Conventions

- C backends output **JSON to stdout** — never add `printf` that breaks JSON parsing
- GPU backend loads OpenCL **dynamically** (`LoadLibrary`/`dlopen`) — no SDK dependency
- `.csproj` embeds pre-built C binaries as `EmbeddedResource` items
- Version string appears in `stream.c` line 3, `stream_gpu.c` line 3, and `StreamBench.csproj` `<Version>`
- Result files: `stream_cpu_results_{size}.csv/json`, `stream_gpu_results_{size}.csv/json`
- Launcher scripts (`run_stream.ps1`, `run_stream_ai.ps1`) auto-detect source vs standalone mode
- `setup.ps1` handles first-time VC++ Redistributable and Foundry Local installation
