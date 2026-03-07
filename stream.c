/*-----------------------------------------------------------------------*/
/* Program: STREAM                                                       */
/* Revision: $Id: stream.c,v 5.10.20 2026/03/07 jtsai Exp $             */
/* Original code developed by John D. McCalpin                           */
/* Programmers: John D. McCalpin                                         */
/*              Joe R. Zagar                                             */
/*              Jeremy Tsai (Windows/cross-platform/GPU enhancements)    */
/*                                                                       */
/* This program measures memory transfer rates in MB/s for simple        */
/* computational kernels coded in C.                                     */
/*-----------------------------------------------------------------------*/
/* Copyright 1991-2013: John D. McCalpin                                 */
/*-----------------------------------------------------------------------*/
/* License:                                                              */
/*  1. You are free to use this program and/or to redistribute           */
/*     this program.                                                     */
/*  2. You are free to modify this program for your own use,             */
/*     including commercial use, subject to the publication              */
/*     restrictions in item 3.                                           */
/*  3. You are free to publish results obtained from running this        */
/*     program, or from works that you derive from this program,         */
/*     with the following limitations:                                   */
/*    3a. In order to be referred to as "STREAM benchmark results",      */
/*        published results must be in conformance to the STREAM         */
/*        Run Rules, (briefly reviewed below) published at               */
/*        http://www.cs.virginia.edu/stream/ref.html                     */
/*        and incorporated herein by reference.                          */
/*        As the copyright holder, John McCalpin retains the             */
/*        right to determine conformity with the Run Rules.              */
/*    3b. Results based on modified source code or on runs not in        */
/*        accordance with the STREAM Run Rules must be clearly           */
/*        labelled whenever they are published.  Examples of             */
/*        proper labelling include:                                      */
/*          "tuned STREAM benchmark results"                             */
/*          "based on a variant of the STREAM benchmark code"            */
/*        Other comparable, clear, and reasonable labelling is           */
/*        acceptable.                                                    */
/*    3c. Submission of results to the STREAM benchmark web site         */
/*        is encouraged, but not required.                               */
/*  4. Use of this program or creation of derived works based on this    */
/*     program constitutes acceptance of these licensing restrictions.   */
/*  5. Absolutely no warranty is expressed or implied.                   */
/*-----------------------------------------------------------------------*/

/*-----------------------------------------------------------------------*/
/* COMPILATION GUIDE                                                     */
/*-----------------------------------------------------------------------*/
/*
 * This is the CPU version of the STREAM benchmark. For GPU memory
 * bandwidth testing, see stream_gpu.c (OpenCL, no SDK needed).
 *
 * ===== Windows (cl.exe) =====
 *
 * Prerequisites (one-time, run in PowerShell or Command Prompt):
 *   winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive"
 *
 * 1. Open the correct Developer Command Prompt from the Start Menu.
 *    This sets up the environment (paths, libraries) for the compiler.
 *
 * 2. To compile for x64/AMD64 (standard 64-bit PCs):
 *    - Open "x64 Native Tools Command Prompt for VS"
 *    
 *    Basic compilation:
 *      cl.exe /O2 /openmp /Fe:stream.exe stream.c
 *    
 *    Optimized compilation with TUNED kernels and larger arrays:
 *      cl.exe /O2 /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /openmp /Fe:stream.exe stream.c
 *
 * 3. To compile for ARM64 (for devices like Windows on ARM):
 *    - If compiling ON an ARM64 machine, open "ARM64 Native Tools Command Prompt".
 *    - If cross-compiling FROM an x64 machine, open "x64_arm64 Cross Tools Command Prompt".
 *    
 *    Basic compilation:
 *      cl.exe /O2 /openmp /Fe:stream_arm64.exe stream.c
 *    
 *    Optimized compilation with TUNED kernels and larger arrays:
 *      cl.exe /O2 /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /openmp /Fe:stream_arm64.exe stream.c
 *
 * ===== Linux (gcc) =====
 *
 *    gcc -O2 -fopenmp -o stream stream.c
 *    gcc -O2 -fopenmp -DTUNED -DSTREAM_ARRAY_SIZE=200000000 -o stream stream.c
 *
 * ===== macOS (clang) =====
 *
 *    brew install libomp   # One-time setup for OpenMP support
 *    clang -O2 -Xpreprocessor -fopenmp -lomp -o stream stream.c
 *
 * ===== Compiler Options =====
 *
 *   /O2 or -O2             : Enable optimizations for speed.
 *   /openmp or -fopenmp    : Enable OpenMP support for multi-threading.
 *   /DTUNED or -DTUNED     : Enable optimized kernel functions.
 *   /DSTREAM_ARRAY_SIZE=N  : Set array size (200M elements = ~4.5GB total memory).
 *   /DNTIMES=N             : Set number of timing iterations (100 for better statistics).
 *   /Fe:name or -o name    : Set the output executable file name.
 *
 * Note: Range testing (sweeping multiple array sizes) is handled by the
 *       .NET 10 frontend: dotnet run --project StreamBench -- --cpu --range 50M:200M:50M
 */
