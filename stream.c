/*-----------------------------------------------------------------------*/
/* Program: STREAM                                                       */
/* Revision: $Id: stream.c,v 5.10.03 2026/02/28 jtsai Exp $             */
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
 *   /DSTART_SIZE=N         : Start array size for range testing (e.g., 50000000).
 *   /DEND_SIZE=N           : End array size for range testing (e.g., 200000000).
 *   /DSTEP_SIZE=N          : Step size for range testing (e.g., 50000000).
 *   /Fe:name or -o name    : Set the output executable file name.
 *
 * ===== Range Testing Examples =====
 *
 *   Test from 50M to 200M elements in 50M steps:
 *     cl.exe /O2 /DTUNED /DSTART_SIZE=50000000 /DEND_SIZE=200000000 /DSTEP_SIZE=50000000 /DNTIMES=20 /openmp /Fe:stream_range.exe stream.c
 *     gcc -O2 -fopenmp -DTUNED -DSTART_SIZE=50000000 -DEND_SIZE=200000000 -DSTEP_SIZE=50000000 -DNTIMES=20 -o stream_range stream.c
 */
/*-----------------------------------------------------------------------*/

#include <stdio.h>
#include <math.h>
#include <float.h>
#include <limits.h>
#include <stdlib.h>

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

#include "stream_hwinfo.h" /* System & hardware info detection */
#include "stream_output.h" /* CSV & JSON output formatting */

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
 * Array size range testing parameters
 * Define START_SIZE, END_SIZE, and STEP_SIZE to test multiple array sizes
 */
#ifndef START_SIZE
    #define START_SIZE 0  /* If 0, use single STREAM_ARRAY_SIZE test */
#endif
#ifndef END_SIZE
    #define END_SIZE 0
#endif
#ifndef STEP_SIZE
    #define STEP_SIZE 10000000  /* Default step is 10M elements */
#endif

/*-----------------------------------------------------------------------*/
/* CONSTANTS AND MACROS                                                  */
/*-----------------------------------------------------------------------*/

#define HLINE "-------------------------------------------------------------\n"

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

#define M 20 /* Number of samples to take in checktick() */

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

/* Labels for the four tested kernels */
static char *label[4] = {"Copy:      ", "Scale:     ",
                         "Add:       ", "Triad:     "};

/* Bytes transferred per iteration for each of the four kernels */
static double bytes[4];  /* Will be calculated dynamically based on current array size */

/* Global CSV file pointer for range testing */
static FILE *range_csv_file = NULL;

/* Hardware & system info (populated by detect_hardware_info in stream_hwinfo.h) */
static HWInfo hw_info;

/*-----------------------------------------------------------------------*/
/* FUNCTION DECLARATIONS                                                 */
/*-----------------------------------------------------------------------*/

