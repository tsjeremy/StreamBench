# STREAM Benchmark for Windows

This is a Windows port of the popular **STREAM** memory bandwidth benchmark, originally developed by John D. McCalpin.

This version is optimized for Windows systems (x64 and ARM64) and includes several enhancements for modern hardware and ease of use.

## Features

*   **Native Windows Support**: Uses `QueryPerformanceCounter` for high-resolution timing and `GetSystemInfo` for automatic core detection.
*   **Multi-Architecture**: Detailed compilation instructions for **x64** (Intel/AMD) and **ARM64** (Snapdragon/Windows on ARM).
*   **CSV Output**: Automatically generates CSV files with results for easy import into Excel or data analysis tools.
*   **Range Testing**: Built-in support for testing a range of array sizes in a single run to find the sweet spot or analyze cache effects.
*   **OpenMP Support**: Easy multi-threading support using Visual Studio's OpenMP implementation.

## Compilation Guide

### Prerequisites
*   **Microsoft Visual Studio** (Community, Professional, or Enterprise) with "Desktop development with C++" workload installed.

### How to Compile

1.  Open the **Developer Command Prompt** for your target architecture from the Start Menu.
    *   For standard 64-bit PCs: **x64 Native Tools Command Prompt for VS**
    *   For Windows on ARM: **ARM64 Native Tools Command Prompt** (or x64_arm64 Cross Tools if compiling on x64).

2.  Navigate to the source code directory.

3.  Run the `cl.exe` command.

#### Basic Compilation
```cmd
cl.exe /O2 /openmp /Fe:stream.exe stream.c
```

#### Optimized Compilation (Recommended)
Enables tuned kernels and sets a larger array size (e.g., 100 million elements) for more stable results.
```cmd
cl.exe /O2 /DTUNED /DSTREAM_ARRAY_SIZE=100000000 /DNTIMES=100 /openmp /Fe:stream_optimized.exe stream.c
```

#### Range Testing Compilation
Compiles a version that tests memory bandwidth across a range of array sizes (e.g., from 50M to 100M elements).
```cmd
cl.exe /O2 /DTUNED /DSTART_SIZE=50000000 /DEND_SIZE=100000000 /DSTEP_SIZE=10000000 /DNTIMES=20 /openmp /Fe:stream_range.exe stream.c
```

### Compiler Options Explained
*   `/O2`: Enable maximum speed optimizations.
*   `/openmp`: Enable multi-threading support.
*   `/Fe:filename.exe`: Specify output filename.
*   `/DSTREAM_ARRAY_SIZE=N`: Set specific array size.
*   `/DNTIMES=N`: Number of iterations (higher is better for stability).
*   `/DTUNED`: Enable optimized kernel functions.

## How to Run

Simply run the executable from the command prompt:
```cmd
stream_optimized.exe
```

The program will:
1.  Detect the number of CPU cores.
2.  Allocate memory.
3.  Run the benchmark (Copy, Scale, Add, Triad).
4.  Print results to the console.
5.  Save results to a CSV file (e.g., `stream_results_100M.csv`).

## Original Project

STREAM is the de facto industry standard benchmark for measuring sustained memory bandwidth.
Original code and documentation by **John D. McCalpin, Ph.D.**

*   **Website**: http://www.cs.virginia.edu/stream/ref.html
*   **Original Source**: `stream.c` and `stream.f`

## License

See [LICENSE.txt](LICENSE.txt) and the header in `stream.c` for license information.