/*-----------------------------------------------------------------------*/

#include <stdio.h>
#include <math.h>
#include <float.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

/*-----------------------------------------------------------------------*/
/* CROSS-PLATFORM COMPATIBILITY HEADERS                                 */
/*-----------------------------------------------------------------------*/
#ifdef _MSC_VER          /* If using Microsoft Visual C++ compiler */
    #include <windows.h> /* Include Windows API header for timers and core count */
    typedef long long ssize_t; /* Define ssize_t for Windows */
#else                    /* For GCC and other compilers (on UNIX-like systems) */
    #include <sys/time.h> /* Include header for gettimeofday() */
#endif

#ifdef _OPENMP     /* If compiling with OpenMP enabled */
    #include <omp.h> /* Include the OpenMP library */
#endif

#include "stream_version.h" /* Version from single source of truth */
#include "stream_hwinfo.h"  /* System & hardware info detection */
#include "stream_output.h"  /* JSON output to stdout */

/*-----------------------------------------------------------------------*/
/* CONFIGURATION PARAMETERS                                              */
/*-----------------------------------------------------------------------*/

/*
 * INSTRUCTIONS:
 *
 * 1) Adjust the value of 'STREAM_ARRAY_SIZE' to meet *both* of the
 *    following criteria:
 *    (a) Each array must be at least 4 times the size of the
 *        available cache memory.
 *    (b) The size should be large enough so that the 'timing calibration'
 *        output by the program is at least 20 clock-ticks.
 *
 *    This can be set on the compile line, e.g.,
 *    gcc -O -DSTREAM_ARRAY_SIZE=100000000 stream.c -o stream.100M
 */
#ifndef STREAM_ARRAY_SIZE
    #define STREAM_ARRAY_SIZE 100000000 /* Default array size is 100 million elements */
#endif

/*
 * 2) STREAM runs each kernel "NTIMES" times and reports the *best* result.
 *    The minimum value for NTIMES is 2. Default is 100.
 */
#ifdef NTIMES
    #if NTIMES <= 1
        #undef NTIMES
        #define NTIMES 100
    #endif
#endif
#ifndef NTIMES
    #define NTIMES 100
#endif

/*
 * OFFSET can be used to alter the relative memory alignment of the arrays.
 */
#ifndef OFFSET
    #define OFFSET 0
#endif

/*
 * Define the primary data type for array elements. Default is double.
 */
#ifndef STREAM_TYPE
    #define STREAM_TYPE double
#endif

/*
 * Array size can also be overridden at runtime via --array-size N argument.
 */

/*-----------------------------------------------------------------------*/
/* CONSTANTS AND MACROS                                                  */
/*-----------------------------------------------------------------------*/

/* Define MIN and MAX macros for convenience */
#ifndef MIN
    #define MIN(x, y) ((x) < (y) ? (x) : (y))
#endif
#ifndef MAX
    #define MAX(x, y) ((x) > (y) ? (x) : (y))
#endif

#ifndef abs
    #define abs(a) ((a) >= 0 ? (a) : -(a))
#endif


/*-----------------------------------------------------------------------*/
/* GLOBAL VARIABLES                                                      */
/*-----------------------------------------------------------------------*/

/* Declare pointers for the three dynamic arrays: a, b, and c */
static STREAM_TYPE *a, *b, *c;