extern double mysecond();
extern void checkSTREAMresults();
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
int main()
{
    /* Gather all system and hardware information */
    detect_hardware_info(&hw_info);

    /* Check if range testing is enabled */
    if (START_SIZE > 0 && END_SIZE > START_SIZE) {
        printf("STREAM Range Testing Mode\n");
        printf("Testing array sizes from %zu to %zu with step %zu\n", 
               (size_t)START_SIZE, (size_t)END_SIZE, (size_t)STEP_SIZE);
        printf("========================================================\n");
        
        /* Initialize consolidated CSV file for range testing */
        {
            char range_filename[256];
            sprintf(range_filename, "stream_range_results_%zuM_to_%zuM_step_%zuM.csv",
                    (size_t)START_SIZE / 1000000,
                    (size_t)END_SIZE / 1000000,
                    (size_t)STEP_SIZE / 1000000);
            range_csv_file = stream_output_range_csv_open(range_filename);
        }
        
        size_t array_size;
        int test_count = 0;
        int successful_tests = 0;
        
        for (array_size = START_SIZE; array_size <= END_SIZE; array_size += STEP_SIZE) {
            test_count++;
            printf("\n--- Test %d: Array size %zu (%.1f M elements) ---\n", 
                   test_count, array_size, array_size / 1000000.0);
            
            if (run_stream_test(array_size) == 0) {
                successful_tests++;
            } else {
                printf("Test failed for array size %zu\n", array_size);
            }
        }
        
        /* Close consolidated CSV file */
        stream_output_range_csv_close(range_csv_file);
        range_csv_file = NULL;
        
        printf("\n========================================================\n");
        printf("Range testing complete: %d/%d tests successful\n", successful_tests, test_count);
        
        if (successful_tests == test_count) {
            printf("All tests completed successfully!\n");
            printf("Results saved in consolidated CSV file.\n");
        } else {
            printf("Some tests failed due to memory allocation issues.\n");
            printf("Try using smaller array sizes or ensure more memory is available.\n");
        }
        
        return 0;
    } else {
        /* Single test mode */
        return run_stream_test(STREAM_ARRAY_SIZE);
    }
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
    int quantum, checktick();
    int BytesPerWord;
    int k;
    ssize_t j; /* Use ssize_t to support large array indices on 64-bit systems */
    STREAM_TYPE scalar;
    double t, times[4][NTIMES];

    /*-------------------------------------------------------------------*/
    /* SETUP - determine precision and check timing                     */
    /*-------------------------------------------------------------------*/

    printf(HLINE);
    printf("STREAM version $Revision: 5.10.03 $\n");
    printf(HLINE);

    /* Print system information for comparison */
    print_system_info(&hw_info);
    print_hardware_info(&hw_info);
    printf(HLINE);
    BytesPerWord = sizeof(STREAM_TYPE);
    printf("This system uses %d bytes per array element.\n", BytesPerWord);

    printf(HLINE);
    printf("Array size = %zu (elements), Offset = %d (elements)\n", 
           current_array_size, OFFSET);
    printf("Memory per array = %.1f MiB (= %.1f GiB).\n",
           BytesPerWord * ((double)current_array_size / 1024.0 / 1024.0),
           BytesPerWord * ((double)current_array_size / 1024.0 / 1024.0 / 1024.0));
    printf("Total memory required = %.1f MiB (= %.1f GiB).\n",
           (3.0 * BytesPerWord) * ((double)current_array_size / 1024.0 / 1024.),
           (3.0 * BytesPerWord) * ((double)current_array_size / 1024.0 / 1024. / 1024.));
    printf("Each kernel will be executed %d times.\n", NTIMES);
    printf(" The *best* time for each kernel (excluding the first iteration)\n");
    printf(" will be used to compute the reported bandwidth.\n");
    
    /* Allocate cache-line aligned memory for best bandwidth.
     * 64-byte alignment matches the typical ARM64/x64 cache line size,
     * ensuring arrays start on cache-line boundaries to avoid false
     * sharing between threads and improve hardware prefetcher efficiency. */
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
        printf("Error: Failed to allocate memory for arrays\n");
        printf("Requested memory: %.1f MB per array (%.1f MB total)\n", 
               (current_array_size + OFFSET) * sizeof(STREAM_TYPE) / (1024.0 * 1024.0),
               3.0 * (current_array_size + OFFSET) * sizeof(STREAM_TYPE) / (1024.0 * 1024.0));
        
        /* Free any successfully allocated arrays before returning */
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
    printf("Memory allocation successful: %.1f MB per array (%.1f MB total)\n",
           (current_array_size + OFFSET) * sizeof(STREAM_TYPE) / (1024.0 * 1024.0),
           3.0 * (current_array_size + OFFSET) * sizeof(STREAM_TYPE) / (1024.0 * 1024.0));

#ifdef _OPENMP
    printf(HLINE);
    
    /* Dynamic thread count detection and configuration */
    int num_threads = 0;
#ifdef _MSC_VER /* For Windows */
    SYSTEM_INFO sysInfo;
    GetSystemInfo(&sysInfo);                    /* Get system information using Windows API */
    num_threads = sysInfo.dwNumberOfProcessors; /* Get the number of processors */
#else           /* For Linux/UNIX */
    num_threads = sysconf(_SC_NPROCESSORS_ONLN); /* Get the number of online processors using POSIX standard */
#endif
    if (num_threads > 0) {
        omp_set_num_threads(num_threads); /* Set the number of threads for OpenMP */
        printf("Number of threads automatically set to %d (number of available cores)\n", num_threads);
    }

#pragma omp parallel /* Start a parallel region */
    {
#pragma omp master /* This block will only be executed by the master thread */
        {
            k = omp_get_num_threads(); /* Get the number of threads requested by the runtime */
            printf("Number of Threads requested = %i\n", k);
        }
    }
#endif

#ifdef _OPENMP
    k = 0;
#pragma omp parallel /* Start a parallel region */
#pragma omp atomic   /* Use an atomic operation to safely increment the counter */
    k++;
    printf("Number of Threads counted = %i\n", k); /* Print the number of threads that actually participated */
#endif

    /* Initialize arrays, parallelizing the loop with OpenMP.
     * schedule(static) guarantees each thread gets a contiguous, equal-sized
     * chunk — this maximises sequential memory access and hardware prefetch
     * effectiveness for bandwidth-bound kernels. */
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++) {
        a[j] = 1.0;
        b[j] = 2.0;
        c[j] = 0.0;
    }

    printf(HLINE);

    /* Check the granularity of the system clock */
    if ((quantum = checktick()) >= 1) {
        printf("Your clock granularity/precision appears to be "
               "%d microseconds.\n", quantum);
    } else {
        printf("Your clock granularity appears to be "
               "less than one microsecond.\n");
        quantum = 1;
    }

    /* Perform a sample computation to estimate the test duration */
    t = mysecond();
