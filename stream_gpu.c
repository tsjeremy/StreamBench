/*-----------------------------------------------------------------------*/
/* Program: STREAM (GPU Version)                                         */
/* Revision: $Id: stream_gpu.c,v 5.10.03 2026/02/28 jtsai Exp $         */
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
 *     cl.exe /O2 /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=20 /Fe:stream_gpu.exe stream_gpu.c
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
 *     Your GPU may not support fp64. Compile with -DGPU_USE_FLOAT:
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
 *   /DNTIMES=N             : Number of timing iterations (default: 20)
 *   /DGPU_USE_FLOAT        : Use float instead of double (for GPUs without fp64)
 *   /DOFFSET=N             : Array alignment offset (default: 0)
 */
/*-----------------------------------------------------------------------*/

#include <stdio.h>
#include <stdlib.h>
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
    #include <dlfcn.h>
#endif

#include "stream_hwinfo.h" /* Hardware & system info detection */
#include "stream_output.h" /* CSV & JSON output formatting */

/*-----------------------------------------------------------------------*/
/* CONFIGURATION                                                         */
/*-----------------------------------------------------------------------*/

#ifndef STREAM_ARRAY_SIZE
    #define STREAM_ARRAY_SIZE 200000000
#endif

#ifdef NTIMES
    #if NTIMES <= 1
        #define NTIMES 20
    #endif
#endif
#ifndef NTIMES
    #define NTIMES 20
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

#define HLINE "-------------------------------------------------------------\n"

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
        printf("Error: Could not load OpenCL library (%s)\n", OCL_LIB_NAME);
#ifndef _WIN32
        printf("       %s\n", dlerror());
#endif
        printf("Make sure GPU drivers are installed.\n");
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

static const char *kernel_source =
    "#ifdef USE_FP64\n"
    "#pragma OPENCL EXTENSION cl_khr_fp64 : enable\n"
    "#endif\n"
    "\n"
    "__kernel void stream_copy(__global STYPE* restrict c,\n"
    "                          __global const STYPE* restrict a,\n"
    "                          const ulong n)\n"
    "{\n"
    "    size_t i = get_global_id(0);\n"
    "    if (i < n) c[i] = a[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_scale(__global STYPE* restrict b,\n"
    "                           __global const STYPE* restrict c,\n"
    "                           const STYPE scalar,\n"
    "                           const ulong n)\n"
    "{\n"
    "    size_t i = get_global_id(0);\n"
    "    if (i < n) b[i] = scalar * c[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_add(__global STYPE* restrict c,\n"
    "                         __global const STYPE* restrict a,\n"
    "                         __global const STYPE* restrict b,\n"
    "                         const ulong n)\n"
    "{\n"
    "    size_t i = get_global_id(0);\n"
    "    if (i < n) c[i] = a[i] + b[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_triad(__global STYPE* restrict a,\n"
    "                           __global const STYPE* restrict b,\n"
    "                           __global const STYPE* restrict c,\n"
    "                           const STYPE scalar,\n"
    "                           const ulong n)\n"
    "{\n"
    "    size_t i = get_global_id(0);\n"
    "    if (i < n) a[i] = b[i] + scalar * c[i];\n"
    "}\n"
    "\n"
    "__kernel void stream_init(__global STYPE* restrict a,\n"
    "                          __global STYPE* restrict b,\n"
    "                          __global STYPE* restrict c,\n"
    "                          const STYPE a_val,\n"
    "                          const STYPE b_val,\n"
    "                          const STYPE c_val,\n"
    "                          const ulong n)\n"
    "{\n"
    "    size_t i = get_global_id(0);\n"
    "    if (i < n) { a[i] = a_val; b[i] = b_val; c[i] = c_val; }\n"
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
/* HELPER: Get event elapsed time in seconds                             */
/*-----------------------------------------------------------------------*/