/* Current array size being tested (for range testing) */
static size_t current_array_size = STREAM_ARRAY_SIZE;

/* Declare arrays to store timing results: average, max, and min times */
static double avgtime[4] = {0}, 
              maxtime[4] = {0},
              mintime[4] = {FLT_MAX, FLT_MAX, FLT_MAX, FLT_MAX};

/* Bytes transferred per iteration for each of the four kernels */
static double bytes[4];  /* Calculated dynamically based on current array size */

/* Hardware & system info (populated by detect_hardware_info in stream_hwinfo.h) */
static HWInfo hw_info;

/*-----------------------------------------------------------------------*/
/* FUNCTION DECLARATIONS                                                 */
/*-----------------------------------------------------------------------*/

extern double mysecond();
extern int checkSTREAMresults(); /* returns 0=pass, non-zero=fail */
extern int run_stream_test(size_t array_size);

#ifdef TUNED
extern void tuned_STREAM_Copy();
extern void tuned_STREAM_Scale(STREAM_TYPE scalar);
extern void tuned_STREAM_Add();
extern void tuned_STREAM_Triad(STREAM_TYPE scalar);
#endif

/*-----------------------------------------------------------------------*/
/* MAIN FUNCTION                                                         */
/*-----------------------------------------------------------------------*/
int main(int argc, char **argv)
{
    size_t array_size = STREAM_ARRAY_SIZE;
    int i;

    /* Parse --array-size N from command line */
    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--array-size") == 0 && i + 1 < argc) {
            array_size = (size_t)strtoull(argv[++i], NULL, 10);
        }
    }

    /* Gather all system and hardware information */
    detect_hardware_info(&hw_info);

    return run_stream_test(array_size);
}

