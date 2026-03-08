/*-----------------------------------------------------------------------*/
/* Program: STREAM (GPU Version)                                         */
/* Revision: $Id: stream_gpu.c,v 5.10.24 2026/03/08 jtsai Exp $         */
/* Original CPU code developed by John D. McCalpin                       */
/* GPU/OpenCL version by Jeremy Tsai                                     */
/*                                                                       */
/* This program measures GPU memory transfer rates in MB/s for simple    */
/* computational kernels using OpenCL.                                   */
/*-----------------------------------------------------------------------*/
/* Based on the STREAM benchmark by John D. McCalpin                     */
/* GPU results must be clearly labelled as:                              */
/*   "GPU variant of the STREAM benchmark code"                          */
/*-----------------------------------------------------------------------*/

/*-----------------------------------------------------------------------*/
/* COMPILATION GUIDE                                                     */
/*-----------------------------------------------------------------------*/
/*
 * This program dynamically loads OpenCL at runtime — NO SDK installation
 * is required. The only prerequisite is that GPU drivers are installed,
 * which provide the OpenCL runtime (OpenCL.dll / libOpenCL.so).
 *
 * ===== Windows (MSVC) =====
 *
 * Prerequisites (one-time, run in PowerShell or Command Prompt):
 *   winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive"
 *
 * 1. Open "x64 Native Tools Command Prompt for VS" from the Start Menu.
 * 2. Navigate to the source directory.
 * 3. Compile:
 *
 *   Basic:
 *     cl.exe /O2 /Fe:stream_gpu.exe stream_gpu.c
 *
 *   With custom array size (200M elements):
 *     cl.exe /O2 /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /Fe:stream_gpu.exe stream_gpu.c
 *
 *   For ARM64 Windows:
 *     Open "ARM64 Native Tools Command Prompt" and use the same commands.
 *
 * ===== Linux (GCC) =====
 *
 *   Prerequisites (Ubuntu/Debian):
 *     sudo apt install build-essential mesa-opencl-icd
 *     # Or for AMD: rocm-opencl-runtime
 *     # Or for NVIDIA: nvidia-opencl-icd
 *
 *   Compile:
 *     gcc -O2 -o stream_gpu stream_gpu.c -ldl -lm
 *
 * ===== macOS (Clang) =====
 *
 *   No extra installation needed — OpenCL ships with macOS.
 *
 *   Compile:
 *     clang -O2 -o stream_gpu stream_gpu.c -lm
 *
 * ===== Troubleshooting =====
 *
 *   "Could not load OpenCL library":
 *     Install or update your GPU drivers. OpenCL runtime is included.
 *
 *   "Failed to build program" (double precision):
 *     Your GPU may not support fp64. The program auto-detects this and
 *     falls back to float. If auto-detection fails, compile with -DGPU_USE_FLOAT:
 *       cl.exe /O2 /DGPU_USE_FLOAT /Fe:stream_gpu.exe stream_gpu.c
 *       gcc -O2 -DGPU_USE_FLOAT -o stream_gpu stream_gpu.c -ldl -lm
 *
 *   Low bandwidth results:
 *     - Ensure you are plugged in (not on battery)
 *     - Close GPU-intensive applications
 *     - Try a larger STREAM_ARRAY_SIZE (e.g., 200000000)
 *
 * ===== Compiler Options =====
 *
 *   /DSTREAM_ARRAY_SIZE=N  : Array size in elements (default: 200000000)
 *   /DNTIMES=N             : Number of timing iterations (default: 100)
 *   /DGPU_USE_FLOAT        : Force float even on GPUs with fp64 (auto-detected)
 *   /DOFFSET=N             : Array alignment offset (default: 0)
 */
/*-----------------------------------------------------------------------*/

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <float.h>
#include <stdint.h>

/*-----------------------------------------------------------------------*/
/* CROSS-PLATFORM HEADERS                                                */
/*-----------------------------------------------------------------------*/

#ifdef _WIN32
    #include <windows.h>
    typedef long long ssize_t;
#else
    #include <sys/time.h>
    #include <time.h>
    #include <dlfcn.h>
#endif

#include "stream_version.h" /* Version from single source of truth */
#include "stream_hwinfo.h"  /* Hardware & system info detection */
#include "stream_output.h"  /* JSON output formatting */

/*-----------------------------------------------------------------------*/
/* CONFIGURATION                                                         */
/*-----------------------------------------------------------------------*/

#ifndef STREAM_ARRAY_SIZE
    #define STREAM_ARRAY_SIZE 200000000
#endif

#ifdef NTIMES
    #if NTIMES <= 1
        #undef NTIMES
        #define NTIMES 100
    #endif
#endif
#ifndef NTIMES
    #define NTIMES 100
#endif

#ifndef OFFSET
    #define OFFSET 0
#endif

#ifndef STREAM_TYPE
    #define STREAM_TYPE double
#endif

/* Use float for GPU if defined */
#ifdef GPU_USE_FLOAT
    #undef STREAM_TYPE
    #define STREAM_TYPE float
    #define OPENCL_TYPE_STR "float"
#else
    #define OPENCL_TYPE_STR "double"
#endif

#ifndef MIN
    #define MIN(x, y) ((x) < (y) ? (x) : (y))
#endif
#ifndef MAX
    #define MAX(x, y) ((x) > (y) ? (x) : (y))
#endif

/*-----------------------------------------------------------------------*/
/* OPENCL TYPE DEFINITIONS (no SDK headers needed)                       */
/*-----------------------------------------------------------------------*/

typedef int32_t  cl_int;
typedef uint32_t cl_uint;
typedef int64_t  cl_long;
typedef uint64_t cl_ulong;
typedef cl_uint  cl_bool;

/* Opaque handle types */
typedef struct _cl_platform_id*   cl_platform_id;
typedef struct _cl_device_id*     cl_device_id;
typedef struct _cl_context*       cl_context;
typedef struct _cl_command_queue*  cl_command_queue;
typedef struct _cl_mem*           cl_mem;
typedef struct _cl_program*       cl_program;
typedef struct _cl_kernel*        cl_kernel;
typedef struct _cl_event*         cl_event;

