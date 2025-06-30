/*-----------------------------------------------------------------------*/
/* Program: STREAM                                                       */
/* Revision: $Id: stream.c,v 5.10.01 2025/06/29 17:19:00 mccalpin Exp mccalpin $ */
/* Original code developed by John D. McCalpin                           */
/* Programmers: John D. McCalpin                                         */
/*              Joe R. Zagar                                             */
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
/* COMPILATION GUIDE FOR WINDOWS                                         */
/*-----------------------------------------------------------------------*/
/*
 * How to compile with cl.exe (Microsoft Visual C++ Compiler) on Windows:
 *
 * 1. Open the correct Developer Command Prompt from the Start Menu.
 *    This sets up the environment (paths, libraries) for the compiler.
 *
 * 2. To compile for x64/AMD64 (standard 64-bit PCs):
 *    - Open "x64 Native Tools Command Prompt for VS"
 *    
 *    Basic compilation:
 *      cl.exe /O2 /openmp /Fe:stream_x64.exe stream.c
 *    
 *    Optimized compilation with TUNED kernels and larger arrays:
 *      cl.exe /O2 /DTUNED /DSTREAM_ARRAY_SIZE=50000000 /DNTIMES=20 /openmp /Fe:stream_x64_tuned.exe stream.c
 *
 * 3. To compile for ARM64 (for devices like Windows on ARM):
 *    - If compiling ON an ARM64 machine, open "ARM64 Native Tools Command Prompt".
 *    - If cross-compiling FROM an x64 machine, open "x64_arm64 Cross Tools Command Prompt".
 *    
 *    Basic compilation:
 *      cl.exe /O2 /openmp /Fe:stream_arm64.exe stream.c
 *    
 *    Optimized compilation with TUNED kernels and larger arrays:
 *      cl.exe /O2 /DTUNED /DSTREAM_ARRAY_SIZE=50000000 /DNTIMES=20 /openmp /Fe:stream_arm64_tuned.exe stream.c
 *
 * Command-line options explained:
 *   /O2                        : Enable optimizations for speed.
 *   /openmp                    : Enable OpenMP support for multi-threading.
 *   /DTUNED                    : Enable optimized kernel functions.
 *   /DSTREAM_ARRAY_SIZE=N      : Set array size (50M elements = ~1.1GB total memory).
 *   /DNTIMES=N                 : Set number of timing iterations (20 for better statistics).
 *   /Fe:name                   : Set the output executable file name.
 */
/*-----------------------------------------------------------------------*/

#include <stdio.h>
#include <math.h>
#include <float.h>
#include <limits.h>

/*-----------------------------------------------------------------------*/
/* CROSS-PLATFORM COMPATIBILITY HEADERS                                 */
/*-----------------------------------------------------------------------*/
#ifdef _MSC_VER          /* If using Microsoft Visual C++ compiler */
    #include <windows.h> /* Include Windows API header for timers and core count */
    typedef long long ssize_t; /* Define ssize_t for Windows */
#else                    /* For GCC and other compilers (on UNIX-like systems) */
    #include <unistd.h>  /* For POSIX standard functions, e.g., sysconf */
    #include <sys/time.h> /* Include header for gettimeofday() */
#endif

#ifdef _OPENMP     /* If compiling with OpenMP enabled */
    #include <omp.h> /* Include the OpenMP library */
#endif

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
    #define STREAM_ARRAY_SIZE 10000000 /* Default array size is 10 million elements */
#endif

/*
 * 2) STREAM runs each kernel "NTIMES" times and reports the *best* result.
 *    The minimum value for NTIMES is 2. Default is 10.
 */
#ifdef NTIMES
    #if NTIMES <= 1
        #define NTIMES 10
    #endif
#endif
#ifndef NTIMES
    #define NTIMES 10
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

/* Declare the three static global arrays: a, b, and c */
static STREAM_TYPE a[STREAM_ARRAY_SIZE + OFFSET],
                   b[STREAM_ARRAY_SIZE + OFFSET],
                   c[STREAM_ARRAY_SIZE + OFFSET];

/* Declare arrays to store timing results: average, max, and min times */
static double avgtime[4] = {0}, 
              maxtime[4] = {0},
              mintime[4] = {FLT_MAX, FLT_MAX, FLT_MAX, FLT_MAX};

/* Labels for the four tested kernels */
static char *label[4] = {"Copy:      ", "Scale:     ",
                         "Add:       ", "Triad:     "};

/* Bytes transferred per iteration for each of the four kernels */
static double bytes[4] = {
    2 * sizeof(STREAM_TYPE) * STREAM_ARRAY_SIZE, /* Copy: 1 read, 1 write */
    2 * sizeof(STREAM_TYPE) * STREAM_ARRAY_SIZE, /* Scale: 1 read, 1 write */
    3 * sizeof(STREAM_TYPE) * STREAM_ARRAY_SIZE, /* Add: 2 reads, 1 write */
    3 * sizeof(STREAM_TYPE) * STREAM_ARRAY_SIZE  /* Triad: 2 reads, 1 write */
};

