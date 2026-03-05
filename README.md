# STREAM Memory Bandwidth Benchmark

A cross-platform **memory bandwidth benchmark** with both **CPU** and **GPU** versions, based on the
industry-standard [STREAM benchmark](http://www.cs.virginia.edu/stream/ref.html) by John D. McCalpin.
Also includes an **AI inference benchmark** using [Microsoft AI Foundry Local](https://learn.microsoft.com/en-us/azure/ai-foundry/foundry-local/)
to measure LLM response time and tokens/second on CPU, GPU, and NPU.

## Architecture

| Component | Technology | Role |
|-----------|-----------|------|
| `stream.c` | C + OpenMP | CPU memory bandwidth kernels (headless backend, outputs JSON) |
| `stream_gpu.c` | C + OpenCL | GPU memory bandwidth kernels (headless backend, outputs JSON) |
| `StreamBench/` | .NET 10 | User-facing CLI — colored output, JSON/CSV saving, AI inference benchmark |

The C backends run the performance-critical kernels and output raw JSON to stdout.
The **StreamBench** .NET app is the primary entry point — it launches the C backend,
displays color-formatted results, saves files, and runs the AI inference benchmark.

```
  User -> StreamBench (.NET 10) -> stream_cpu / stream_gpu (C)
                                        | JSON on stdout
                        <- display colored table, save .csv / .json

  User -> StreamBench (.NET 10) --ai -> Foundry Local CLI + REST API
                                        | runs SLM on CPU / GPU / NPU
                        <- display inference timing, tokens/sec, save .json
```

---

## Download & Run (Pre-built Binaries — No Build Required)

Pre-built binaries for **Windows** and **macOS** (x64 + ARM64) are available on the
[Releases page](https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.18).
No compiler, .NET SDK, or build tools needed — just download and run.

Each `StreamBench` binary has the CPU and GPU benchmark engines **embedded inside**,
so you only need a single download. The benchmarks still run as native C code for
maximum performance — StreamBench extracts them automatically on first run.

> **Windows users**: A standalone **zip package** (`StreamBench_v5.10.18_win_standalone.zip`)
> is also available — download one file, extract, and run. Includes setup script,
> launcher scripts, and all four Windows executables (standard + AI-enabled).

### Windows — Standalone ZIP (recommended)

1. Go to the **[v5.10.18 Release](https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.18)**
2. Download **`StreamBench_v5.10.18_win_standalone.zip`**
3. Extract to any folder and run:

```powershell
# First-time setup (installs prerequisites: VC++ Redist, .NET 10 Runtime,
# PowerShell 7, and Foundry Local for AI — all silent via winget)
.\.setup.ps1

# Memory benchmark (CPU + GPU) — auto-runs setup.ps1 if prerequisites are missing
.\run_stream.ps1

# Memory + AI benchmark — auto-runs setup.ps1 if prerequisites are missing
.\run_stream_ai.ps1
```

### Windows — Individual exe download

1. Go to the **[v5.10.18 Release](https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.18)**
2. Download the exe for your architecture:

| File | Description |
|------|-------------|
| `StreamBench_win_x64.exe` | Memory benchmark only (x64) |
| `StreamBench_win_arm64.exe` | Memory benchmark only (ARM64) |
| `StreamBench_win_x64_ai.exe` | Memory + AI benchmark (x64) |
| `StreamBench_win_arm64_ai.exe` | Memory + AI benchmark (ARM64) |

3. Run it:

```powershell
# CPU benchmark
.\StreamBench_win_x64.exe --cpu

# GPU benchmark
.\StreamBench_win_x64.exe --gpu

# AI inference benchmark (requires AI-enabled exe + Foundry Local)
# The _ai binary auto-runs memory + AI on all devices (CPU/GPU/NPU) with no flags needed:
.\StreamBench_win_x64_ai.exe

# Or specify devices explicitly:
.\StreamBench_win_x64_ai.exe --ai --ai-device cpu,gpu
```

> **ARM64 Windows users** (Snapdragon/Qualcomm): Use `*_arm64*` variants instead.

#### One-liner PowerShell (copy-paste)

```powershell
Invoke-WebRequest "https://github.com/tsjeremy/StreamBench/releases/download/v5.10.18/StreamBench_win_x64.exe" -OutFile StreamBench.exe; .\StreamBench.exe --cpu
```

### macOS — Download and run

1. Go to the **[v5.10.18 Release](https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.18)**
2. Download **`StreamBench_osx-arm64`** (Apple Silicon) or **`StreamBench_osx-x64`** (Intel)
3. Run it:

```bash
chmod +x StreamBench_osx-arm64
./StreamBench_osx-arm64 --cpu
./StreamBench_osx-arm64 --gpu
```

#### One-liner bash (copy-paste into Terminal)

```bash
curl -fLO https://github.com/tsjeremy/StreamBench/releases/download/v5.10.18/StreamBench_osx-arm64 && chmod +x StreamBench_osx-arm64 && ./StreamBench_osx-arm64 --cpu
```

### Using the launcher scripts (alternative)

The launcher scripts are available as separate downloads on the
[release page](https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.18).

- **`setup.ps1`**: first-time setup — installs VC++ Redistributable, .NET 10 Runtime, PowerShell 7, and Foundry Local (all silent via winget; standalone mode auto-detected)
- **`run_stream.ps1`**: default memory benchmark launcher (CPU + GPU only) — **auto-runs `setup.ps1`** if prerequisites are missing
- **`run_stream_ai.ps1`**: memory + AI launcher (CPU + GPU + AI, uses `*_ai.exe` when available) — **auto-runs `setup.ps1`** if prerequisites are missing

Download the script(s) alongside the `StreamBench_*` binary and run:

- **Windows**:
  ```powershell
  # If blocked by execution policy ("not digitally signed" error), unblock first:
  Unblock-File .\run_stream.ps1
  .\run_stream.ps1

  # Memory + AI launcher:
  Unblock-File .\run_stream_ai.ps1
  .\run_stream_ai.ps1

  # Or run directly with bypass:
  pwsh -ExecutionPolicy Bypass -File .\run_stream.ps1
  pwsh -ExecutionPolicy Bypass -File .\run_stream_ai.ps1
  ```
- **macOS/Linux**: `pwsh ./run_stream.ps1` or `pwsh ./run_stream_ai.ps1`

Launcher environment overrides:

```powershell
# Override default 200M array size for both launchers
$env:STREAMBENCH_ARRAY_SIZE = "100000000"

# Optional AI launcher overrides (run_stream_ai.ps1)
$env:STREAMBENCH_AI_MODEL = "phi-4-mini"
$env:STREAMBENCH_AI_DEVICES = "cpu,npu"   # if unset, all detected devices are used
$env:STREAMBENCH_AI_NO_DOWNLOAD = "1"     # cached models only
```

### Standalone C backend binaries (advanced)

The individual C backend binaries (`stream_cpu_*`, `stream_gpu_*`) are also available on the
release page for users who want to run them directly without the StreamBench frontend:

```bash
# Run C backend directly (outputs raw JSON to stdout)
./stream_cpu_macos_arm64 --array-size 200000000
./stream_gpu_win_x64.exe --array-size 200000000
```

---

## Build from Source

### Quick Start

### Prerequisites

- **.NET 10 SDK** — [https://dot.net](https://dot.net)
- **C compiler** — MSVC (Windows), GCC (Linux), Clang (macOS)
- For CPU OpenMP: `libomp` on macOS (`brew install libomp`), `libomp-dev` on Linux
- For GPU: GPU drivers installed (OpenCL is loaded dynamically — no SDK needed)

### New Windows PC setup (recommended)

If you're setting up a fresh Windows machine for source builds and `--ai`, run:

```powershell
# .NET SDK (build + dotnet run)
winget install Microsoft.DotNet.SDK.10

# AI benchmark backend
winget install Microsoft.FoundryLocal

# Windows ARM64 only: install x64 .NET runtime for mixed-arch dependencies
winget install --id Microsoft.DotNet.Runtime.10 --architecture x64
```

> **Windows ARM64 note (Snapdragon/Qualcomm):** StreamBench now auto-uses `win-x64`
> for local `dotnet run` on ARM64 Windows. You only need the x64 runtime install command above.

### 1. Build everything

```powershell
# macOS / Linux  (requires PowerShell 7+: https://aka.ms/powershell)
pwsh ./build_all_macos.ps1   # or build_all_linux.ps1
# -> produces stream_cpu_macos_arm64, stream_gpu_macos_arm64, and builds StreamBench/

# Windows (run in any terminal — script auto-finds MSVC)
.\build_all_windows.ps1
# -> produces stream_cpu_win_x64.exe, stream_gpu_win_x64.exe, and builds StreamBench/
```

### 2. Run (CPU benchmark)

```bash
dotnet run --project StreamBench -- --cpu --array-size 200M
```

### 3. Run (GPU benchmark)

```bash
dotnet run --project StreamBench -- --gpu --array-size 100M
```

### 4. Range test (sweep array sizes)

```bash
dotnet run --project StreamBench -- --cpu --range 50M:200M:50M
```

### StreamBench CLI options

```
--cpu                    Run CPU benchmark only
--gpu                    Run GPU benchmark only
(no flag)                Run both CPU and GPU benchmarks (default)
--array-size N           Array size in elements (e.g. 200M, 100000000)
--range START:END:STEP   Range test multiple array sizes (e.g. 50M:200M:50M)
--no-save                Don't write CSV/JSON files
--output-dir DIR         Directory for output files (default: current dir)
--exe PATH               Explicit path to the C backend executable
--help                   Show help

AI Inference Benchmark (requires Microsoft AI Foundry Local):
--ai                     Add AI inference benchmark (memory benchmarks still run by default)
--ai-device LIST         Comma-separated devices: cpu, gpu, npu (default: all)
--ai-model ALIAS         Model alias to use (e.g. phi-3.5-mini, qwen2.5-0.5b)
```

---

## AI Inference Benchmark (`--ai`)

StreamBench includes an AI inference benchmark powered by
**[Microsoft AI Foundry Local](https://learn.microsoft.com/en-us/azure/ai-foundry/foundry-local/)**,
which runs small language models (SLMs) directly on-device with hardware acceleration.

### What it measures

| Metric | Description |
|--------|-------------|
| **Model load time** | Time to load the model into device memory (one-time cost) |
| **Q1 response time** | Time for the first inference — "Hello World!" |
| **Q1 total time** | Model load + Q1 response (what a cold-start user experiences) |
| **Q2 response time** | Time for the second inference — "How to calculate memory bandwidth on different memory?" |
| **Tokens/second** | Output throughput (completion tokens ÷ inference time) |

The benchmark runs Q1 immediately after model loading (cold run), then Q2 with the model
already resident in memory (warm run). This lets you compare cold-start latency with
sustained inference throughput across CPU, GPU, and NPU.

### Prerequisites

Microsoft AI Foundry Local must be installed on the target machine:

```powershell
# Windows
winget install Microsoft.FoundryLocal
```

```bash
# macOS
brew install foundrylocal
```

### Running the AI benchmark

```powershell
# Memory-only default (CPU + GPU)
.\StreamBench.exe

# Add AI benchmark on all available AI devices (CPU/GPU/NPU)
.\StreamBench.exe --ai

# Benchmark specific devices
.\StreamBench.exe --ai --ai-device cpu,gpu

# Use a specific model
.\StreamBench.exe --ai --ai-model phi-3.5-mini

# Don't save the JSON result file
.\StreamBench.exe --ai --no-save
```

StreamBench prints full Q1/Q2 answers and, when memory JSON exists, also runs
relation questions (Q3 and future Q4/Q5...) on each selected AI device.
All relation questions use the same question output style and the same
latency/tokens-per-second reporting method.

When benchmarking multiple devices together, StreamBench chooses shared model aliases
by this order:

1. Highest selected-device coverage (CPU/GPU/NPU variants available)
2. Most cached variants for the selected devices
3. Internal shared-priority list

If no alias covers all selected devices, StreamBench automatically falls back to
the best partial coverage and then runs best-per-device comparison pass.

If NPU model load fails during automatic multi-device comparison, StreamBench
continues with CPU/GPU to avoid long repeated retries in the same run.

For single-device runs, StreamBench uses device-specific priority lists and prefers
cached models first to reduce download/startup time.

### Example output

```
══════════════════════════════════════════════════════════════
  AI Inference Benchmark — Microsoft.AI.Foundry.Local
══════════════════════════════════════════════════════════════
  Q1 (cold): Hello World!
  Q2 (warm): How to calculate memory bandwidth on different memory?

── AI Benchmark: CPU (qwen2.5-0.5b-instruct-generic-cpu) ──
╭──────────────── Model Info ─────────────────╮
│ Device             │ CPU                    │
│ Model ID           │ qwen2.5-0.5b-instruct… │
│ Execution Provider │ CPUExecutionProvider   │
╰────────────────────┴────────────────────────╯
╭────── Inference Timing ──────────────────────────────────────────────╮
│ Run                   │ Load (s) │ Response (s) │ Total (s) │ Tok/s  │
├───────────────────────┼──────────┼──────────────┼───────────┼────────┤
│ Q1 (cold, incl. load) │    1.243 │        3.517 │     4.760 │  42.3  │
│ Q2 (warm)             │       —  │        2.891 │     2.891 │  51.6  │
╰───────────────────────┴──────────┴──────────────┴───────────┴────────╯
```

### Saved output

Results are saved as `ai_inference_benchmark_<timestamp>.json` with full details
including model info, per-run timings, token counts, full Q1/Q2 response text,
and response previews.

When memory JSON exists in the output directory, StreamBench also runs and saves
`ai_relation_summary_<model-alias>_<timestamp>.json` containing Q1 (cold),
Q2 (warm), Q3 (local JSON summary), and future Qn relation prompts per device,
plus parsed cross-file relation aggregates for model comparison over time.
This relation summary uses a unified `questions` array schema (`index`,
`question`, `answer`, `device_type`, `run`) so future prompts (Q4/Q5...) keep
the same JSON/log/CLI structure and timing metrics.

In addition, after AI completes, StreamBench embeds these AI sections into each
memory benchmark JSON (`stream_cpu_results_*.json`, `stream_gpu_results_*.json`,
`stream_npu_results_*.json`) so Q1/Q2/Q3 (and future Qn) remain available in
the same saved file:

- `ai_inference_benchmark` (Q1/Q2 runs)
- `ai_relation_summary` (device-tagged relation question answers and timing)

### Interpreting results

- **Higher tokens/second** = better inference throughput (limited by memory bandwidth)
- **Lower model load time** = faster cold start (depends on storage speed and model size)
- **NPU > GPU > CPU** in tokens/second is typical for small models on compatible hardware
- Compare Q1 total time vs Q2 time to understand the impact of model loading

The tokens/second metric is directly comparable to your memory bandwidth results
(higher memory bandwidth → higher tokens/second, especially for CPU inference).

---

## Features

### .NET 10 Frontend (`StreamBench/`)

- **Rich colored output** using platform-native .NET Console API — works on Windows Terminal, macOS Terminal, Linux
- **Formatted tables** for system info, memory modules, cache hierarchy, and benchmark results
- **JSON and CSV file saving** — consistent format for analysis and archiving
- **Range testing** — sweep multiple array sizes, save consolidated CSV
- **AI-extensible** — .NET 10 platform for future analysis and AI features

### CPU Backend (`stream.c`)

- OpenMP multi-threading with automatic core detection
- Native Windows support (`QueryPerformanceCounter`, `GetSystemInfo`)
- Tuned kernel variants (`/DTUNED`)
- x64 and ARM64 support
- Runtime `--array-size N` argument

### GPU Backend (`stream_gpu.c`)

- **Zero SDK dependency** — OpenCL loaded dynamically via `LoadLibrary` / `dlopen`
- Works with any OpenCL-capable GPU: AMD, NVIDIA, Intel, Apple
- Automatic GPU discovery and device info
- Runtime `--array-size N` argument

---

## Detailed Compilation Guide (C backends only)

### Prerequisites

| Platform | CPU version | GPU version |
|----------|------------|------------|
| **Windows** | Visual Studio with "Desktop development with C++" | Same (GPU drivers provide `OpenCL.dll`) |
| **Linux** | `gcc` and `libomp-dev` (or equivalent) | `gcc` and GPU drivers with OpenCL ICD (`mesa-opencl-icd`, `rocm-opencl-runtime`, or `nvidia-opencl-icd`) |
| **macOS** | Xcode Command Line Tools + `libomp` (via Homebrew) | Same (OpenCL ships with macOS) |

### Windows — Step by Step

#### Step 0: Install Visual Studio (one-time setup)

Open **PowerShell** or **Command Prompt** and run:

```cmd
winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive"
```

This single command downloads and installs Visual Studio Community 2022 (free) with the
**"Desktop development with C++"** workload, which includes `cl.exe`, the linker, Windows SDK,
and the Developer Command Prompts. Restart your PC if prompted.

> **Already have Visual Studio?** Run the same command — winget will detect the existing
> installation and add the C++ workload if missing.
>
> **Want the latest Visual Studio 2026?** Use `Microsoft.VisualStudio.Community` instead of
> `Microsoft.VisualStudio.2022.Community`.

#### Step 1: Verify GPU Drivers (for GPU version)

Your GPU drivers already include the OpenCL runtime — no extra SDK is needed.
To verify, open **PowerShell** and run:

```powershell
Test-Path "$env:SystemRoot\System32\OpenCL.dll"
```

If this returns `True`, you're ready. If `False`, download the latest drivers:
*   **AMD**: [https://www.amd.com/en/support](https://www.amd.com/en/support)
*   **NVIDIA**: [https://www.nvidia.com/drivers](https://www.nvidia.com/drivers)
*   **Intel**: [https://www.intel.com/content/www/us/en/download-center](https://www.intel.com/content/www/us/en/download-center)

#### Step 2: Open the Developer Command Prompt

You need the **Developer Command Prompt**, not a regular Command Prompt or PowerShell.
It sets up the compiler paths automatically.

**How to find it in Windows 11:**
1.  Press the **Start** button (or press the `Win` key).
2.  Type **"x64 Native Tools"** in the search box.
3.  Click **"x64 Native Tools Command Prompt for VS 2022"** (or your VS version).

> **Tip:** Right-click it and choose **"Pin to taskbar"** for quick access next time.
>
> **ARM64 users** (e.g., Snapdragon/Qualcomm laptops): Search for
> **"ARM64 Native Tools Command Prompt"** instead.

You'll see a prompt like:
```
**********************************************************************
** Visual Studio 2022 Developer Command Prompt v17.x
**********************************************************************
C:\Program Files\Microsoft Visual Studio\2022\Community>
```

#### Step 3: Navigate to the Source Code

```cmd
cd /d C:\path\to\StreamBench
```

> Replace `C:\path\to\StreamBench` with the actual folder where you saved the source files.
> For example: `cd /d C:\Users\YourName\Downloads\StreamBench`

#### Step 4: Compile and Run

    **CPU — Basic:**
    ```cmd
    cl.exe /O2 /openmp /Fe:stream.exe stream.c
    stream.exe
    ```

    **CPU — Optimized (recommended for benchmarking):**
    ```cmd
    cl.exe /O2 /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /openmp /Fe:stream.exe stream.c
    stream.exe
    ```

    **CPU — Range Testing** (sweep from 50M to 200M elements):
    ```cmd
    cl.exe /O2 /DTUNED /DSTART_SIZE=50000000 /DEND_SIZE=200000000 /DSTEP_SIZE=50000000 /DNTIMES=20 /openmp /Fe:stream_range.exe stream.c
    ```

    **GPU — Basic:**
    ```cmd
    cl.exe /O2 /Fe:stream_gpu.exe stream_gpu.c
    stream_gpu.exe
    ```

    **GPU — Custom array size:**
    ```cmd
    cl.exe /O2 /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=20 /Fe:stream_gpu.exe stream_gpu.c
    stream_gpu.exe
    ```

> **Note:** You do NOT need `/openmp` for the GPU version — it uses OpenCL instead of OpenMP.

### Linux — Step by Step

```bash
# Install prerequisites (Ubuntu/Debian example)
sudo apt install build-essential libomp-dev

# For AMD GPU OpenCL support:
sudo apt install mesa-opencl-icd clinfo
# Or for NVIDIA:
# sudo apt install nvidia-opencl-icd

# Compile
gcc -O2 -fopenmp -o stream stream.c                 # CPU
gcc -O2 -o stream_gpu stream_gpu.c -ldl -lm          # GPU

# Run
./stream
./stream_gpu
```

### macOS — Step by Step

```bash
# Install prerequisites
xcode-select --install
brew install libomp          # For OpenMP support in CPU version

# Compile
clang -O2 -Xpreprocessor -fopenmp -lomp -o stream stream.c     # CPU
clang -O2 -o stream_gpu stream_gpu.c -lm                       # GPU

# Run
./stream
./stream_gpu
```

> **Note:** macOS has built-in OpenCL support — no additional drivers needed.
> Apple deprecated OpenCL in favor of Metal, but it still works on current macOS versions.

### Compiler Options Reference

| Option | Description |
|--------|-------------|
| `/O2` or `-O2` | Enable speed optimizations |
| `/openmp` or `-fopenmp` | Enable OpenMP multi-threading (CPU version only) |
| `/Fe:name` or `-o name` | Set output executable name |
| `/DSTREAM_ARRAY_SIZE=N` or `-DSTREAM_ARRAY_SIZE=N` | Set array size in elements (default: 200M for GPU, 100M for CPU) |
| `/DNTIMES=N` or `-DNTIMES=N` | Number of iterations (default: 100 CPU, 20 GPU) |
| `/DTUNED` or `-DTUNED` | Enable tuned kernel functions (CPU only) |
| `/DSTART_SIZE=N` | Start of range testing (CPU only) |
| `/DEND_SIZE=N` | End of range testing (CPU only) |
| `/DSTEP_SIZE=N` | Step size for range testing (CPU only) |

---

## How to Run and Interpret Results

### Running

```cmd
:: Windows
stream.exe
stream_gpu.exe

:: Linux / macOS
./stream
./stream_gpu
```

### Understanding the Output

Both versions report four kernels:

| Kernel | Operation | Bytes per element |
|--------|-----------|------------------|
| **Copy** | `c[i] = a[i]` | 2 × 8 = 16 bytes (read + write) |
| **Scale** | `b[i] = scalar × c[i]` | 2 × 8 = 16 bytes |
| **Add** | `c[i] = a[i] + b[i]` | 3 × 8 = 24 bytes (2 reads + 1 write) |
| **Triad** | `a[i] = b[i] + scalar × c[i]` | 3 × 8 = 24 bytes |

The key metric is **Best Rate MB/s** — this is the peak sustained memory bandwidth your system achieves.

### CSV Output

Results are automatically saved to CSV files:
*   CPU: `stream_results_<size>M.csv`
*   GPU: `stream_gpu_results_<size>M.csv`
*   NPU: `stream_npu_results_<size>M.csv`
*   Range testing: `stream_range_results_<start>M_to_<end>M_step_<step>M.csv`

### JSON Output

Results are also saved as JSON files with full system/device information for easy side-by-side comparison across different machines:
*   CPU: `stream_cpu_results_<size>M.json`
*   GPU: `stream_gpu_results_<size>M.json`
*   NPU: `stream_npu_results_<size>M.json`

JSON files include:
*   **System info**: hostname, OS, architecture, CPU model, logical CPUs, CPU frequency, total RAM, NUMA nodes
*   **Memory hardware** (via SMBIOS): memory type (DDR4/DDR5/LPDDR5X), speed (MT/s), configured speed, slots populated/total, per-module details (size, rank, manufacturer, part number, data width, form factor)
*   **Cache hierarchy**: L1 data/instruction (per core), L2 (per core), L3 (total)
*   **Device info** (GPU only): GPU name, vendor, compute units, frequency, VRAM, work group size
*   **Config**: array size, bytes per element, total memory used, iterations
*   **Results**: best rate (MB/s), avg/min/max time for Copy, Scale, Add, Triad
*   **Timestamp**: ISO 8601 format for tracking when benchmarks were run

---

---

## What Bandwidth Should I Expect?

The results depend on your memory type, number of channels, and frequency:

| Memory Type | Typical Config | Theoretical Max | Expected CPU STREAM | Expected GPU STREAM |
|-------------|---------------|-----------------|--------------------|--------------------|
| DDR4-3200 | Dual-channel | ~51 GB/s | ~35–45 GB/s | N/A (no iGPU BW advantage) |
| DDR5-5600 | Dual-channel | ~90 GB/s | ~55–70 GB/s | ~60–80 GB/s |
| DDR5-6400 | Dual-channel | ~102 GB/s | ~65–80 GB/s | ~70–90 GB/s |
| LPDDR5X-7500 | Quad-channel | ~120 GB/s | ~70–90 GB/s | ~90–110 GB/s |
| LPDDR5X-8000 | 8-channel | ~256 GB/s | ~90–110 GB/s | ~180–220 GB/s |
| LPDDR5-6400 (Apple M1 Ultra) | 1024-bit unified | ~819 GB/s | ~280–300 GB/s (20-thread) | ~600–680 GB/s |

> **Tip:** If your results are significantly below these ranges, check that all memory channels are
> populated, XMP/EXPO profiles are enabled in BIOS, and the system is plugged in (not on battery).

---

## Troubleshooting

### CPU Version

| Problem | Solution |
|---------|----------|
| `'cl.exe' is not recognized` | You opened a regular Command Prompt or PowerShell instead of the **Developer Command Prompt**. Search the Start Menu for **"x64 Native Tools Command Prompt for VS"**. |
| `VCOMP140.DLL was not found` | The exe was compiled with `/openmp`, which requires the **Visual C++ Redistributable** on the target machine. **Fix:** Install [VC++ Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe), or use `run_stream.ps1` which auto-detects and installs it. On ARM64: [VC++ Redistributable ARM64](https://aka.ms/vs/17/release/vc_redist.arm64.exe). You can also install via winget: `winget install Microsoft.VCRedist.2015+.x64` |
| Very low bandwidth | Ensure OpenMP is enabled (`/openmp` or `-fopenmp`). Without it, only 1 thread is used. |
| "Failed to allocate memory" | Reduce `STREAM_ARRAY_SIZE`. 100M elements needs ~2.4 GB RAM. |
| Results vary wildly | Increase `NTIMES` (e.g., 100) and close background applications. |
| ARM64 compilation errors | Use the ARM64 Native Tools prompt, not x64. |

### GPU Version

| Problem | Solution |
|---------|----------|
| `'cl.exe' is not recognized` | Same as above — use the **Developer Command Prompt**, not a regular one. |
| "Could not load OpenCL library" | Install or update GPU drivers. OpenCL runtime comes with AMD/NVIDIA/Intel drivers. Verify: `Test-Path "$env:SystemRoot\System32\OpenCL.dll"` in PowerShell. |
| "No OpenCL platforms found" | Same as above. On Linux, install `mesa-opencl-icd` or vendor-specific ICD. |
| "Failed to build program" | Your GPU may not support `cl_khr_fp64` (double precision). Compile with `/DGPU_USE_FLOAT` to use single precision. |
| Low GPU bandwidth | Ensure you're not on battery power. Some laptops throttle GPU on battery. |
| macOS OpenCL deprecation warning | Safe to ignore — OpenCL still works on current macOS. |
| Inflated GPU bandwidth on Apple Silicon (e.g. 30+ TB/s) | This is a known bug in Apple's Metal-backed OpenCL layer, which returns bogus profiling timestamps. Fixed in `stream_gpu.c` v1.0.1 — rebuild from source. |

---

## Array Size Guidelines

For accurate bandwidth measurement, the total memory used should be **at least 4× your largest cache**:

| System | Typical L3 Cache | Recommended `STREAM_ARRAY_SIZE` | Total Memory Used |
|--------|------------------|--------------------------------|-------------------|
| Desktop (Intel/AMD) | 16–64 MB | 100,000,000 (100M) | ~2.4 GB |
| Laptop | 8–32 MB | 50,000,000 (50M) | ~1.2 GB |
| Workstation (64+ MB L3) | 64 MB | 200,000,000 (200M) | ~4.5 GB |
| Memory-limited system | — | 10,000,000 (10M) | ~240 MB |

Additional guidance:

- Keep array size consistent when comparing two runs or two devices.
- Very small sizes (for example 5M–20M) can be skewed by cache effects and may not
  represent sustained memory bandwidth.
- If 200M causes memory pressure on your machine, use a smaller size via
  `--array-size` (CLI) or `STREAMBENCH_ARRAY_SIZE` (launcher scripts).

---

## Project Structure

```
StreamBench/
├── stream.c                  # CPU benchmark (OpenMP, cross-platform)
├── stream_gpu.c              # GPU benchmark (OpenCL, cross-platform, no SDK needed)
├── stream_hwinfo.h           # Hardware detection (SMBIOS memory, cache, CPU freq)
├── stream_output.h           # Output formatting (CSV & JSON file generation)
├── stream.f                  # Original Fortran version
├── mysecond.c                # Timer for Fortran version
├── Makefile                  # Build targets for Linux/macOS
├── build_all_windows.ps1     # Build script for Windows (x64 + ARM64, standard + AI)
├── build_all_macos.ps1       # Build script for macOS (Intel + Apple Silicon)
├── build_all_linux.ps1       # Build script for Linux (x64 + ARM64)
├── setup.ps1                 # First-time setup (standalone or source mode)
├── run_stream.ps1            # Memory benchmark launcher (CPU + GPU)
├── run_stream_ai.ps1         # Memory + AI benchmark launcher
├── README.md                 # This file
├── README                    # Original STREAM project notes
├── LICENSE.txt               # License information
└── HISTORY.txt               # Version history

StreamBench/ (.NET 10 frontend)
├── Program.cs                # CLI entry point (--cpu, --gpu, --ai flags)
├── BenchmarkRunner.cs        # C backend process management
├── ConsoleOutput.cs          # Colored output and result tables
├── ResultSaver.cs            # JSON / CSV saving
├── SystemInfoDetector.cs     # Cross-platform hardware detection
├── EmbeddedBackends.cs       # Self-contained binary extraction
├── AiBenchmarkRunner.cs      # AI inference benchmark (Microsoft.AI.Foundry.Local)
└── Models/
    ├── BenchmarkResult.cs          # STREAM benchmark result model
    └── AiInferenceBenchmarkResult.cs  # AI benchmark result model
```

## Original Project

STREAM is the de facto industry standard benchmark for measuring sustained memory bandwidth.
Original code and documentation by **John D. McCalpin, Ph.D.**

*   **Website**: http://www.cs.virginia.edu/stream/ref.html
*   **Original Source**: `stream.c` and `stream.f`

## License

See [LICENSE.txt](LICENSE.txt) and the header in `stream.c` for license information.
GPU results from `stream_gpu.c` must be labelled as "GPU variant of the STREAM benchmark code".