/* Property types */
typedef cl_ulong cl_mem_flags;
typedef cl_uint  cl_device_info;
typedef cl_uint  cl_platform_info;
typedef cl_uint  cl_program_build_info;
typedef cl_uint  cl_command_queue_properties;
typedef cl_ulong cl_device_type;
typedef cl_uint  cl_profiling_info;
typedef cl_uint  cl_context_info;
typedef intptr_t cl_context_properties;

/* OpenCL constants */
#define CL_SUCCESS                    0
#define CL_DEVICE_TYPE_GPU            (1 << 2)
#define CL_DEVICE_TYPE_ALL            0xFFFFFFFF
#define CL_DEVICE_NAME                0x102B
#define CL_DEVICE_VENDOR              0x102C
#define CL_DEVICE_GLOBAL_MEM_SIZE     0x101F
#define CL_DEVICE_MAX_COMPUTE_UNITS   0x1002
#define CL_DEVICE_MAX_CLOCK_FREQUENCY 0x100C
#define CL_DEVICE_MAX_WORK_GROUP_SIZE 0x1004
#define CL_DEVICE_LOCAL_MEM_SIZE      0x1023
#define CL_DEVICE_EXTENSIONS          0x1030
#define CL_PLATFORM_NAME              0x0902
#define CL_PLATFORM_VERSION           0x0901
#define CL_PLATFORM_VENDOR            0x0903
#define CL_MEM_READ_ONLY              (1 << 2)
#define CL_MEM_WRITE_ONLY             (1 << 1)
#define CL_MEM_READ_WRITE             (1 << 0)
#define CL_MEM_COPY_HOST_PTR          (1 << 5)
#define CL_MEM_ALLOC_HOST_PTR         (1 << 4)
#define CL_MEM_USE_HOST_PTR           (1 << 3)
#define CL_PROGRAM_BUILD_LOG          0x1183
#define CL_QUEUE_PROFILING_ENABLE     (1 << 1)
#define CL_PROFILING_COMMAND_START    0x1282
#define CL_PROFILING_COMMAND_END      0x1283
#define CL_PROFILING_COMMAND_QUEUED   0x1280
#define CL_PROFILING_COMMAND_SUBMIT   0x1281
#define CL_TRUE                       1
#define CL_FALSE                      0

/*-----------------------------------------------------------------------*/
/* OPENCL DYNAMIC LOADING                                                */
/*-----------------------------------------------------------------------*/

#ifdef _WIN32
    #define OCL_LIB_NAME "OpenCL.dll"
    static HMODULE ocl_lib = NULL;
    #define LOAD_OCL() (ocl_lib = LoadLibraryA(OCL_LIB_NAME))
    #define GET_OCL_FUNC(name) GetProcAddress(ocl_lib, name)
    #define CLOSE_OCL() FreeLibrary(ocl_lib)
#elif defined(__APPLE__)
    #define OCL_LIB_NAME "/System/Library/Frameworks/OpenCL.framework/OpenCL"
    static void* ocl_lib = NULL;
    #define LOAD_OCL() (ocl_lib = dlopen(OCL_LIB_NAME, RTLD_NOW))
    #define GET_OCL_FUNC(name) dlsym(ocl_lib, name)
    #define CLOSE_OCL() dlclose(ocl_lib)
#else
    #define OCL_LIB_NAME "libOpenCL.so.1"
    static void* ocl_lib = NULL;
    #define LOAD_OCL() (ocl_lib = dlopen(OCL_LIB_NAME, RTLD_NOW))
    #define GET_OCL_FUNC(name) dlsym(ocl_lib, name)
    #define CLOSE_OCL() dlclose(ocl_lib)
#endif

/* Function pointer type declarations */
typedef cl_int (*pfn_clGetPlatformIDs)(cl_uint, cl_platform_id*, cl_uint*);
typedef cl_int (*pfn_clGetPlatformInfo)(cl_platform_id, cl_platform_info, size_t, void*, size_t*);
typedef cl_int (*pfn_clGetDeviceIDs)(cl_platform_id, cl_device_type, cl_uint, cl_device_id*, cl_uint*);
typedef cl_int (*pfn_clGetDeviceInfo)(cl_device_id, cl_device_info, size_t, void*, size_t*);
typedef cl_context (*pfn_clCreateContext)(const cl_context_properties*, cl_uint, const cl_device_id*,
    void (*)(const char*, const void*, size_t, void*), void*, cl_int*);
typedef cl_command_queue (*pfn_clCreateCommandQueue)(cl_context, cl_device_id, cl_command_queue_properties, cl_int*);
typedef cl_mem (*pfn_clCreateBuffer)(cl_context, cl_mem_flags, size_t, void*, cl_int*);
typedef cl_program (*pfn_clCreateProgramWithSource)(cl_context, cl_uint, const char**, const size_t*, cl_int*);
typedef cl_int (*pfn_clBuildProgram)(cl_program, cl_uint, const cl_device_id*, const char*, void (*)(cl_program, void*), void*);
typedef cl_int (*pfn_clGetProgramBuildInfo)(cl_program, cl_device_id, cl_program_build_info, size_t, void*, size_t*);
typedef cl_kernel (*pfn_clCreateKernel)(cl_program, const char*, cl_int*);
typedef cl_int (*pfn_clSetKernelArg)(cl_kernel, cl_uint, size_t, const void*);
typedef cl_int (*pfn_clEnqueueNDRangeKernel)(cl_command_queue, cl_kernel, cl_uint, const size_t*, const size_t*,
    const size_t*, cl_uint, const cl_event*, cl_event*);
typedef cl_int (*pfn_clEnqueueReadBuffer)(cl_command_queue, cl_mem, cl_bool, size_t, size_t, void*,
    cl_uint, const cl_event*, cl_event*);
typedef cl_int (*pfn_clEnqueueWriteBuffer)(cl_command_queue, cl_mem, cl_bool, size_t, size_t, const void*,
    cl_uint, const cl_event*, cl_event*);