/*-----------------------------------------------------------------------*/
/* FUNCTION DECLARATIONS                                                 */
/*-----------------------------------------------------------------------*/

extern double mysecond();
extern void checkSTREAMresults();

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
    printf("STREAM version $Revision: 5.10.01 $\n");
    printf(HLINE);
    BytesPerWord = sizeof(STREAM_TYPE);
    printf("This system uses %d bytes per array element.\n", BytesPerWord);

    printf(HLINE);
    printf("Array size = %llu (elements), Offset = %d (elements)\n", 
           (unsigned long long)STREAM_ARRAY_SIZE, OFFSET);
    printf("Memory per array = %.1f MiB (= %.1f GiB).\n",
           BytesPerWord * ((double)STREAM_ARRAY_SIZE / 1024.0 / 1024.0),
           BytesPerWord * ((double)STREAM_ARRAY_SIZE / 1024.0 / 1024.0 / 1024.0));
    printf("Total memory required = %.1f MiB (= %.1f GiB).\n",
           (3.0 * BytesPerWord) * ((double)STREAM_ARRAY_SIZE / 1024.0 / 1024.),
           (3.0 * BytesPerWord) * ((double)STREAM_ARRAY_SIZE / 1024.0 / 1024. / 1024.));
    printf("Each kernel will be executed %d times.\n", NTIMES);
    printf(" The *best* time for each kernel (excluding the first iteration)\n");
    printf(" will be used to compute the reported bandwidth.\n");

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

    /* Initialize arrays, parallelizing the loop with OpenMP */
#pragma omp parallel for
    for (j = 0; j < STREAM_ARRAY_SIZE; j++) {
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
#pragma omp parallel for
    for (j = 0; j < STREAM_ARRAY_SIZE; j++)
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
#pragma omp parallel for /* Parallelize with OpenMP */
        for (j = 0; j < STREAM_ARRAY_SIZE; j++)
            c[j] = a[j];
#endif
        times[0][k] = mysecond() - times[0][k];

        /* Scale kernel: b[j] = scalar * c[j] */
        times[1][k] = mysecond();
#ifdef TUNED
        tuned_STREAM_Scale(scalar);
#else
#pragma omp parallel for
        for (j = 0; j < STREAM_ARRAY_SIZE; j++)
            b[j] = scalar * c[j];
#endif
        times[1][k] = mysecond() - times[1][k];

        /* Add kernel: c[j] = a[j] + b[j] */
        times[2][k] = mysecond();
#ifdef TUNED
        tuned_STREAM_Add();
#else
#pragma omp parallel for
        for (j = 0; j < STREAM_ARRAY_SIZE; j++)
            c[j] = a[j] + b[j];
#endif
        times[2][k] = mysecond() - times[2][k];

        /* Triad kernel: a[j] = b[j] + scalar * c[j] */
        times[3][k] = mysecond();
#ifdef TUNED
        tuned_STREAM_Triad(scalar);
#else
#pragma omp parallel for
        for (j = 0; j < STREAM_ARRAY_SIZE; j++)
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
    for (j = 0; j < STREAM_ARRAY_SIZE; j++) {
        aSumErr += abs(a[j] - aj);
        bSumErr += abs(b[j] - bj);
        cSumErr += abs(c[j] - cj);
    }
    aAvgErr = aSumErr / (STREAM_TYPE)STREAM_ARRAY_SIZE;
    bAvgErr = bSumErr / (STREAM_TYPE)STREAM_ARRAY_SIZE;
    cAvgErr = cSumErr / (STREAM_TYPE)STREAM_ARRAY_SIZE;

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
#pragma omp parallel for
    for (j = 0; j < STREAM_ARRAY_SIZE; j++)
        c[j] = a[j];
}

void tuned_STREAM_Scale(STREAM_TYPE scalar)
{
    ssize_t j;
#pragma omp parallel for
    for (j = 0; j < STREAM_ARRAY_SIZE; j++)
        b[j] = scalar * c[j];
}

void tuned_STREAM_Add()
{
    ssize_t j;
#pragma omp parallel for
    for (j = 0; j < STREAM_ARRAY_SIZE; j++)
        c[j] = a[j] + b[j];
}

void tuned_STREAM_Triad(STREAM_TYPE scalar)
{
    ssize_t j;
#pragma omp parallel for
    for (j = 0; j < STREAM_ARRAY_SIZE; j++)
        a[j] = b[j] + scalar * c[j];
}

#endif /* TUNED */