/*-----------------------------------------------------------------------*/
/* STREAM TEST FUNCTION                                                  */
/*-----------------------------------------------------------------------*/
int run_stream_test(size_t array_size)
{
    current_array_size = array_size;
    
    /* Reset timing arrays for each test */
    int i;
    for (i = 0; i < 4; i++) {
        avgtime[i] = 0.0;
        maxtime[i] = 0.0;
        mintime[i] = FLT_MAX;
    }
    
    /* Update bytes array for current array size */
    bytes[0] = 2 * sizeof(STREAM_TYPE) * current_array_size; /* Copy: 1 read, 1 write */
    bytes[1] = 2 * sizeof(STREAM_TYPE) * current_array_size; /* Scale: 1 read, 1 write */
    bytes[2] = 3 * sizeof(STREAM_TYPE) * current_array_size; /* Add: 2 reads, 1 write */
    bytes[3] = 3 * sizeof(STREAM_TYPE) * current_array_size; /* Triad: 2 reads, 1 write */
    int k;
    ssize_t j;
    STREAM_TYPE scalar;
    double times[4][NTIMES];

    /* Allocate cache-line aligned memory for best bandwidth.
     * 64-byte alignment matches the typical ARM64/x64 cache line size. */
#ifdef _MSC_VER
    a = (STREAM_TYPE*) _aligned_malloc((current_array_size + OFFSET) * sizeof(STREAM_TYPE), 64);
    b = (STREAM_TYPE*) _aligned_malloc((current_array_size + OFFSET) * sizeof(STREAM_TYPE), 64);
    c = (STREAM_TYPE*) _aligned_malloc((current_array_size + OFFSET) * sizeof(STREAM_TYPE), 64);
#else
    posix_memalign((void**)&a, 64, (current_array_size + OFFSET) * sizeof(STREAM_TYPE));
    posix_memalign((void**)&b, 64, (current_array_size + OFFSET) * sizeof(STREAM_TYPE));
    posix_memalign((void**)&c, 64, (current_array_size + OFFSET) * sizeof(STREAM_TYPE));
#endif

    if (a == NULL || b == NULL || c == NULL) {
        fprintf(stderr, "Error: Failed to allocate memory (%.1f MB per array)\n",
                (current_array_size + OFFSET) * sizeof(STREAM_TYPE) / (1024.0 * 1024.0));
#ifdef _MSC_VER
        if (a != NULL) { _aligned_free(a); a = NULL; }
        if (b != NULL) { _aligned_free(b); b = NULL; }
        if (c != NULL) { _aligned_free(c); c = NULL; }
#else
        if (a != NULL) { free(a); a = NULL; }
        if (b != NULL) { free(b); b = NULL; }
        if (c != NULL) { free(c); c = NULL; }
#endif
        return 1;
    }

    /* Initialize arrays with schedule(static) for sequential access and prefetch */
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++) {
        a[j] = 1.0;
        b[j] = 2.0;
        c[j] = 0.0;
    }

    /* Warm-up pass */
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++)
        a[j] = 2.0E0 * a[j];

    /*-------------------------------------------------------------------*/
    /* MAIN LOOP - repeat test cases NTIMES times                       */
    /*-------------------------------------------------------------------*/

    scalar = 3.0; /* Set the scalar value for Scale and Triad operations */
    for (k = 0; k < NTIMES; k++) {
        
        /* Copy kernel: c[j] = a[j] */
        times[0][k] = mysecond();
#ifdef TUNED /* If TUNED is defined, call the tuned version */
        tuned_STREAM_Copy();
#else
#pragma omp parallel for schedule(static) /* Parallelize with OpenMP */
        for (j = 0; j < current_array_size; j++)
            c[j] = a[j];
#endif
        times[0][k] = mysecond() - times[0][k];

        /* Scale kernel: b[j] = scalar * c[j] */
        times[1][k] = mysecond();
#ifdef TUNED
        tuned_STREAM_Scale(scalar);
#else
#pragma omp parallel for schedule(static)
        for (j = 0; j < current_array_size; j++)
            b[j] = scalar * c[j];
#endif
        times[1][k] = mysecond() - times[1][k];

        /* Add kernel: c[j] = a[j] + b[j] */
        times[2][k] = mysecond();
#ifdef TUNED
        tuned_STREAM_Add();
#else
#pragma omp parallel for schedule(static)
        for (j = 0; j < current_array_size; j++)
            c[j] = a[j] + b[j];
#endif
        times[2][k] = mysecond() - times[2][k];

        /* Triad kernel: a[j] = b[j] + scalar * c[j] */
        times[3][k] = mysecond();
#ifdef TUNED
        tuned_STREAM_Triad(scalar);
#else
#pragma omp parallel for schedule(static)
        for (j = 0; j < current_array_size; j++)
            a[j] = b[j] + scalar * c[j];
#endif
        times[3][k] = mysecond() - times[3][k];
    }

    /*-------------------------------------------------------------------*/
    /* SUMMARY — calculate avg/min/max, validate, output JSON to stdout */
    /*-------------------------------------------------------------------*/

    /* Calculate avg, min, max times (skip first iteration k=0) */
    for (k = 1; k < NTIMES; k++) {
        for (j = 0; j < 4; j++) {
            avgtime[j] = avgtime[j] + times[j][k];
            mintime[j] = MIN(mintime[j], times[j][k]);
            maxtime[j] = MAX(maxtime[j], times[j][k]);
        }
    }
    for (j = 0; j < 4; j++)
        avgtime[j] = avgtime[j] / (double)(NTIMES - 1);

    /* Validate results and output JSON to stdout */
    {
        int validated = (checkSTREAMresults() == 0) ? 1 : 0;
        StreamBenchResult result;
        result.benchmark_type = "CPU";
        result.version = STREAM_VERSION;
        result.array_size = current_array_size;
        result.bytes_per_element = (int)sizeof(STREAM_TYPE);
        result.ntimes = NTIMES;
        memcpy(result.bytes, bytes, sizeof(bytes));
        memcpy(result.avgtime, avgtime, sizeof(avgtime));
        memcpy(result.mintime, mintime, sizeof(mintime));
        memcpy(result.maxtime, maxtime, sizeof(maxtime));
        stream_output_cpu_json_fp(stdout, &result, &hw_info, validated);
    }

    /* Free allocated memory */
#ifdef _MSC_VER
    _aligned_free(a);
    _aligned_free(b);
    _aligned_free(c);
#else
    free(a);
    free(b);
    free(c);
#endif

    return 0;
}

/*-----------------------------------------------------------------------*/
/* UTILITY FUNCTIONS                                                     */
/*-----------------------------------------------------------------------*/

/*
 * A cross-platform timer function that provides wall-clock time.
 */