typedef cl_int (*pfn_clFinish)(cl_command_queue);
typedef cl_int (*pfn_clWaitForEvents)(cl_uint, const cl_event*);
typedef cl_int (*pfn_clGetEventProfilingInfo)(cl_event, cl_profiling_info, size_t, void*, size_t*);
typedef cl_int (*pfn_clReleaseEvent)(cl_event);
typedef cl_int (*pfn_clReleaseKernel)(cl_kernel);
typedef cl_int (*pfn_clReleaseProgram)(cl_program);
typedef cl_int (*pfn_clReleaseMemObject)(cl_mem);
typedef cl_int (*pfn_clReleaseCommandQueue)(cl_command_queue);
typedef cl_int (*pfn_clReleaseContext)(cl_context);

/* Global function pointers */
static pfn_clGetPlatformIDs          ocl_GetPlatformIDs;
static pfn_clGetPlatformInfo         ocl_GetPlatformInfo;
static pfn_clGetDeviceIDs            ocl_GetDeviceIDs;
static pfn_clGetDeviceInfo           ocl_GetDeviceInfo;
static pfn_clCreateContext           ocl_CreateContext;
static pfn_clCreateCommandQueue      ocl_CreateCommandQueue;
static pfn_clCreateBuffer            ocl_CreateBuffer;
static pfn_clCreateProgramWithSource ocl_CreateProgramWithSource;
static pfn_clBuildProgram            ocl_BuildProgram;
static pfn_clGetProgramBuildInfo     ocl_GetProgramBuildInfo;
static pfn_clCreateKernel            ocl_CreateKernel;
static pfn_clSetKernelArg            ocl_SetKernelArg;
static pfn_clEnqueueNDRangeKernel    ocl_EnqueueNDRangeKernel;
static pfn_clEnqueueReadBuffer       ocl_EnqueueReadBuffer;
static pfn_clEnqueueWriteBuffer      ocl_EnqueueWriteBuffer;
static pfn_clFinish                  ocl_Finish;
static pfn_clWaitForEvents           ocl_WaitForEvents;
static pfn_clGetEventProfilingInfo   ocl_GetEventProfilingInfo;
static pfn_clReleaseEvent            ocl_ReleaseEvent;
static pfn_clReleaseKernel           ocl_ReleaseKernel;
static pfn_clReleaseProgram          ocl_ReleaseProgram;
static pfn_clReleaseMemObject        ocl_ReleaseMemObject;
static pfn_clReleaseCommandQueue     ocl_ReleaseCommandQueue;
static pfn_clReleaseContext          ocl_ReleaseContext;

static int load_opencl(void)
{
    if (!LOAD_OCL()) {
        fprintf(stderr, "Error: Could not load OpenCL library (%s)\n", OCL_LIB_NAME);
#ifndef _WIN32
        fprintf(stderr, "       %s\n", dlerror());
#endif
        fprintf(stderr, "Make sure GPU drivers are installed.\n");
        return -1;
    }

#define LOAD_FUNC(name) \
    ocl_##name = (pfn_cl##name)GET_OCL_FUNC("cl" #name); \
    if (!ocl_##name) { printf("Error: Could not load cl%s\n", #name); return -1; }

    LOAD_FUNC(GetPlatformIDs);
    LOAD_FUNC(GetPlatformInfo);
    LOAD_FUNC(GetDeviceIDs);
    LOAD_FUNC(GetDeviceInfo);
    LOAD_FUNC(CreateContext);
    LOAD_FUNC(CreateCommandQueue);
    LOAD_FUNC(CreateBuffer);
    LOAD_FUNC(CreateProgramWithSource);
    LOAD_FUNC(BuildProgram);
    LOAD_FUNC(GetProgramBuildInfo);
    LOAD_FUNC(CreateKernel);
    LOAD_FUNC(SetKernelArg);
    LOAD_FUNC(EnqueueNDRangeKernel);
    LOAD_FUNC(EnqueueReadBuffer);
    LOAD_FUNC(EnqueueWriteBuffer);
    LOAD_FUNC(Finish);
    LOAD_FUNC(WaitForEvents);
    LOAD_FUNC(GetEventProfilingInfo);
    LOAD_FUNC(ReleaseEvent);
    LOAD_FUNC(ReleaseKernel);
    LOAD_FUNC(ReleaseProgram);
    LOAD_FUNC(ReleaseMemObject);
    LOAD_FUNC(ReleaseCommandQueue);
    LOAD_FUNC(ReleaseContext);

#undef LOAD_FUNC
    return 0;
}

/*-----------------------------------------------------------------------*/
/* OPENCL KERNEL SOURCE CODE                                             */
/*-----------------------------------------------------------------------*/

/*
 * Vectorized kernels: use float4 (128-bit) for float, double2 (128-bit) for
 * double.  This matches the 128-bit LPDDR5 memory bus width, giving each
 * work-item a full bus-width transaction and significantly improving
 * throughput on bandwidth-bound kernels.
 *
 * The uint index type avoids expensive 64-bit integer arithmetic on GPUs
 * that lack native 64-bit ALUs (like many mobile GPUs).
 */
static const char *kernel_source =
    "#ifdef USE_FP64\n"
    "#pragma OPENCL EXTENSION cl_khr_fp64 : enable\n"
    "typedef double2 SVEC;\n"
    "#else\n"
    "typedef float4 SVEC;\n"
    "#endif\n"
    "\n"
    "__kernel void stream_copy(__global SVEC* restrict c,\n"
    "                          __global const SVEC* restrict a,\n"
    "                          const uint n_vec)\n"
    "{\n"
    "    uint i = get_global_id(0);\n"
    "    if (i < n_vec) c[i] = a[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_scale(__global SVEC* restrict b,\n"
    "                           __global const SVEC* restrict c,\n"
    "                           const STYPE scalar,\n"
    "                           const uint n_vec)\n"
    "{\n"
    "    uint i = get_global_id(0);\n"
    "    if (i < n_vec) b[i] = scalar * c[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_add(__global SVEC* restrict c,\n"
    "                         __global const SVEC* restrict a,\n"
    "                         __global const SVEC* restrict b,\n"
    "                         const uint n_vec)\n"
    "{\n"
    "    uint i = get_global_id(0);\n"
    "    if (i < n_vec) c[i] = a[i] + b[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_triad(__global SVEC* restrict a,\n"
    "                           __global const SVEC* restrict b,\n"
    "                           __global const SVEC* restrict c,\n"
    "                           const STYPE scalar,\n"
    "                           const uint n_vec)\n"
    "{\n"
    "    uint i = get_global_id(0);\n"
    "    if (i < n_vec) a[i] = b[i] + scalar * c[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_init(__global SVEC* restrict a,\n"
    "                          __global SVEC* restrict b,\n"
    "                          __global SVEC* restrict c,\n"
    "                          const STYPE a_val,\n"
    "                          const STYPE b_val,\n"
    "                          const STYPE c_val,\n"
    "                          const uint n_vec)\n"
    "{\n"
    "    uint i = get_global_id(0);\n"
    "    if (i < n_vec) {\n"
    "        a[i] = (SVEC)(a_val);\n"
    "        b[i] = (SVEC)(b_val);\n"
    "        c[i] = (SVEC)(c_val);\n"
    "    }\n"
    "}\n";

/*-----------------------------------------------------------------------*/
/* HELPER: Set a scalar kernel argument (float or double at runtime)     */
/*-----------------------------------------------------------------------*/

static void set_scalar_arg(cl_kernel k, cl_uint idx, int use_float, double val)
{
    if (use_float) {
        float fval = (float)val;
        ocl_SetKernelArg(k, idx, sizeof(float), &fval);
    } else {
        ocl_SetKernelArg(k, idx, sizeof(double), &val);
    }
}

/*-----------------------------------------------------------------------*/
/* HOST TIMER                                                            */
/*-----------------------------------------------------------------------*/

static double mysecond(void)
{
#ifdef _WIN32
    LARGE_INTEGER freq, count;
    if (QueryPerformanceFrequency(&freq)) {
        QueryPerformanceCounter(&count);
        return (double)count.QuadPart / (double)freq.QuadPart;
    }
    return (double)GetTickCount() / 1000.0;
#else
    struct timeval tp;
    gettimeofday(&tp, NULL);
    return (double)tp.tv_sec + (double)tp.tv_usec * 1.e-6;
#endif
}

/*-----------------------------------------------------------------------*/
/* HELPER: Wall-clock time in seconds                                    */
/* Used instead of OpenCL event profiling, which is unreliable on some  */
/* platforms (e.g. macOS ARM64's Metal-backed OpenCL layer).            */
/*-----------------------------------------------------------------------*/

static double wtime(void)
{
#ifdef _WIN32
    LARGE_INTEGER freq, count;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&count);
    return (double)count.QuadPart / (double)freq.QuadPart;
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + 1.0e-9 * (double)ts.tv_nsec;
#endif
}