#pragma omp parallel for schedule(static)
    for (j = 0; j < current_array_size; j++)
        a[j] = 2.0E0 * a[j];
    t = 1.0E6 * (mysecond() - t);

    printf("Each test below will take on the order"
           " of %d microseconds.\n", (int)t);
    printf("   (= %d clock ticks)\n", (int)(t / quantum));
    printf("Increase the size of the arrays if this shows that\n");
    printf("you are not getting at least 20 clock ticks per test.\n");
    printf(HLINE);

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
    /* SUMMARY - Calculate and display results                          */
    /*-------------------------------------------------------------------*/

    /* Calculate avg, min, and max times, skipping the first iteration (k=0) */
    for (k = 1; k < NTIMES; k++) {
        for (j = 0; j < 4; j++) {
            avgtime[j] = avgtime[j] + times[j][k];
            mintime[j] = MIN(mintime[j], times[j][k]);
            maxtime[j] = MAX(maxtime[j], times[j][k]);
        }
    }

    /* Print the results table */
    printf("Function    Best Rate MB/s  Avg time     Min time     Max time\n");
    for (j = 0; j < 4; j++) {
        avgtime[j] = avgtime[j] / (double)(NTIMES - 1);

        /* The best rate is calculated from the minimum time */
        printf("%s%12.1f  %11.6f  %11.6f  %11.6f\n", label[j],
               1.0E-06 * bytes[j] / mintime[j],
               avgtime[j],
               mintime[j],
               maxtime[j]);
    }
    printf(HLINE);

    /* Check Results */
    checkSTREAMresults();
    printf(HLINE);

    /* Build result struct for output */
    {
        StreamBenchResult result;
        result.benchmark_type = "CPU";
        result.version = "5.10.03";
        result.array_size = current_array_size;
        result.bytes_per_element = (int)sizeof(STREAM_TYPE);
        result.ntimes = NTIMES;
        memcpy(result.bytes, bytes, sizeof(bytes));
        memcpy(result.avgtime, avgtime, sizeof(avgtime));
        memcpy(result.mintime, mintime, sizeof(mintime));
        memcpy(result.maxtime, maxtime, sizeof(maxtime));

        /* CSV output */
        if (range_csv_file != NULL) {
            stream_output_range_csv_append(range_csv_file, &result);
        } else {
            stream_output_csv("stream_results", &result);
            stream_output_cpu_json("stream_cpu_results", &result, &hw_info);
        }
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
 * Checks the resolution (granularity) of the system clock.
 */
int checktick()
{
    int i, minDelta, Delta;
    double t1, t2, timesfound[M];

    /* Collect a sequence of M unique time values from the system */
    for (i = 0; i < M; i++) {
        t1 = mysecond();
        while (((t2 = mysecond()) - t1) < 1.0E-6) /* Ensure we get a new time value */
            ;
        timesfound[i] = t1 = t2;
    }

    /* Determine the minimum difference between these M values */
    minDelta = 1000000;
    for (i = 1; i < M; i++) {
        Delta = (int)(1.0E6 * (timesfound[i] - timesfound[i - 1]));
        minDelta = MIN(minDelta, MAX(Delta, 0));
    }

    return (minDelta); /* Return the minimum difference in microseconds */
}

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
 */
void checkSTREAMresults()
{
    STREAM_TYPE aj, bj, cj, scalar;
    STREAM_TYPE aSumErr, bSumErr, cSumErr;
    STREAM_TYPE aAvgErr, bAvgErr, cAvgErr;
    double epsilon;
    ssize_t j;
    int k, ierr, err;

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
    if (sizeof(STREAM_TYPE) == 4) {        /* single-precision */
        epsilon = 1.e-6;
    } else if (sizeof(STREAM_TYPE) == 8) { /* double-precision */
        epsilon = 1.e-13;
    } else {
        printf("WEIRD: sizeof(STREAM_TYPE) = %zu\n", sizeof(STREAM_TYPE));
        epsilon = 1.e-6;
    }

    err = 0;
    /* Check if the average relative error for array 'a' is within tolerance */
    if (abs(aAvgErr / aj) > epsilon) {
        err++;
        printf("Failed Validation on array a[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
    }
    /* Check if the average relative error for array 'b' is within tolerance */
    if (abs(bAvgErr / bj) > epsilon) {
        err++;
        printf("Failed Validation on array b[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
    }
    /* Check if the average relative error for array 'c' is within tolerance */
    if (abs(cAvgErr / cj) > epsilon) {
        err++;
        printf("Failed Validation on array c[], AvgRelAbsErr > epsilon (%e)\n", epsilon);
    }

    if (err == 0) {
        printf("Solution Validates: avg error less than %e on all three arrays\n", epsilon);
    }
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