double mysecond()
{
#ifdef _MSC_VER /* For Microsoft Visual C++ */
    LARGE_INTEGER freq, count;
    
    /* Query the frequency of the performance counter */
    if (QueryPerformanceFrequency(&freq)) {
        /* Query the current value of the performance counter */
        QueryPerformanceCounter(&count);
        /* Return the time in seconds */
        return (double)count.QuadPart / (double)freq.QuadPart;
    }
    /* Fallback to GetTickCount if high-resolution timer is not available */
    return (double)GetTickCount() / 1000.0;
    
#else /* For GCC and other compilers on UNIX/Linux */
    struct timeval tp;
    struct timezone tzp;
    int i;
    
    /* Get the time using gettimeofday() */
    i = gettimeofday(&tp, &tzp);
    /* Convert seconds and microseconds to a single double value and return */
    return ((double)tp.tv_sec + (double)tp.tv_usec * 1.e-6);
#endif
}

/*
 * Checks if the STREAM results are valid.
 * Returns 0 if all arrays pass validation, non-zero otherwise.
 */
int checkSTREAMresults()
{
    STREAM_TYPE aj, bj, cj, scalar;
    STREAM_TYPE aSumErr, bSumErr, cSumErr;
    STREAM_TYPE aAvgErr, bAvgErr, cAvgErr;
    double epsilon;
    ssize_t j;
    int k, err;

    /* Reproduce the initialization */
    aj = 1.0;
    bj = 2.0;
    cj = 0.0;

    /* a[] is modified during timing check */
    aj = 2.0E0 * aj;

    /* Now execute the same computations as in the timing loop */
    scalar = 3.0;
    for (k = 0; k < NTIMES; k++) {
        cj = aj;
        bj = scalar * cj;
        cj = aj + bj;
        aj = bj + scalar * cj;
    }

    /* Accumulate the errors between observed and expected results */
    aSumErr = 0.0;
    bSumErr = 0.0;
    cSumErr = 0.0;
    for (j = 0; j < current_array_size; j++) {
        aSumErr += abs(a[j] - aj);
        bSumErr += abs(b[j] - bj);
        cSumErr += abs(c[j] - cj);
    }
    aAvgErr = aSumErr / (STREAM_TYPE)current_array_size;
    bAvgErr = bSumErr / (STREAM_TYPE)current_array_size;
    cAvgErr = cSumErr / (STREAM_TYPE)current_array_size;

    /* Set the acceptable error tolerance (epsilon) */
    if (sizeof(STREAM_TYPE) == 4) {
        epsilon = 1.e-6;
    } else if (sizeof(STREAM_TYPE) == 8) {
        epsilon = 1.e-13;
    } else {
        fprintf(stderr, "WEIRD: sizeof(STREAM_TYPE) = %zu\n", sizeof(STREAM_TYPE));
        epsilon = 1.e-6;
    }

    err = 0;
    if (abs(aAvgErr / aj) > epsilon) {
        err++;
        fprintf(stderr, "Failed Validation on array a[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
    }
    if (abs(bAvgErr / bj) > epsilon) {
        err++;
        fprintf(stderr, "Failed Validation on array b[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
    }
    if (abs(cAvgErr / cj) > epsilon) {
        err++;
        fprintf(stderr, "Failed Validation on array c[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
    }

    return err;
}

/*-----------------------------------------------------------------------*/
/* TUNED KERNEL FUNCTIONS                                                */
/*-----------------------------------------------------------------------*/

#ifdef TUNED
/*
 * Stub functions for "tuned" versions of the kernels.
 * Users can replace these with versions optimized for specific hardware.
 */

void tuned_STREAM_Copy()
{
    ssize_t j;
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++)
        c[j] = a[j];
}

void tuned_STREAM_Scale(STREAM_TYPE scalar)
{
    ssize_t j;
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++)
        b[j] = scalar * c[j];
}

void tuned_STREAM_Add()
{
    ssize_t j;
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++)
        c[j] = a[j] + b[j];
}

void tuned_STREAM_Triad(STREAM_TYPE scalar)
{
    ssize_t j;
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++)
        a[j] = b[j] + scalar * c[j];
}

#endif /* TUNED */