/*-----------------------------------------------------------------------*/
/* HELPER: Print build log on failure                                    */
/*-----------------------------------------------------------------------*/

static void print_build_log(cl_program program, cl_device_id device)
{
    size_t log_size;
    ocl_GetProgramBuildInfo(program, device, CL_PROGRAM_BUILD_LOG, 0, NULL, &log_size);
    if (log_size > 1) {
        char *log = (char*)malloc(log_size + 1);
        if (log) {
            ocl_GetProgramBuildInfo(program, device, CL_PROGRAM_BUILD_LOG, log_size, log, NULL);
            log[log_size] = '\0';
            printf("Build log:\n%s\n", log);
            free(log);
        }
    }
}

/* Hardware & system info (populated by detect_hardware_info in stream_hwinfo.h) */
static HWInfo hw_info;

/*-----------------------------------------------------------------------*/
/* MAIN                                                                  */
/*-----------------------------------------------------------------------*/

int main(int argc, char **argv)
{
    size_t array_size;
    int i;
    int BytesPerWord = sizeof(STREAM_TYPE);
    int k;
    cl_int err;
    int gpu_use_float = 0;
    size_t gpu_elem_size;
    int vec_size;           /* elements per vector: 4 (float4) or 2 (double2) */
    size_t padded_size;     /* array_size rounded up to vec_size multiple */
    cl_uint n_vec;          /* number of vector elements to process */

    /* Timing arrays */
    double times[4][NTIMES];
    double avgtime[4] = {0}, maxtime[4] = {0};
    double mintime[4] = {FLT_MAX, FLT_MAX, FLT_MAX, FLT_MAX};
    double bytes[4];
    int gpu_validated = 1;

    /* Parse command-line arguments */
    array_size = STREAM_ARRAY_SIZE;
    int gpu_device_index = -1;   /* -1 = auto (first GPU found) */
    int list_gpus_only = 0;
    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--array-size") == 0 && i + 1 < argc) {
            array_size = (size_t)strtoull(argv[++i], NULL, 10);
        }
        else if (strcmp(argv[i], "--gpu-device") == 0 && i + 1 < argc) {
            gpu_device_index = atoi(argv[++i]);
        }
        else if (strcmp(argv[i], "--list-gpus") == 0) {
            list_gpus_only = 1;
        }
    }

    /* Gather all system and hardware information */
    detect_hardware_info(&hw_info);

    /*-------------------------------------------------------------------*/
    /* Load OpenCL                                                       */
    /*-------------------------------------------------------------------*/

    if (load_opencl() != 0) {
        fprintf(stderr, "Failed to load OpenCL. Ensure GPU drivers are installed.\n");
        return 1;
    }
    fprintf(stderr, "OpenCL library loaded successfully.\n");

    /*-------------------------------------------------------------------*/
    /* Platform & Device Selection                                       */
    /*-------------------------------------------------------------------*/

    cl_uint num_platforms;
    err = ocl_GetPlatformIDs(0, NULL, &num_platforms);
    if (err != CL_SUCCESS || num_platforms == 0) {
        fprintf(stderr, "Error: No OpenCL platforms found (error %d)\n", err);
        return 1;
    }

    cl_platform_id *platforms = (cl_platform_id*)malloc(num_platforms * sizeof(cl_platform_id));
    ocl_GetPlatformIDs(num_platforms, platforms, NULL);

    /* Enumerate ALL GPU devices across all platforms */
    #define MAX_GPUS 16
    cl_device_id   all_gpus[MAX_GPUS];
    cl_platform_id all_gpu_plats[MAX_GPUS];
    int            num_gpus = 0;

    for (i = 0; i < (int)num_platforms; i++) {
        char plat_name[256] = {0};
        ocl_GetPlatformInfo(platforms[i], CL_PLATFORM_NAME, sizeof(plat_name), plat_name, NULL);
        fprintf(stderr, "Platform %d: %s\n", i, plat_name);

        cl_uint num_devs = 0;
        cl_device_id devs[MAX_GPUS];
        cl_uint max_devs = (cl_uint)(MAX_GPUS - num_gpus);
        if (max_devs == 0) break;
        err = ocl_GetDeviceIDs(platforms[i], CL_DEVICE_TYPE_GPU, max_devs, devs, &num_devs);
        if (err == CL_SUCCESS && num_devs > 0) {
            int j;
            for (j = 0; j < (int)num_devs && num_gpus < MAX_GPUS; j++) {
                all_gpus[num_gpus] = devs[j];
                all_gpu_plats[num_gpus] = platforms[i];
                num_gpus++;
            }
            fprintf(stderr, "  -> %u GPU device(s) found\n", num_devs);
        }
    }

    /* --list-gpus: output JSON array of all GPUs to stdout, then exit */
    if (list_gpus_only) {
        printf("[");
        for (i = 0; i < num_gpus; i++) {
            char name[256] = {0}, vendor[256] = {0};
            cl_uint cu = 0, freq = 0;
            cl_ulong gmem = 0;
            ocl_GetDeviceInfo(all_gpus[i], CL_DEVICE_NAME, sizeof(name), name, NULL);
            ocl_GetDeviceInfo(all_gpus[i], CL_DEVICE_VENDOR, sizeof(vendor), vendor, NULL);
            ocl_GetDeviceInfo(all_gpus[i], CL_DEVICE_MAX_COMPUTE_UNITS, sizeof(cu), &cu, NULL);
            ocl_GetDeviceInfo(all_gpus[i], CL_DEVICE_MAX_CLOCK_FREQUENCY, sizeof(freq), &freq, NULL);
            ocl_GetDeviceInfo(all_gpus[i], CL_DEVICE_GLOBAL_MEM_SIZE, sizeof(gmem), &gmem, NULL);
            /* Escape any quotes in device name/vendor */
            if (i > 0) printf(",");
            printf("{\"index\":%d,\"name\":\"%s\",\"vendor\":\"%s\","
                   "\"compute_units\":%u,\"max_frequency_mhz\":%u,"
                   "\"global_memory_bytes\":%llu}",
                   i, name, vendor, cu, freq, (unsigned long long)gmem);
        }
        printf("]\n");
        free(platforms);
        CLOSE_OCL();
        return 0;
    }

    /* Select GPU device */
    cl_platform_id chosen_platform = NULL;
    cl_device_id device = NULL;

    if (num_gpus > 0) {
        if (gpu_device_index >= 0) {
            /* User-specified device index */
            if (gpu_device_index < num_gpus) {
                device = all_gpus[gpu_device_index];
                chosen_platform = all_gpu_plats[gpu_device_index];
                fprintf(stderr, "  -> Using GPU device %d (user-selected)\n", gpu_device_index);
            } else {
                fprintf(stderr, "Error: --gpu-device %d out of range (found %d GPU(s), valid: 0-%d)\n",
                        gpu_device_index, num_gpus, num_gpus - 1);
                free(platforms);
                return 1;
            }
        } else {
            /* Auto-select first GPU */
            device = all_gpus[0];
            chosen_platform = all_gpu_plats[0];
            fprintf(stderr, "  -> Auto-selected GPU device 0 of %d\n", num_gpus);
        }
    }

    if (!chosen_platform || !device) {
        /* Fallback: try CL_DEVICE_TYPE_ALL */
        fprintf(stderr, "No dedicated GPU found, trying all device types...\n");
        for (i = 0; i < (int)num_platforms; i++) {
            cl_uint num_devs = 0;
            err = ocl_GetDeviceIDs(platforms[i], CL_DEVICE_TYPE_ALL, 1, &device, &num_devs);
            if (err == CL_SUCCESS && num_devs > 0) {
                chosen_platform = platforms[i];
                break;
            }
        }
    }
    free(platforms);

    if (!device) {
        fprintf(stderr, "Error: No OpenCL device found.\n");
        return 1;
    }

    /* Print device info */
    char dev_name[256] = {0};
    char dev_vendor[256] = {0};
    cl_uint compute_units = 0;
    cl_uint max_freq = 0;
    cl_ulong global_mem = 0;
    size_t max_wg_size = 0;

    ocl_GetDeviceInfo(device, CL_DEVICE_NAME, sizeof(dev_name), dev_name, NULL);
    ocl_GetDeviceInfo(device, CL_DEVICE_VENDOR, sizeof(dev_vendor), dev_vendor, NULL);
    ocl_GetDeviceInfo(device, CL_DEVICE_MAX_COMPUTE_UNITS, sizeof(compute_units), &compute_units, NULL);
    ocl_GetDeviceInfo(device, CL_DEVICE_MAX_CLOCK_FREQUENCY, sizeof(max_freq), &max_freq, NULL);
    ocl_GetDeviceInfo(device, CL_DEVICE_GLOBAL_MEM_SIZE, sizeof(global_mem), &global_mem, NULL);
    ocl_GetDeviceInfo(device, CL_DEVICE_MAX_WORK_GROUP_SIZE, sizeof(max_wg_size), &max_wg_size, NULL);

    fprintf(stderr, "Device: %s (%s)\n", dev_name, dev_vendor);
    fprintf(stderr, "Compute Units: %u, Max Frequency: %u MHz\n", compute_units, max_freq);
    fprintf(stderr, "Global Memory: %.1f MiB (%.1f GiB)\n",
           global_mem / (1024.0 * 1024.0), global_mem / (1024.0 * 1024.0 * 1024.0));
    fprintf(stderr, "Max Work Group Size: %zu\n", max_wg_size);

    /*-------------------------------------------------------------------*/
    /* Check for double precision (fp64) support                         */
    /*-------------------------------------------------------------------*/

    {
        size_t ext_size = 0;
        ocl_GetDeviceInfo(device, CL_DEVICE_EXTENSIONS, 0, NULL, &ext_size);
        if (ext_size > 0) {
            char *extensions = (char*)malloc(ext_size + 1);
            if (extensions) {
                ocl_GetDeviceInfo(device, CL_DEVICE_EXTENSIONS, ext_size, extensions, NULL);
                extensions[ext_size] = '\0';
#ifndef GPU_USE_FLOAT
                if (strstr(extensions, "cl_khr_fp64") == NULL) {
                    fprintf(stderr, "NOTE: GPU does not support double precision (cl_khr_fp64).\n");
                    fprintf(stderr, "      Automatically using single precision (float).\n");
                    gpu_use_float = 1;
                }
#else
                gpu_use_float = 1;
#endif
                free(extensions);
            }
        }
    }
    gpu_elem_size = gpu_use_float ? sizeof(float) : sizeof(STREAM_TYPE);
    if (gpu_use_float) BytesPerWord = (int)sizeof(float);

    /* Vectorization: float4 (128-bit) or double2 (128-bit) per work-item */
    vec_size = gpu_use_float ? 4 : 2;
    padded_size = ((array_size + vec_size - 1) / vec_size) * vec_size;
    n_vec = (cl_uint)(padded_size / vec_size);
    /* Calculate bytes transferred */
    bytes[0] = 2 * gpu_elem_size * (double)array_size; /* Copy */
    bytes[1] = 2 * gpu_elem_size * (double)array_size; /* Scale */
    bytes[2] = 3 * gpu_elem_size * (double)array_size; /* Add */
    bytes[3] = 3 * gpu_elem_size * (double)array_size; /* Triad */

    /*-------------------------------------------------------------------*/
    /* Create OpenCL context, queue, buffers                             */
    /*-------------------------------------------------------------------*/

    cl_context context = ocl_CreateContext(NULL, 1, &device, NULL, NULL, &err);
    if (err != CL_SUCCESS) {
        fprintf(stderr, "Error: Failed to create context (%d)\n", err);
        return 1;
    }

    cl_command_queue queue = ocl_CreateCommandQueue(context, device,
                                                     CL_QUEUE_PROFILING_ENABLE, &err);
    if (err != CL_SUCCESS) {
        fprintf(stderr, "Error: Failed to create command queue (%d)\n", err);
        return 1;
    }

    size_t buf_size = (padded_size + OFFSET) * gpu_elem_size;
    fprintf(stderr, "Allocating GPU buffers: %.1f MiB each (%.1f MiB total)\n",
           buf_size / (1024.0 * 1024.0), 3.0 * buf_size / (1024.0 * 1024.0));

    cl_mem d_a = ocl_CreateBuffer(context, CL_MEM_READ_WRITE, buf_size, NULL, &err);
    if (err != CL_SUCCESS) { fprintf(stderr, "Error: Failed to allocate buffer a (%d)\n", err); return 1; }

    cl_mem d_b = ocl_CreateBuffer(context, CL_MEM_READ_WRITE, buf_size, NULL, &err);
    if (err != CL_SUCCESS) { fprintf(stderr, "Error: Failed to allocate buffer b (%d)\n", err); return 1; }

    cl_mem d_c = ocl_CreateBuffer(context, CL_MEM_READ_WRITE, buf_size, NULL, &err);
    if (err != CL_SUCCESS) { fprintf(stderr, "Error: Failed to allocate buffer c (%d)\n", err); return 1; }

    fprintf(stderr, "GPU buffer allocation successful.\n");

    /*-------------------------------------------------------------------*/
    /* Build OpenCL program                                              */
    /*-------------------------------------------------------------------*/

    /* Build options: define STYPE to match GPU precision */
    char build_opts[256];
    if (gpu_use_float) {
        sprintf(build_opts, "-DSTYPE=float");
    } else {
        sprintf(build_opts, "-DSTYPE=double -DUSE_FP64");
    }

    cl_program program = ocl_CreateProgramWithSource(context, 1, &kernel_source, NULL, &err);
    if (err != CL_SUCCESS) {
        fprintf(stderr, "Error: Failed to create program (%d)\n", err);
        return 1;
    }

    err = ocl_BuildProgram(program, 1, &device, build_opts, NULL, NULL);
    if (err != CL_SUCCESS) {
        fprintf(stderr, "Error: Failed to build program (%d)\n", err);
        print_build_log(program, device);
        return 1;
    }
    fprintf(stderr, "OpenCL kernels compiled successfully.\n");

    /*-------------------------------------------------------------------*/
    /* Create kernels                                                    */
    /*-------------------------------------------------------------------*/

    cl_kernel k_copy  = ocl_CreateKernel(program, "stream_copy", &err);
    cl_kernel k_scale = ocl_CreateKernel(program, "stream_scale", &err);
    cl_kernel k_add   = ocl_CreateKernel(program, "stream_add", &err);
    cl_kernel k_triad = ocl_CreateKernel(program, "stream_triad", &err);
    cl_kernel k_init  = ocl_CreateKernel(program, "stream_init", &err);

    if (!k_copy || !k_scale || !k_add || !k_triad || !k_init) {
        fprintf(stderr, "Error: Failed to create kernels (%d)\n", err);
        return 1;
    }

    /*-------------------------------------------------------------------*/
    /* Set global work size (vectorized: n_vec work items)               */
    /*-------------------------------------------------------------------*/

    /* Round global work size up to a multiple of 256 for the vectorized
     * kernel dispatch.  Pass NULL as local_work_size so the OpenCL
     * driver can choose the optimal work-group size for the device. */
    size_t local_size = 256;
    if (local_size > max_wg_size) local_size = max_wg_size;
    size_t global_size = (((size_t)n_vec + local_size - 1) / local_size) * local_size;

    /*-------------------------------------------------------------------*/
    /* Initialize arrays on GPU                                          */
    /*-------------------------------------------------------------------*/

    {
        ocl_SetKernelArg(k_init, 0, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_init, 1, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_init, 2, sizeof(cl_mem), &d_c);
        set_scalar_arg(k_init, 3, gpu_use_float, 1.0);
        set_scalar_arg(k_init, 4, gpu_use_float, 2.0);
        set_scalar_arg(k_init, 5, gpu_use_float, 0.0);
        ocl_SetKernelArg(k_init, 6, sizeof(cl_uint), &n_vec);
        err = ocl_EnqueueNDRangeKernel(queue, k_init, 1, NULL, &global_size, NULL, 0, NULL, NULL);
        if (err != CL_SUCCESS) { fprintf(stderr, "Error: Init kernel failed (%d)\n", err); return 1; }
        ocl_Finish(queue);
    }

    /* Warm-up: a[j] = 2.0 * a[j] (matches CPU version's timing calibration) */
    {
        ocl_SetKernelArg(k_scale, 0, sizeof(cl_mem), &d_a);  /* output = a */
        ocl_SetKernelArg(k_scale, 1, sizeof(cl_mem), &d_a);  /* input = a */
        set_scalar_arg(k_scale, 2, gpu_use_float, 2.0);
        ocl_SetKernelArg(k_scale, 3, sizeof(cl_uint), &n_vec);
        ocl_EnqueueNDRangeKernel(queue, k_scale, 1, NULL, &global_size, NULL, 0, NULL, NULL);
        ocl_Finish(queue);
    }

    fprintf(stderr, "Running GPU STREAM benchmark...\n");

    /*-------------------------------------------------------------------*/
    /* MAIN LOOP (vectorized: each work-item processes vec_size elems)   */
    /*-------------------------------------------------------------------*/

    for (k = 0; k < NTIMES; k++) {
        double t0;

        /* Copy: c[j] = a[j] */
        ocl_SetKernelArg(k_copy, 0, sizeof(cl_mem), &d_c);
        ocl_SetKernelArg(k_copy, 1, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_copy, 2, sizeof(cl_uint), &n_vec);
        t0 = wtime();
        err = ocl_EnqueueNDRangeKernel(queue, k_copy, 1, NULL, &global_size, NULL, 0, NULL, NULL);
        if (err != CL_SUCCESS) { fprintf(stderr, "Error: Copy kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        ocl_Finish(queue);
        times[0][k] = wtime() - t0;

        /* Scale: b[j] = scalar * c[j] */
        ocl_SetKernelArg(k_scale, 0, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_scale, 1, sizeof(cl_mem), &d_c);
        set_scalar_arg(k_scale, 2, gpu_use_float, 3.0);
        ocl_SetKernelArg(k_scale, 3, sizeof(cl_uint), &n_vec);
        t0 = wtime();
        err = ocl_EnqueueNDRangeKernel(queue, k_scale, 1, NULL, &global_size, NULL, 0, NULL, NULL);
        if (err != CL_SUCCESS) { fprintf(stderr, "Error: Scale kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        ocl_Finish(queue);
        times[1][k] = wtime() - t0;

        /* Add: c[j] = a[j] + b[j] */
        ocl_SetKernelArg(k_add, 0, sizeof(cl_mem), &d_c);
        ocl_SetKernelArg(k_add, 1, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_add, 2, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_add, 3, sizeof(cl_uint), &n_vec);
        t0 = wtime();
        err = ocl_EnqueueNDRangeKernel(queue, k_add, 1, NULL, &global_size, NULL, 0, NULL, NULL);
        if (err != CL_SUCCESS) { fprintf(stderr, "Error: Add kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        ocl_Finish(queue);
        times[2][k] = wtime() - t0;

        /* Triad: a[j] = b[j] + scalar * c[j] */
        ocl_SetKernelArg(k_triad, 0, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_triad, 1, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_triad, 2, sizeof(cl_mem), &d_c);
        set_scalar_arg(k_triad, 3, gpu_use_float, 3.0);
        ocl_SetKernelArg(k_triad, 4, sizeof(cl_uint), &n_vec);
        t0 = wtime();
        err = ocl_EnqueueNDRangeKernel(queue, k_triad, 1, NULL, &global_size, NULL, 0, NULL, NULL);
        if (err != CL_SUCCESS) { fprintf(stderr, "Error: Triad kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        ocl_Finish(queue);
        times[3][k] = wtime() - t0;
    }

    ocl_Finish(queue);

    /*-------------------------------------------------------------------*/
    /* RESULTS                                                           */
    /*-------------------------------------------------------------------*/

    /* Calculate statistics (skip first iteration) */
    for (k = 1; k < NTIMES; k++) {
        int j;
        for (j = 0; j < 4; j++) {
            avgtime[j] += times[j][k];
            mintime[j] = MIN(mintime[j], times[j][k]);
            maxtime[j] = MAX(maxtime[j], times[j][k]);
        }
    }

    /* Compute average times (skip first iteration) */
    {
        int j;
        for (j = 0; j < 4; j++)
            avgtime[j] /= (double)(NTIMES - 1);
    }

    /*-------------------------------------------------------------------*/
    /* VALIDATION - Read back arrays and check                           */
    /*-------------------------------------------------------------------*/

    {
        STREAM_TYPE aj, bj, cj, scalar_v;
        STREAM_TYPE aAvgErr, bAvgErr, cAvgErr;
        STREAM_TYPE aSumErr = 0, bSumErr = 0, cSumErr = 0;
        double epsilon;
        ssize_t j;

        /* Reproduce expected values in the precision used by the GPU */
        if (gpu_use_float && sizeof(STREAM_TYPE) != sizeof(float)) {
            float faj = 1.0f, fbj = 2.0f, fcj = 0.0f;
            float fscalar = 3.0f;
            faj = 2.0f * faj;
            for (k = 0; k < NTIMES; k++) {
                fcj = faj;
                fbj = fscalar * fcj;
                fcj = faj + fbj;
                faj = fbj + fscalar * fcj;
            }
            aj = (STREAM_TYPE)faj;
            bj = (STREAM_TYPE)fbj;
            cj = (STREAM_TYPE)fcj;
        } else {
            aj = 1.0;
            bj = 2.0;
            cj = 0.0;
            aj = 2.0E0 * aj; /* warm-up scaling */
            scalar_v = 3.0;
            for (k = 0; k < NTIMES; k++) {
                cj = aj;
                bj = scalar_v * cj;
                cj = aj + bj;
                aj = bj + scalar_v * cj;
            }
        }

        /* Read back from GPU */
        {
        size_t read_size = array_size * gpu_elem_size;
        void *h_a = malloc(read_size);
        void *h_b = malloc(read_size);
        void *h_c = malloc(read_size);

        if (!h_a || !h_b || !h_c) {
            fprintf(stderr, "Error: Could not allocate host memory for validation\n");
        } else {
            ocl_EnqueueReadBuffer(queue, d_a, CL_TRUE, 0, read_size, h_a, 0, NULL, NULL);
            ocl_EnqueueReadBuffer(queue, d_b, CL_TRUE, 0, read_size, h_b, 0, NULL, NULL);
            ocl_EnqueueReadBuffer(queue, d_c, CL_TRUE, 0, read_size, h_c, 0, NULL, NULL);

            for (j = 0; j < (ssize_t)array_size; j++) {
                STREAM_TYPE va, vb, vc;
                if (gpu_use_float && sizeof(STREAM_TYPE) != sizeof(float)) {
                    va = (STREAM_TYPE)((float*)h_a)[j];
                    vb = (STREAM_TYPE)((float*)h_b)[j];
                    vc = (STREAM_TYPE)((float*)h_c)[j];
                } else {
                    va = ((STREAM_TYPE*)h_a)[j];
                    vb = ((STREAM_TYPE*)h_b)[j];
                    vc = ((STREAM_TYPE*)h_c)[j];
                }
                aSumErr += fabs(va - aj);
                bSumErr += fabs(vb - bj);
                cSumErr += fabs(vc - cj);
            }
            aAvgErr = aSumErr / (STREAM_TYPE)array_size;
            bAvgErr = bSumErr / (STREAM_TYPE)array_size;
            cAvgErr = cSumErr / (STREAM_TYPE)array_size;

            if (gpu_use_float || sizeof(STREAM_TYPE) == 4)
                epsilon = 1.e-6;
            else
                epsilon = 1.e-13;

            int errs = 0;
            if (fabs(aAvgErr / aj) > epsilon) {
                errs++;
                fprintf(stderr, "Failed Validation on array a[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
                fprintf(stderr, "  Expected: %e, AvgAbsErr: %e, AvgRelAbsErr: %e\n", (double)aj, (double)aAvgErr, (double)fabs(aAvgErr/aj));
            }
            if (fabs(bAvgErr / bj) > epsilon) {
                errs++;
                fprintf(stderr, "Failed Validation on array b[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
                fprintf(stderr, "  Expected: %e, AvgAbsErr: %e, AvgRelAbsErr: %e\n", (double)bj, (double)bAvgErr, (double)fabs(bAvgErr/bj));
            }
            if (fabs(cAvgErr / cj) > epsilon) {
                errs++;
                fprintf(stderr, "Failed Validation on array c[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
                fprintf(stderr, "  Expected: %e, AvgAbsErr: %e, AvgRelAbsErr: %e\n", (double)cj, (double)cAvgErr, (double)fabs(cAvgErr/cj));
            }
            if (errs == 0)
                fprintf(stderr, "Solution Validates: avg error less than %e on all three arrays\n", epsilon);
            else
                gpu_validated = 0;

            free(h_a);
            free(h_b);
            free(h_c);
        }
        }
    }

    /*-------------------------------------------------------------------*/
    /* JSON OUTPUT (stdout)                                              */
    /*-------------------------------------------------------------------*/

    {
        StreamBenchResult result;
        StreamGpuDevice gpu_dev;

        result.benchmark_type = "GPU";
        result.version = STREAM_VERSION;
        result.array_size = array_size;
        result.bytes_per_element = (int)gpu_elem_size;
        result.ntimes = NTIMES;
        memcpy(result.bytes, bytes, sizeof(bytes));
        memcpy(result.avgtime, avgtime, sizeof(avgtime));
        memcpy(result.mintime, mintime, sizeof(mintime));
        memcpy(result.maxtime, maxtime, sizeof(maxtime));

        strncpy(gpu_dev.name, dev_name, sizeof(gpu_dev.name) - 1);
        gpu_dev.name[sizeof(gpu_dev.name) - 1] = '\0';
        strncpy(gpu_dev.vendor, dev_vendor, sizeof(gpu_dev.vendor) - 1);
        gpu_dev.vendor[sizeof(gpu_dev.vendor) - 1] = '\0';
        gpu_dev.compute_units = compute_units;
        gpu_dev.max_frequency_mhz = max_freq;
        gpu_dev.global_memory_bytes = (double)global_mem;
        gpu_dev.max_work_group_size = max_wg_size;

        stream_output_gpu_json_fp(stdout, &result, &hw_info, &gpu_dev, gpu_validated);
    }

    /*-------------------------------------------------------------------*/
    /* CLEANUP                                                           */
    /*-------------------------------------------------------------------*/

    ocl_ReleaseKernel(k_copy);
    ocl_ReleaseKernel(k_scale);
    ocl_ReleaseKernel(k_add);
    ocl_ReleaseKernel(k_triad);
    ocl_ReleaseKernel(k_init);
    ocl_ReleaseProgram(program);
    ocl_ReleaseMemObject(d_a);
    ocl_ReleaseMemObject(d_b);
    ocl_ReleaseMemObject(d_c);
    ocl_ReleaseCommandQueue(queue);
    ocl_ReleaseContext(context);
    CLOSE_OCL();

    return 0;
}
