/*-----------------------------------------------------------------------*/
/* Output Formatting for STREAM Benchmark (JSON to FILE*)                 */
/*                                                                        */
/* Provides JSON output functions for CPU and GPU benchmark results.      */
/* Writes to stdout (headless mode) or any FILE* pointer.                 */
/* Outputs: benchmark metadata, config, results, validation.              */
/*                                                                        */
/* System, memory, and cache info are detected and added by the           */
/* .NET 10 frontend (StreamBench/SystemInfoDetector.cs) after reading     */
/* this JSON output.                                                      */
/*                                                                        */
/* Cross-platform: Windows (MSVC), Linux (GCC), macOS (Clang)            */
/* Requires: stream_hwinfo.h (for HWInfo, hwinfo_json_escape)            */
/*-----------------------------------------------------------------------*/

#ifndef STREAM_OUTPUT_H
#define STREAM_OUTPUT_H

#include <stdio.h>
#include <string.h>

/*-----------------------------------------------------------------------*/
/* DATA STRUCTURES                                                       */
/*-----------------------------------------------------------------------*/

/* Benchmark result data — filled by the benchmark loop */
typedef struct {
    const char *benchmark_type;   /* "CPU" or "GPU" */
    const char *version;          /* e.g., "5.10.03" */
    size_t array_size;            /* elements */
    int bytes_per_element;        /* sizeof(STREAM_TYPE) */
    int ntimes;                   /* iteration count */
    double bytes[4];              /* bytes transferred per kernel */
    double avgtime[4];
    double mintime[4];
    double maxtime[4];
} StreamBenchResult;

/* GPU device info (pass NULL for CPU benchmarks) */
typedef struct {
    char name[256];
    char vendor[256];
    unsigned int compute_units;
    unsigned int max_frequency_mhz;
    double global_memory_bytes;
    size_t max_work_group_size;
} StreamGpuDevice;

/*-----------------------------------------------------------------------*/
/* JSON HELPERS (config and results blocks)                              */
/*-----------------------------------------------------------------------*/

static void stream_output_json_config(FILE *f, const StreamBenchResult *r)
{
    double array_size_mib = (r->bytes_per_element * (double)r->array_size) / (1024.0 * 1024.0);
    double total_memory_mib = (3.0 * r->bytes_per_element * (double)r->array_size) / (1024.0 * 1024.0);

    fprintf(f, "  \"config\": {\n");
    fprintf(f, "    \"array_size_elements\": %zu,\n", r->array_size);
    fprintf(f, "    \"array_size_mib\": %.1f,\n", array_size_mib);
    fprintf(f, "    \"bytes_per_element\": %d,\n", r->bytes_per_element);
    fprintf(f, "    \"total_memory_mib\": %.1f,\n", total_memory_mib);
    fprintf(f, "    \"ntimes\": %d\n", r->ntimes);
    fprintf(f, "  },\n");
}

static void stream_output_json_results(FILE *f, const StreamBenchResult *r)
{
    int j;
    static const char *func_keys[4] = {"copy", "scale", "add", "triad"};

    fprintf(f, "  \"results\": {\n");
    for (j = 0; j < 4; j++) {
        double best_rate = 1.0E-06 * r->bytes[j] / r->mintime[j];
        fprintf(f, "    \"%s\": {\n", func_keys[j]);
        fprintf(f, "      \"best_rate_mbps\": %.1f,\n", best_rate);
        fprintf(f, "      \"avg_time_sec\": %.6f,\n", r->avgtime[j]);
        fprintf(f, "      \"min_time_sec\": %.6f,\n", r->mintime[j]);
        fprintf(f, "      \"max_time_sec\": %.6f\n", r->maxtime[j]);
        fprintf(f, "    }%s\n", (j < 3) ? "," : "");
    }
    fprintf(f, "  },\n");
}

/*-----------------------------------------------------------------------*/
/* JSON OUTPUT — CPU BENCHMARK (writes to any FILE*)                     */
/*-----------------------------------------------------------------------*/

/*
 * Write CPU benchmark JSON to the given FILE* (e.g. stdout or a file).
 * Outputs: benchmark metadata, config, results, validation.
 * The .NET frontend appends system/memory/cache info via SystemInfoDetector.
 * validation_passed: 1 if checkSTREAMresults passed, 0 otherwise.
 */
static void stream_output_cpu_json_fp(FILE *f,
                                       const StreamBenchResult *r,
                                       const HWInfo *hw,
                                       int validation_passed)
{
    fprintf(f, "{\n");
    fprintf(f, "  \"benchmark\": \"STREAM\",\n");
    fprintf(f, "  \"version\": \"%s\",\n", r->version);
    fprintf(f, "  \"type\": \"%s\",\n", r->benchmark_type);
    fprintf(f, "  \"timestamp\": \"%s\",\n", hw->timestamp);

    stream_output_json_config(f, r);
    stream_output_json_results(f, r);

    fprintf(f, "  \"validation\": \"%s\"\n", validation_passed ? "passed" : "failed");
    fprintf(f, "}\n");
    fflush(f);
}

/*-----------------------------------------------------------------------*/
/* JSON OUTPUT — GPU BENCHMARK (writes to any FILE*)                     */
/*-----------------------------------------------------------------------*/

static void stream_output_json_gpu_device(FILE *f, const StreamGpuDevice *dev)
{
    char esc_name[512], esc_vendor[512];
    hwinfo_json_escape(dev->name, esc_name, sizeof(esc_name));
    hwinfo_json_escape(dev->vendor, esc_vendor, sizeof(esc_vendor));

    fprintf(f, "  \"device\": {\n");
    fprintf(f, "    \"name\": \"%s\",\n", esc_name);
    fprintf(f, "    \"vendor\": \"%s\",\n", esc_vendor);
    fprintf(f, "    \"compute_units\": %u,\n", dev->compute_units);
    fprintf(f, "    \"max_frequency_mhz\": %u,\n", dev->max_frequency_mhz);
    fprintf(f, "    \"global_memory_gib\": %.1f,\n",
            dev->global_memory_bytes / (1024.0 * 1024.0 * 1024.0));
    fprintf(f, "    \"max_work_group_size\": %zu\n", dev->max_work_group_size);
    fprintf(f, "  },\n");
}

/*
 * Write GPU benchmark JSON to the given FILE* (e.g. stdout or a file).
 * Outputs: benchmark metadata, GPU device info, config, results, validation.
 * The .NET frontend appends system/memory/cache info via SystemInfoDetector.
 * validation_passed: 1 if results validated, 0 otherwise.
 */
static void stream_output_gpu_json_fp(FILE *f,
                                       const StreamBenchResult *r,
                                       const HWInfo *hw,
                                       const StreamGpuDevice *dev,
                                       int validation_passed)
{
    fprintf(f, "{\n");
    fprintf(f, "  \"benchmark\": \"STREAM\",\n");
    fprintf(f, "  \"version\": \"%s\",\n", r->version);
    fprintf(f, "  \"type\": \"%s\",\n", r->benchmark_type);
    fprintf(f, "  \"timestamp\": \"%s\",\n", hw->timestamp);

    if (dev != NULL)
        stream_output_json_gpu_device(f, dev);
    stream_output_json_config(f, r);
    stream_output_json_results(f, r);

    fprintf(f, "  \"validation\": \"%s\"\n", validation_passed ? "passed" : "failed");
    fprintf(f, "}\n");
    fflush(f);
}

#endif /* STREAM_OUTPUT_H */
