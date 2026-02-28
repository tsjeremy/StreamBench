/*-----------------------------------------------------------------------*/
/* Output Formatting for STREAM Benchmark (CSV & JSON)                    */
/*                                                                        */
/* Centralizes ALL file output (CSV and JSON) for benchmark reporting:    */
/*   - CSV: single-run and range-test consolidated files                  */
/*   - JSON: full system/hardware/config/results with HWInfo integration  */
/*   - GPU JSON includes device section (compute units, VRAM, etc.)       */
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
/* CSV OUTPUT                                                            */
/*-----------------------------------------------------------------------*/

/*
 * Write benchmark results to a CSV file.
 * filename_prefix: e.g., "stream_results" or "stream_gpu_results"
 */
static void stream_output_csv(const char *filename_prefix,
                               const StreamBenchResult *r)
{
    FILE *csvfile;
    char filename[256];
    int j;
    double array_size_mib = (r->bytes_per_element * (double)r->array_size) / (1024.0 * 1024.0);
    double total_memory_gib = (3.0 * r->bytes_per_element * (double)r->array_size) / (1024.0 * 1024.0 * 1024.0);

    sprintf(filename, "%s_%zuM.csv", filename_prefix, r->array_size / 1000000);
    csvfile = fopen(filename, "w");
    if (csvfile == NULL) {
        printf(C_WARN "Warning: Could not create CSV file %s" C_R "\n", filename);
        return;
    }

    fprintf(csvfile, "Array_Size_Elements,Array_Size_MiB,Total_Memory_GiB,Function,Best_Rate_MBps,Avg_Time_sec,Min_Time_sec,Max_Time_sec\n");
    for (j = 0; j < 4; j++) {
        fprintf(csvfile, "%zu,%.1f,%.3f,%s,%.1f,%.6f,%.6f,%.6f\n",
                r->array_size, array_size_mib, total_memory_gib,
                (j == 0) ? "Copy" : (j == 1) ? "Scale" : (j == 2) ? "Add" : "Triad",
                1.0E-06 * r->bytes[j] / r->mintime[j],
                r->avgtime[j], r->mintime[j], r->maxtime[j]);
    }

    fclose(csvfile);
    printf(C_FILE "CSV results written to: %s" C_R "\n", filename);
}

/*-----------------------------------------------------------------------*/
/* RANGE CSV (consolidated file for multi-size testing)                  */
/*-----------------------------------------------------------------------*/

/*
 * Open a consolidated CSV file for range testing and write the header.
 * Returns FILE pointer (caller must track and pass to append/close).
 */
static FILE *stream_output_range_csv_open(const char *filename)
{
    FILE *f = fopen(filename, "w");
    if (f == NULL) {
        printf(C_WARN "Warning: Could not create consolidated CSV file %s" C_R "\n", filename);
        return NULL;
    }
    fprintf(f, "Array_Size_Elements,Array_Size_MiB,Total_Memory_GiB,Function,Best_Rate_MBps,Avg_Time_sec,Min_Time_sec,Max_Time_sec\n");
    printf(C_FILE "Consolidated CSV file created: %s" C_R "\n", filename);
    printf(C_FILE "All range test results will be saved to this single file." C_R "\n");
    return f;
}

/*
 * Append one set of benchmark results to the consolidated CSV file.
 */
static void stream_output_range_csv_append(FILE *f,
                                            const StreamBenchResult *r)
{
    int j;
    double array_size_mib = (r->bytes_per_element * (double)r->array_size) / (1024.0 * 1024.0);
    double total_memory_gib = (3.0 * r->bytes_per_element * (double)r->array_size) / (1024.0 * 1024.0 * 1024.0);

    for (j = 0; j < 4; j++) {
        fprintf(f, "%zu,%.1f,%.3f,%s,%.1f,%.6f,%.6f,%.6f\n",
                r->array_size, array_size_mib, total_memory_gib,
                (j == 0) ? "Copy" : (j == 1) ? "Scale" : (j == 2) ? "Add" : "Triad",
                1.0E-06 * r->bytes[j] / r->mintime[j],
                r->avgtime[j], r->mintime[j], r->maxtime[j]);
    }
    fflush(f);
    printf(C_FILE "Results appended to consolidated CSV file" C_R "\n");
}

/*
 * Close the consolidated CSV file.
 */
static void stream_output_range_csv_close(FILE *f)
{
    if (f != NULL) {
        fclose(f);
        printf(C_FILE "Consolidated CSV file closed successfully" C_R "\n");
    }
}

