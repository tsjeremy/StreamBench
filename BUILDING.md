# Building StreamBench from Source

This guide covers building StreamBench from source. If you just want to run benchmarks, see the [pre-built binaries](https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.18) — no build required.

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
├── README.md                 # Main README (pre-built binaries, features, results)
├── BUILDING.md               # Build from source guide
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

---
[← Back to README](README.md)
