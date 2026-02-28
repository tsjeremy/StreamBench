/*-----------------------------------------------------------------------*/
/* Hardware Info for STREAM Benchmark — minimal C-side detection         */
/*                                                                        */
/* The C backend only captures the benchmark timestamp. All system,       */
/* memory module, and cache detection has moved to the .NET 10 frontend   */
/* (StreamBench/SystemInfoDetector.cs) for simpler cross-platform code.  */
/*                                                                        */
/* Cross-platform: Windows (MSVC), Linux (GCC), macOS (Clang)            */
/*-----------------------------------------------------------------------*/

#ifndef STREAM_HWINFO_H
#define STREAM_HWINFO_H

#include <string.h>
#include <time.h>

/*-----------------------------------------------------------------------*/
/* Data Structure                                                         */
/*-----------------------------------------------------------------------*/

/* Holds only the benchmark timestamp; all other info detected by .NET.  */
typedef struct {
    char timestamp[64];   /* ISO 8601 timestamp of benchmark run          */
} HWInfo;

/*-----------------------------------------------------------------------*/
/* Helper: JSON string escaping (used by stream_output.h for GPU output) */
/*-----------------------------------------------------------------------*/

static void hwinfo_json_escape(const char *src, char *dst, size_t dst_size)
{
    size_t di = 0, si;
    if (!src || !dst || dst_size == 0) return;
    for (si = 0; src[si] && di < dst_size - 1; si++) {
        if (src[si] == '"' || src[si] == '\\') {
            if (di + 2 >= dst_size) break;
            dst[di++] = '\\';
            dst[di++] = src[si];
        } else if ((unsigned char)src[si] < 0x20) {
            /* skip control characters */
        } else {
            dst[di++] = src[si];
        }
    }
    dst[di] = '\0';
}

/*-----------------------------------------------------------------------*/
/* Main Entry Point                                                       */
/*-----------------------------------------------------------------------*/

static void detect_hardware_info(HWInfo *hw)
{
    time_t now = time(NULL);
    struct tm *tm_info = localtime(&now);
    memset(hw, 0, sizeof(HWInfo));
    strftime(hw->timestamp, sizeof(hw->timestamp), "%Y-%m-%dT%H:%M:%S", tm_info);
}

#endif /* STREAM_HWINFO_H */