/*-----------------------------------------------------------------------*/
/* JSON HELPERS (shared system/memory/cache blocks)                      */
/*-----------------------------------------------------------------------*/

/*
 * Write the "system" JSON block (shared between CPU and GPU).
 */
static void stream_output_json_system(FILE *f, const HWInfo *hw)
{
    char esc_hostname[512], esc_os[512], esc_cpu[512];
    hwinfo_json_escape(hw->hostname, esc_hostname, sizeof(esc_hostname));
    hwinfo_json_escape(hw->os_name, esc_os, sizeof(esc_os));
    hwinfo_json_escape(hw->cpu_model, esc_cpu, sizeof(esc_cpu));

    fprintf(f, "  \"system\": {\n");
    fprintf(f, "    \"hostname\": \"%s\",\n", esc_hostname);
    fprintf(f, "    \"os\": \"%s\",\n", esc_os);
    fprintf(f, "    \"architecture\": \"%s\",\n", hw->architecture);
    fprintf(f, "    \"cpu_model\": \"%s\",\n", esc_cpu);
    fprintf(f, "    \"logical_cpus\": %d,\n", hw->num_threads);
    fprintf(f, "    \"cpu_base_mhz\": %d,\n", hw->cpu_base_mhz);
    if (hw->cpu_max_mhz > 0)
        fprintf(f, "    \"cpu_max_mhz\": %d,\n", hw->cpu_max_mhz);
    fprintf(f, "    \"total_ram_gb\": %.1f,\n", hw->total_ram_gb);
    fprintf(f, "    \"numa_nodes\": %d\n", hw->numa_nodes);
    fprintf(f, "  },\n");
}

/*
 * Write the "memory" JSON block (SMBIOS module details).
 */
static void stream_output_json_memory(FILE *f, const HWInfo *hw)
{
    int i;

    fprintf(f, "  \"memory\": {\n");
    if (hw->num_modules > 0) {
        char esc_type[64];
        hwinfo_json_escape(hw->memory_type, esc_type, sizeof(esc_type));
        fprintf(f, "    \"type\": \"%s\",\n", esc_type);
        fprintf(f, "    \"speed_mts\": %d,\n", hw->speed_mts);
        fprintf(f, "    \"configured_speed_mts\": %d,\n", hw->configured_speed_mts);
        fprintf(f, "    \"modules_populated\": %d,\n", hw->num_modules);
        fprintf(f, "    \"total_slots\": %d,\n", hw->total_slots);
        fprintf(f, "    \"modules\": [\n");
        {
            int first = 1;
            for (i = 0; i < hw->total_slots; i++) {
                const HWMemModule *m = &hw->modules[i];
                if (m->size_mb > 0) {
                    char esc_loc[128], esc_mfr[128], esc_pn[128], esc_mt[64], esc_ff[64];
                    hwinfo_json_escape(m->locator, esc_loc, sizeof(esc_loc));
                    hwinfo_json_escape(m->manufacturer, esc_mfr, sizeof(esc_mfr));
                    hwinfo_json_escape(m->part_number, esc_pn, sizeof(esc_pn));
                    hwinfo_json_escape(m->type_str, esc_mt, sizeof(esc_mt));
                    hwinfo_json_escape(m->form_factor_str, esc_ff, sizeof(esc_ff));
                    if (!first) fprintf(f, ",\n");
                    fprintf(f, "      {\n");
                    fprintf(f, "        \"locator\": \"%s\",\n", esc_loc);
                    fprintf(f, "        \"size_mb\": %d,\n", m->size_mb);
                    fprintf(f, "        \"type\": \"%s\",\n", esc_mt);
                    fprintf(f, "        \"form_factor\": \"%s\",\n", esc_ff);
                    fprintf(f, "        \"speed_mts\": %d,\n", m->speed_mts);
                    fprintf(f, "        \"configured_speed_mts\": %d,\n", m->configured_speed_mts);
                    fprintf(f, "        \"data_width_bits\": %d,\n", m->data_width_bits);
                    fprintf(f, "        \"total_width_bits\": %d,\n", m->total_width_bits);
                    fprintf(f, "        \"rank\": %d,\n", m->rank);
                    fprintf(f, "        \"manufacturer\": \"%s\",\n", esc_mfr);
                    fprintf(f, "        \"part_number\": \"%s\"\n", esc_pn);
                    fprintf(f, "      }");
                    first = 0;
                }
            }
            fprintf(f, "\n    ]\n");
        }
    } else {
        fprintf(f, "    \"available\": false\n");
    }
    fprintf(f, "  },\n");
}