static double event_time_sec(cl_event ev)
{
    cl_ulong t_start, t_end;
    ocl_WaitForEvents(1, &ev);
    ocl_GetEventProfilingInfo(ev, CL_PROFILING_COMMAND_START, sizeof(t_start), &t_start, NULL);
    ocl_GetEventProfilingInfo(ev, CL_PROFILING_COMMAND_END, sizeof(t_end), &t_end, NULL);
    return (double)(t_end - t_start) * 1.0e-9;
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

int main(void)
{
    size_t array_size = STREAM_ARRAY_SIZE;
    int BytesPerWord = sizeof(STREAM_TYPE);
    int k;
    cl_int err;
    int gpu_use_float = 0;
    size_t gpu_elem_size;

    /* Timing arrays */
    double times[4][NTIMES];
    double avgtime[4] = {0}, maxtime[4] = {0};
    double mintime[4] = {FLT_MAX, FLT_MAX, FLT_MAX, FLT_MAX};
    double bytes[4];

    static const char *label[4] = {"Copy:      ", "Scale:     ",
                                    "Add:       ", "Triad:     "};

    printf(HLINE);
    printf("GPU STREAM version 5.10.03 (based on STREAM Revision 5.10.03)\n");
    printf("GPU variant of the STREAM benchmark code\n");
    printf(HLINE);

    /* Gather all system and hardware information */
    detect_hardware_info(&hw_info);
    print_system_info(&hw_info);
    print_hardware_info(&hw_info);

    /*-------------------------------------------------------------------*/
    /* Load OpenCL                                                       */
    /*-------------------------------------------------------------------*/

    if (load_opencl() != 0) {
        printf("Failed to load OpenCL. Ensure GPU drivers are installed.\n");
        return 1;
    }
    printf("OpenCL library loaded successfully.\n");

    /*-------------------------------------------------------------------*/
    /* Platform & Device Selection                                       */
    /*-------------------------------------------------------------------*/

    cl_uint num_platforms;
    err = ocl_GetPlatformIDs(0, NULL, &num_platforms);
    if (err != CL_SUCCESS || num_platforms == 0) {
        printf("Error: No OpenCL platforms found (error %d)\n", err);
        return 1;
    }

    cl_platform_id *platforms = (cl_platform_id*)malloc(num_platforms * sizeof(cl_platform_id));
    ocl_GetPlatformIDs(num_platforms, platforms, NULL);

    /* Find a GPU device, trying each platform */
    cl_platform_id chosen_platform = NULL;
    cl_device_id device = NULL;
    cl_uint i;

    for (i = 0; i < num_platforms; i++) {
        char plat_name[256] = {0};
        ocl_GetPlatformInfo(platforms[i], CL_PLATFORM_NAME, sizeof(plat_name), plat_name, NULL);
        printf("Platform %u: %s\n", i, plat_name);

        cl_uint num_devs = 0;
        err = ocl_GetDeviceIDs(platforms[i], CL_DEVICE_TYPE_GPU, 1, &device, &num_devs);
        if (err == CL_SUCCESS && num_devs > 0) {
            chosen_platform = platforms[i];
            printf("  -> GPU device found on this platform\n");
            break;
        }
    }

    if (!chosen_platform || !device) {
        /* Fallback: try CL_DEVICE_TYPE_ALL */
        printf("No dedicated GPU found, trying all device types...\n");
        for (i = 0; i < num_platforms; i++) {
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
        printf("Error: No OpenCL device found.\n");
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

    printf(HLINE);
    printf("Device: %s (%s)\n", dev_name, dev_vendor);
    printf("Compute Units: %u, Max Frequency: %u MHz\n", compute_units, max_freq);
    printf("Global Memory: %.1f MiB (%.1f GiB)\n",
           global_mem / (1024.0 * 1024.0), global_mem / (1024.0 * 1024.0 * 1024.0));
    printf("Max Work Group Size: %zu\n", max_wg_size);

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
                    printf("\nNOTE: GPU does not support double precision (cl_khr_fp64).\n");
                    printf("      Automatically using single precision (float).\n\n");
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

    /*-------------------------------------------------------------------*/
    /* Print test configuration                                          */
    /*-------------------------------------------------------------------*/

    printf(HLINE);
    printf("This system uses %d bytes per array element.\n", BytesPerWord);
    printf("Array size = %zu (elements), Offset = %d (elements)\n", array_size, OFFSET);
    printf("Memory per array = %.1f MiB (= %.1f GiB).\n",
           BytesPerWord * ((double)array_size / 1024.0 / 1024.0),
           BytesPerWord * ((double)array_size / 1024.0 / 1024.0 / 1024.0));
    printf("Total memory required = %.1f MiB (= %.1f GiB).\n",
           (3.0 * BytesPerWord) * ((double)array_size / 1024.0 / 1024.0),
           (3.0 * BytesPerWord) * ((double)array_size / 1024.0 / 1024.0 / 1024.0));
    printf("Each kernel will be executed %d times.\n", NTIMES);
    printf(" The *best* time for each kernel (excluding the first iteration)\n");
    printf(" will be used to compute the reported bandwidth.\n");

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
        printf("Error: Failed to create context (%d)\n", err);
        return 1;
    }

    cl_command_queue queue = ocl_CreateCommandQueue(context, device,
                                                     CL_QUEUE_PROFILING_ENABLE, &err);
    if (err != CL_SUCCESS) {
        printf("Error: Failed to create command queue (%d)\n", err);
        return 1;
    }

    size_t buf_size = (array_size + OFFSET) * gpu_elem_size;
    printf(HLINE);
    printf("Allocating GPU buffers: %.1f MiB each (%.1f MiB total)\n",
           buf_size / (1024.0 * 1024.0), 3.0 * buf_size / (1024.0 * 1024.0));

    cl_mem d_a = ocl_CreateBuffer(context, CL_MEM_READ_WRITE, buf_size, NULL, &err);
    if (err != CL_SUCCESS) { printf("Error: Failed to allocate buffer a (%d)\n", err); return 1; }

    cl_mem d_b = ocl_CreateBuffer(context, CL_MEM_READ_WRITE, buf_size, NULL, &err);
    if (err != CL_SUCCESS) { printf("Error: Failed to allocate buffer b (%d)\n", err); return 1; }

    cl_mem d_c = ocl_CreateBuffer(context, CL_MEM_READ_WRITE, buf_size, NULL, &err);
    if (err != CL_SUCCESS) { printf("Error: Failed to allocate buffer c (%d)\n", err); return 1; }

    printf("GPU buffer allocation successful.\n");

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
        printf("Error: Failed to create program (%d)\n", err);
        return 1;
    }

    err = ocl_BuildProgram(program, 1, &device, build_opts, NULL, NULL);
    if (err != CL_SUCCESS) {
        printf("Error: Failed to build program (%d)\n", err);
        print_build_log(program, device);
        return 1;
    }
    printf("OpenCL kernels compiled successfully.\n");

    /*-------------------------------------------------------------------*/
    /* Create kernels                                                    */
    /*-------------------------------------------------------------------*/

    cl_kernel k_copy  = ocl_CreateKernel(program, "stream_copy", &err);
    cl_kernel k_scale = ocl_CreateKernel(program, "stream_scale", &err);
    cl_kernel k_add   = ocl_CreateKernel(program, "stream_add", &err);
    cl_kernel k_triad = ocl_CreateKernel(program, "stream_triad", &err);
    cl_kernel k_init  = ocl_CreateKernel(program, "stream_init", &err);

    if (!k_copy || !k_scale || !k_add || !k_triad || !k_init) {
        printf("Error: Failed to create kernels (%d)\n", err);
        return 1;
    }

    /*-------------------------------------------------------------------*/
    /* Set global work size                                              */
    /*-------------------------------------------------------------------*/

    /* Round up to multiple of preferred work group size */
    size_t local_size = 256;
    if (local_size > max_wg_size) local_size = max_wg_size;
    size_t global_size = ((array_size + local_size - 1) / local_size) * local_size;

    printf("Global work size: %zu, Local work size: %zu\n", global_size, local_size);

    /*-------------------------------------------------------------------*/
    /* Initialize arrays on GPU                                          */
    /*-------------------------------------------------------------------*/

    {
        cl_ulong n = (cl_ulong)array_size;
        ocl_SetKernelArg(k_init, 0, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_init, 1, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_init, 2, sizeof(cl_mem), &d_c);
        set_scalar_arg(k_init, 3, gpu_use_float, 1.0);
        set_scalar_arg(k_init, 4, gpu_use_float, 2.0);
        set_scalar_arg(k_init, 5, gpu_use_float, 0.0);
        ocl_SetKernelArg(k_init, 6, sizeof(cl_ulong), &n);
        err = ocl_EnqueueNDRangeKernel(queue, k_init, 1, NULL, &global_size, &local_size, 0, NULL, NULL);
        if (err != CL_SUCCESS) { printf("Error: Init kernel failed (%d)\n", err); return 1; }
        ocl_Finish(queue);
    }

    /* Warm-up: a[j] = 2.0 * a[j] (matches CPU version's timing calibration) */
    {
        cl_ulong n = (cl_ulong)array_size;
        ocl_SetKernelArg(k_scale, 0, sizeof(cl_mem), &d_a);  /* output = a */
        ocl_SetKernelArg(k_scale, 1, sizeof(cl_mem), &d_a);  /* input = a */
        set_scalar_arg(k_scale, 2, gpu_use_float, 2.0);
        ocl_SetKernelArg(k_scale, 3, sizeof(cl_ulong), &n);
        ocl_EnqueueNDRangeKernel(queue, k_scale, 1, NULL, &global_size, &local_size, 0, NULL, NULL);
        ocl_Finish(queue);
    }

    printf(HLINE);
    printf("Running GPU STREAM benchmark...\n");
    printf(HLINE);

    /*-------------------------------------------------------------------*/
    /* MAIN LOOP                                                         */
    /*-------------------------------------------------------------------*/

    cl_ulong n = (cl_ulong)array_size;

    for (k = 0; k < NTIMES; k++) {
        cl_event ev;

        /* Copy: c[j] = a[j] */
        ocl_SetKernelArg(k_copy, 0, sizeof(cl_mem), &d_c);
        ocl_SetKernelArg(k_copy, 1, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_copy, 2, sizeof(cl_ulong), &n);
        err = ocl_EnqueueNDRangeKernel(queue, k_copy, 1, NULL, &global_size, &local_size, 0, NULL, &ev);
        if (err != CL_SUCCESS) { printf("Error: Copy kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        times[0][k] = event_time_sec(ev);
        ocl_ReleaseEvent(ev);

        /* Scale: b[j] = scalar * c[j] */
        ocl_SetKernelArg(k_scale, 0, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_scale, 1, sizeof(cl_mem), &d_c);
        set_scalar_arg(k_scale, 2, gpu_use_float, 3.0);
        ocl_SetKernelArg(k_scale, 3, sizeof(cl_ulong), &n);
        err = ocl_EnqueueNDRangeKernel(queue, k_scale, 1, NULL, &global_size, &local_size, 0, NULL, &ev);
        if (err != CL_SUCCESS) { printf("Error: Scale kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        times[1][k] = event_time_sec(ev);
        ocl_ReleaseEvent(ev);

        /* Add: c[j] = a[j] + b[j] */
        ocl_SetKernelArg(k_add, 0, sizeof(cl_mem), &d_c);
        ocl_SetKernelArg(k_add, 1, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_add, 2, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_add, 3, sizeof(cl_ulong), &n);
        err = ocl_EnqueueNDRangeKernel(queue, k_add, 1, NULL, &global_size, &local_size, 0, NULL, &ev);
        if (err != CL_SUCCESS) { printf("Error: Add kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        times[2][k] = event_time_sec(ev);
        ocl_ReleaseEvent(ev);

        /* Triad: a[j] = b[j] + scalar * c[j] */
        ocl_SetKernelArg(k_triad, 0, sizeof(cl_mem), &d_a);
        ocl_SetKernelArg(k_triad, 1, sizeof(cl_mem), &d_b);
        ocl_SetKernelArg(k_triad, 2, sizeof(cl_mem), &d_c);
        set_scalar_arg(k_triad, 3, gpu_use_float, 3.0);
        ocl_SetKernelArg(k_triad, 4, sizeof(cl_ulong), &n);
        err = ocl_EnqueueNDRangeKernel(queue, k_triad, 1, NULL, &global_size, &local_size, 0, NULL, &ev);
        if (err != CL_SUCCESS) { printf("Error: Triad kernel launch failed (%d) at iter %d\n", err, k); return 1; }
        times[3][k] = event_time_sec(ev);
        ocl_ReleaseEvent(ev);
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

    printf("Function    Best Rate MB/s  Avg time     Min time     Max time\n");
    {
        int j;
        for (j = 0; j < 4; j++) {
            avgtime[j] /= (double)(NTIMES - 1);
            printf("%s%12.1f  %11.6f  %11.6f  %11.6f\n", label[j],
                   1.0E-06 * bytes[j] / mintime[j],
                   avgtime[j], mintime[j], maxtime[j]);
        }
    }
    printf(HLINE);

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
            printf("Error: Could not allocate host memory for validation\n");
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
                printf("Failed Validation on array a[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
                printf("  Expected: %e, AvgAbsErr: %e, AvgRelAbsErr: %e\n", (double)aj, (double)aAvgErr, (double)fabs(aAvgErr/aj));
            }
            if (fabs(bAvgErr / bj) > epsilon) {
                errs++;
                printf("Failed Validation on array b[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
                printf("  Expected: %e, AvgAbsErr: %e, AvgRelAbsErr: %e\n", (double)bj, (double)bAvgErr, (double)fabs(bAvgErr/bj));
            }
            if (fabs(cAvgErr / cj) > epsilon) {
                errs++;
                printf("Failed Validation on array c[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
                printf("  Expected: %e, AvgAbsErr: %e, AvgRelAbsErr: %e\n", (double)cj, (double)cAvgErr, (double)fabs(cAvgErr/cj));
            }
            if (errs == 0)
                printf("Solution Validates: avg error less than %e on all three arrays\n", epsilon);

            free(h_a);
            free(h_b);
            free(h_c);
        }
        }
    }
    printf(HLINE);

    /*-------------------------------------------------------------------*/
    /* FILE OUTPUT (CSV & JSON)                                          */
    /*-------------------------------------------------------------------*/

    {
        StreamBenchResult result;
        StreamGpuDevice gpu_dev;

        result.benchmark_type = "GPU";
        result.version = "5.10.03";
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

        stream_output_csv("stream_gpu_results", &result);
        stream_output_gpu_json("stream_gpu_results", &result, &hw_info, &gpu_dev);
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
