# STREAM Memory Bandwidth Benchmark

A cross-platform **memory bandwidth benchmark** with both **CPU** and **GPU** versions, based on the
industry-standard [STREAM benchmark](http://www.cs.virginia.edu/stream/ref.html) by John D. McCalpin.

| Version | File | What it measures |
|---------|------|-----------------|
| **CPU** | `stream.c` | System memory bandwidth using OpenMP multi-threading |
| **GPU** | `stream_gpu.c` | GPU memory bandwidth using OpenCL (dynamically loaded, no SDK needed) |

Both versions run four kernels — **Copy**, **Scale**, **Add**, **Triad** — and report the best
sustained bandwidth in MB/s. Results are saved to CSV for easy analysis.

---

## Quick Start

### Windows (CPU version)

```cmd
:: 1. Open "x64 Native Tools Command Prompt for VS" (search for it in the Start Menu)
:: 2. Navigate to the source folder, then compile and run:
cd /d C:\path\to\STREAM_windows
cl.exe /O2 /openmp /Fe:stream.exe stream.c
stream.exe
```

> **Note:** The `/openmp` flag requires `VCOMP140.DLL` (part of the [Visual C++ Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe)) on the machine that runs the exe.
> To build a portable exe with no DLL dependencies, omit `/openmp` — it will run single-threaded.

### Windows (GPU version)

```cmd
:: Same Developer Command Prompt — no OpenCL SDK needed, just GPU drivers
cd /d C:\path\to\STREAM_windows
cl.exe /O2 /Fe:stream_gpu.exe stream_gpu.c
stream_gpu.exe
```

### Linux / macOS (both versions)

```bash
# CPU version
make stream_c.exe

# GPU version
make stream_gpu.exe

# Or compile manually:
# Linux
gcc -O2 -fopenmp -o stream stream.c               # CPU
gcc -O2 -o stream_gpu stream_gpu.c -ldl -lm        # GPU

# macOS
clang -O2 -Xpreprocessor -fopenmp -lomp -o stream stream.c   # CPU (needs libomp)
clang -O2 -o stream_gpu stream_gpu.c -lm                     # GPU
```

---

## Features

*   **CPU Version (`stream.c`)**
    *   OpenMP multi-threading with automatic core detection
    *   Native Windows support (`QueryPerformanceCounter`, `GetSystemInfo`)
    *   Range testing mode — sweep across array sizes in one run
    *   Tuned kernel variants (`/DTUNED`)
    *   x64 and ARM64 support

*   **GPU Version (`stream_gpu.c`)**
    *   **Zero SDK dependency** — OpenCL is loaded dynamically at runtime via `LoadLibrary` (Windows) / `dlopen` (Linux/macOS)
    *   Works with any OpenCL-capable GPU: AMD, NVIDIA, Intel, Apple
    *   OpenCL profiling events for accurate GPU-side timing
    *   Automatic GPU discovery and device info reporting
    *   Validation and CSV output identical to CPU version

---

## Detailed Compilation Guide

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
cd /d C:\path\to\STREAM_windows
```

> Replace `C:\path\to\STREAM_windows` with the actual folder where you saved the source files.
> For example: `cd /d C:\Users\YourName\Downloads\STREAM_windows`

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
*   Range testing: `stream_range_results_<start>M_to_<end>M_step_<step>M.csv`

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

> **Tip:** If your results are significantly below these ranges, check that all memory channels are
> populated, XMP/EXPO profiles are enabled in BIOS, and the system is plugged in (not on battery).

---

## Troubleshooting

### CPU Version

| Problem | Solution |
|---------|----------|
| `'cl.exe' is not recognized` | You opened a regular Command Prompt or PowerShell instead of the **Developer Command Prompt**. Search the Start Menu for **"x64 Native Tools Command Prompt for VS"**. |
| `VCOMP140.DLL was not found` | The exe was compiled with `/openmp`, which requires the **Visual C++ Redistributable** on the target machine. **Fix:** Install [VC++ Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe), **or** recompile without OpenMP: `cl.exe /O2 /Fe:stream.exe stream.c` (runs single-threaded). |
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

---

## Array Size Guidelines

For accurate bandwidth measurement, the total memory used should be **at least 4× your largest cache**:

| System | Typical L3 Cache | Recommended `STREAM_ARRAY_SIZE` | Total Memory Used |
|--------|------------------|--------------------------------|-------------------|
| Desktop (Intel/AMD) | 16–64 MB | 100,000,000 (100M) | ~2.4 GB |
| Laptop | 8–32 MB | 50,000,000 (50M) | ~1.2 GB |
| Workstation (64+ MB L3) | 64 MB | 200,000,000 (200M) | ~4.5 GB |
| Memory-limited system | — | 10,000,000 (10M) | ~240 MB |

---

## Project Structure

```
STREAM_windows/
├── stream.c                  # CPU benchmark (OpenMP, cross-platform)
├── stream_gpu.c              # GPU benchmark (OpenCL, cross-platform, no SDK needed)
├── stream.f                  # Original Fortran version
├── mysecond.c                # Timer for Fortran version
├── Makefile                  # Build targets for Linux/macOS
├── README.md                 # This file
├── README                    # Original STREAM project notes
├── LICENSE.txt               # License information
└── HISTORY.txt               # Version history
```

## Original Project

STREAM is the de facto industry standard benchmark for measuring sustained memory bandwidth.
Original code and documentation by **John D. McCalpin, Ph.D.**

*   **Website**: http://www.cs.virginia.edu/stream/ref.html
*   **Original Source**: `stream.c` and `stream.f`

## License

See [LICENSE.txt](LICENSE.txt) and the header in `stream.c` for license information.
GPU results from `stream_gpu.c` must be labelled as "GPU variant of the STREAM benchmark code".