/*
 * Write the "cache" JSON block.
 */
static void stream_output_json_cache(FILE *f, const HWInfo *hw)
{
    fprintf(f, "  \"cache\": {\n");
    fprintf(f, "    \"l1d_per_core_kb\": %d,\n", hw->l1d_cache_kb);
    fprintf(f, "    \"l1i_per_core_kb\": %d,\n", hw->l1i_cache_kb);
    fprintf(f, "    \"l2_per_core_kb\": %d,\n", hw->l2_cache_kb);
    fprintf(f, "    \"l3_total_kb\": %d\n", hw->l3_cache_kb);
    fprintf(f, "  },\n");
}

/*
 * Write the "config" JSON block.
 */
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

/*
 * Write the "results" JSON block (benchmark rates and timings).
 */
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
    fprintf(f, "  }\n");
}

/*-----------------------------------------------------------------------*/
/* JSON OUTPUT — CPU BENCHMARK                                           */
/*-----------------------------------------------------------------------*/

/*
 * Write full CPU benchmark JSON file.
 * filename_prefix: e.g., "stream_cpu_results"
 */
static void stream_output_cpu_json(const char *filename_prefix,
                                    const StreamBenchResult *r,
                                    const HWInfo *hw)
{
    FILE *jsonfile;
    char filename[256];

    sprintf(filename, "%s_%zuM.json", filename_prefix, r->array_size / 1000000);
    jsonfile = fopen(filename, "w");
    if (jsonfile == NULL) {
        printf(C_WARN "Warning: Could not create JSON file %s" C_R "\n", filename);
        return;
    }

    fprintf(jsonfile, "{\n");
    fprintf(jsonfile, "  \"benchmark\": \"STREAM\",\n");
    fprintf(jsonfile, "  \"version\": \"%s\",\n", r->version);
    fprintf(jsonfile, "  \"type\": \"%s\",\n", r->benchmark_type);
    fprintf(jsonfile, "  \"timestamp\": \"%s\",\n", hw->timestamp);

    stream_output_json_system(jsonfile, hw);
    stream_output_json_memory(jsonfile, hw);
    stream_output_json_cache(jsonfile, hw);
    stream_output_json_config(jsonfile, r);
    stream_output_json_results(jsonfile, r);

    fprintf(jsonfile, "}\n");
    fclose(jsonfile);
    printf(C_FILE "JSON results written to: %s" C_R "\n", filename);
}

/*-----------------------------------------------------------------------*/
/* JSON OUTPUT — GPU BENCHMARK                                           */
/*-----------------------------------------------------------------------*/

/*
 * Write the "device" JSON block for GPU benchmarks.
 */
static void stream_output_json_gpu_device(FILE *f,
                                           const StreamGpuDevice *dev)
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
 * Write full GPU benchmark JSON file.
 * filename_prefix: e.g., "stream_gpu_results"
 */
static void stream_output_gpu_json(const char *filename_prefix,
                                    const StreamBenchResult *r,
                                    const HWInfo *hw,
                                    const StreamGpuDevice *dev)
{
    FILE *jsonfile;
    char filename[256];

    sprintf(filename, "%s_%zuM.json", filename_prefix, r->array_size / 1000000);
    jsonfile = fopen(filename, "w");
    if (jsonfile == NULL) {
        printf(C_WARN "Warning: Could not create JSON file %s" C_R "\n", filename);
        return;
    }

    fprintf(jsonfile, "{\n");
    fprintf(jsonfile, "  \"benchmark\": \"STREAM\",\n");
    fprintf(jsonfile, "  \"version\": \"%s\",\n", r->version);
    fprintf(jsonfile, "  \"type\": \"%s\",\n", r->benchmark_type);
    fprintf(jsonfile, "  \"timestamp\": \"%s\",\n", hw->timestamp);

    stream_output_json_system(jsonfile, hw);
    stream_output_json_gpu_device(jsonfile, dev);
    stream_output_json_memory(jsonfile, hw);
    stream_output_json_cache(jsonfile, hw);
    stream_output_json_config(jsonfile, r);
    stream_output_json_results(jsonfile, r);

    fprintf(jsonfile, "}\n");
    fclose(jsonfile);
    printf(C_FILE "JSON results written to: %s" C_R "\n", filename);
}

#endif /* STREAM_OUTPUT_H */
