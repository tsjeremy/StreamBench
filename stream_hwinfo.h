/*-----------------------------------------------------------------------*/
/* Hardware & System Information Detection for STREAM Benchmark           */
/*                                                                        */
/* Centralizes ALL system and hardware detection for benchmark reporting:  */
/*   - System: hostname, OS, architecture, CPU model, threads, RAM        */
/*   - Memory: SMBIOS module details (type, speed, slots, manufacturer)   */
/*   - Cache: L1/L2/L3 hierarchy                                         */
/*   - CPU frequency and NUMA topology                                    */
/*   - Utilities: json_escape, print helpers                              */
/*                                                                        */
/* Cross-platform: Windows (MSVC), Linux (GCC), macOS (Clang)            */
/* Include after platform headers. Both stream.c and stream_gpu.c use it. */
/*-----------------------------------------------------------------------*/

#ifndef STREAM_HWINFO_H
#define STREAM_HWINFO_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <time.h>

#ifdef _MSC_VER
    #ifndef _WINDOWS_
        #include <windows.h>
    #endif
#else
    #include <unistd.h>
    #ifdef __linux__
        #include <sys/sysinfo.h>
    #endif
    #ifdef __APPLE__
        #include <sys/sysctl.h>
    #endif
#endif

#ifndef HLINE
#define HLINE "-------------------------------------------------------------\n"
#endif

#define HWINFO_MAX_MODULES 64

/*-----------------------------------------------------------------------*/
/* Data Structures                                                        */
/*-----------------------------------------------------------------------*/

typedef struct {
    int    size_mb;
    int    speed_mts;               /* rated speed in MT/s */
    int    configured_speed_mts;    /* actual running speed in MT/s */
    int    data_width_bits;         /* 64=non-ECC, 72=ECC */
    int    total_width_bits;
    int    rank;
    char   type_str[32];            /* DDR4, DDR5, LPDDR5X, etc. */
    char   locator[64];             /* DIMM slot label */
    char   bank[64];
    char   manufacturer[64];
    char   part_number[64];
    char   form_factor_str[32];     /* DIMM, SO-DIMM, etc. */
} HWMemModule;

typedef struct {
    /* System identification */
    char   hostname[256];
    char   os_name[256];
    char   architecture[64];
    char   cpu_model[256];
    int    num_threads;                 /* logical CPUs */
    double total_ram_gb;
    char   timestamp[64];               /* ISO 8601 */

    /* Memory modules (from SMBIOS Type 17) */
    int         num_modules;            /* populated slots */
    int         total_slots;            /* all slots including empty */
    HWMemModule modules[HWINFO_MAX_MODULES];

    /* Aggregated memory info */
    char   memory_type[32];
    int    speed_mts;                   /* max rated speed across modules */
    int    configured_speed_mts;        /* max configured speed */

    /* Cache hierarchy */
    int    l1d_cache_kb;                /* per core */
    int    l1i_cache_kb;                /* per core */
    int    l2_cache_kb;                 /* per core */
    int    l3_cache_kb;                 /* total */

    /* CPU frequency */
    int    cpu_base_mhz;
    int    cpu_max_mhz;

    /* NUMA */
    int    numa_nodes;
} HWInfo;

/*-----------------------------------------------------------------------*/
/* Helper Functions                                                       */
/*-----------------------------------------------------------------------*/

/* Escape a string for JSON output (handles backslash, quote, control chars) */
static void hwinfo_json_escape(const char *src, char *dst, size_t dst_size)
{
    size_t di = 0;
    size_t si;
    if (!src || !dst || dst_size == 0) return;
    for (si = 0; src[si] && di < dst_size - 1; si++) {
        if (src[si] == '"' || src[si] == '\\') {
            if (di + 2 >= dst_size) break;
            dst[di++] = '\\';
            dst[di++] = src[si];
        } else if ((unsigned char)src[si] < 0x20) {
            /* Skip control characters */
        } else {
            dst[di++] = src[si];
        }
    }
    dst[di] = '\0';
}

/* Safe little-endian reads (handles unaligned access on ARM) */
static unsigned short hw_read16(const unsigned char *p)
{
    return (unsigned short)p[0] | ((unsigned short)p[1] << 8);
}

static unsigned int hw_read32(const unsigned char *p)
{
    return (unsigned int)p[0] | ((unsigned int)p[1] << 8) |
           ((unsigned int)p[2] << 16) | ((unsigned int)p[3] << 24);
}

static const char* hwinfo_mem_type_str(int type)
{
    switch (type) {
        case 0x18: return "DDR3";
        case 0x1A: return "DDR4";
        case 0x1B: return "LPDDR";
        case 0x1C: return "LPDDR2";
        case 0x1D: return "LPDDR3";
        case 0x1E: return "LPDDR4";
        case 0x20: return "HBM";
        case 0x21: return "HBM2";
        case 0x22: return "DDR5";
        case 0x23: return "LPDDR5";
        case 0x24: return "HBM3";
        case 0x25: return "LPDDR5X";
        default:   return "Unknown";
    }
}

static const char* hwinfo_form_factor_str(int ff)
{
    switch (ff) {
        case 0x09: return "DIMM";
        case 0x0B: return "Row of chips";
        case 0x0D: return "SO-DIMM";
        case 0x0F: return "FB-DIMM";
        case 0x10: return "Die";
        default:   return "Other";
    }
}

static void hwinfo_safe_strcpy(char *dst, const char *src, size_t dst_size)
{
    if (!dst || !src || dst_size == 0) return;
    strncpy(dst, src, dst_size - 1);
    dst[dst_size - 1] = '\0';
}

static void hwinfo_trim(char *s)
{
    size_t len;
    if (!s) return;
    len = strlen(s);
    while (len > 0 && (s[len-1] == ' ' || s[len-1] == '\t' ||
           s[len-1] == '\r' || s[len-1] == '\n'))
        s[--len] = '\0';
}

/*-----------------------------------------------------------------------*/
/* SMBIOS Parsing (platform-independent core)                            */
/*-----------------------------------------------------------------------*/

#if defined(_MSC_VER) || defined(__linux__)

/* Extract a string from SMBIOS structure by 1-based index */
static const char* smbios_get_str(const unsigned char *hdr,
                                   unsigned char struct_len, int idx)
{
    const unsigned char *p;
    int i;
    if (idx == 0) return "";
    p = hdr + struct_len;
    for (i = 1; i < idx; i++) {
        while (*p) p++;
        p++;
        if (*p == 0) return "";
    }
    return (const char*)p;
}

/* Parse raw SMBIOS table data and extract Type 17 (Memory Device) entries */
static void parse_smbios_table(HWInfo *hw, const unsigned char *data,
                                size_t data_len)
{
    const unsigned char *end = data + data_len;
    const unsigned char *ptr = data;

    hw->total_slots = 0;
    hw->num_modules = 0;

    while (ptr + 4 <= end) {
        unsigned char type = ptr[0];
        unsigned char length = ptr[1];
        const unsigned char *next;

        if (length < 4) break;
        if (ptr + length > end) break;

        if (type == 17 && length >= 0x15 &&
            hw->total_slots < HWINFO_MAX_MODULES) {
            int slot_idx = hw->total_slots++;
            HWMemModule *mod = &hw->modules[slot_idx];
            unsigned short size_raw;

            memset(mod, 0, sizeof(HWMemModule));

            /* Size: offset 0x0C (WORD) */
            size_raw = hw_read16(ptr + 0x0C);
            if (size_raw == 0) {
                mod->size_mb = 0; /* empty slot */
            } else if (size_raw == 0x7FFF && length >= 0x20) {
                /* Extended Size: offset 0x1C (DWORD) */
                mod->size_mb = (int)(hw_read32(ptr + 0x1C) & 0x7FFFFFFF);
            } else if (size_raw != 0xFFFF) {
                mod->size_mb = (size_raw & 0x8000) ?
                    ((size_raw & 0x7FFF) * 1024) : (int)size_raw;
            }

            /* Data/Total width: offsets 0x08, 0x0A */
            mod->total_width_bits = (int)hw_read16(ptr + 0x08);
            mod->data_width_bits  = (int)hw_read16(ptr + 0x0A);
            if (mod->total_width_bits == 0xFFFF) mod->total_width_bits = 0;
            if (mod->data_width_bits == 0xFFFF)  mod->data_width_bits = 0;

            /* Form Factor: offset 0x0E */
            if (length > 0x0E)
                hwinfo_safe_strcpy(mod->form_factor_str,
                    hwinfo_form_factor_str(ptr[0x0E]),
                    sizeof(mod->form_factor_str));

            /* Memory Type: offset 0x12 */
            if (length > 0x12)
                hwinfo_safe_strcpy(mod->type_str,
                    hwinfo_mem_type_str(ptr[0x12]),
                    sizeof(mod->type_str));

            /* Speed: offset 0x15 (WORD, MT/s) */
            mod->speed_mts = (int)hw_read16(ptr + 0x15);
            if (mod->speed_mts == 0xFFFF) mod->speed_mts = 0;

            /* Configured Speed: offset 0x20 (WORD, MT/s) */
            if (length >= 0x22) {
                mod->configured_speed_mts = (int)hw_read16(ptr + 0x20);
                if (mod->configured_speed_mts == 0xFFFF)
                    mod->configured_speed_mts = 0;
            }

            /* Rank: offset 0x1B (low nibble) */
            if (length > 0x1B)
                mod->rank = ptr[0x1B] & 0x0F;

            /* String fields from the string table after the structure */
            if (length > 0x10) {
                hwinfo_safe_strcpy(mod->locator,
                    smbios_get_str(ptr, length, ptr[0x10]),
                    sizeof(mod->locator));
                hwinfo_trim(mod->locator);
            }
            if (length > 0x11) {
                hwinfo_safe_strcpy(mod->bank,
                    smbios_get_str(ptr, length, ptr[0x11]),
                    sizeof(mod->bank));
                hwinfo_trim(mod->bank);
            }
            if (length > 0x17) {
                hwinfo_safe_strcpy(mod->manufacturer,
                    smbios_get_str(ptr, length, ptr[0x17]),
                    sizeof(mod->manufacturer));
                hwinfo_trim(mod->manufacturer);
            }
            if (length > 0x1A) {
                hwinfo_safe_strcpy(mod->part_number,
                    smbios_get_str(ptr, length, ptr[0x1A]),
                    sizeof(mod->part_number));
                hwinfo_trim(mod->part_number);
            }

            /* Aggregate stats for populated modules */
            if (mod->size_mb > 0) {
                hw->num_modules++;
                if (mod->speed_mts > hw->speed_mts)
                    hw->speed_mts = mod->speed_mts;
                if (mod->configured_speed_mts > hw->configured_speed_mts)
                    hw->configured_speed_mts = mod->configured_speed_mts;
                if (hw->memory_type[0] == '\0')
                    hwinfo_safe_strcpy(hw->memory_type, mod->type_str,
                                       sizeof(hw->memory_type));
            }
        }

        /* Skip past structure data and string table (double-null terminated) */
        next = ptr + length;
        while (next + 1 < end) {
            if (next[0] == 0 && next[1] == 0) { next += 2; break; }
            next++;
        }
        ptr = next;
    }
}

#endif /* _MSC_VER || __linux__ */

/*-----------------------------------------------------------------------*/
/* Platform-Specific Detection                                            */
/*-----------------------------------------------------------------------*/

#ifdef _MSC_VER
/* ================================================================
 * Windows Implementation
 * ================================================================ */

static void hwinfo_detect_system(HWInfo *hw)
{
    /* Timestamp */
    {
        time_t now = time(NULL);
        struct tm *tm_info = localtime(&now);
        strftime(hw->timestamp, sizeof(hw->timestamp),
                 "%Y-%m-%dT%H:%M:%S", tm_info);
    }

    /* Hostname */
    {
        DWORD size = sizeof(hw->hostname);
        if (!GetComputerNameA(hw->hostname, &size))
            strcpy(hw->hostname, "Unknown");
    }

    /* OS version (use RtlGetVersion for accurate info) */
    {
        OSVERSIONINFOEXA osvi;
        typedef LONG (WINAPI *RtlGetVersionPtr)(OSVERSIONINFOEXA*);
        RtlGetVersionPtr rtl_get_ver;
        memset(&osvi, 0, sizeof(osvi));
        osvi.dwOSVersionInfoSize = sizeof(osvi);
        rtl_get_ver = (RtlGetVersionPtr)GetProcAddress(
            GetModuleHandleA("ntdll.dll"), "RtlGetVersion");
        if (rtl_get_ver && rtl_get_ver(&osvi) == 0)
            sprintf(hw->os_name, "Windows %lu.%lu.%lu",
                    osvi.dwMajorVersion, osvi.dwMinorVersion, osvi.dwBuildNumber);
        else
            strcpy(hw->os_name, "Windows");
    }

    /* Architecture */
    {
        SYSTEM_INFO si;
        GetNativeSystemInfo(&si);
        switch (si.wProcessorArchitecture) {
            case PROCESSOR_ARCHITECTURE_AMD64: strcpy(hw->architecture, "x64"); break;
            case 12: /* PROCESSOR_ARCHITECTURE_ARM64 */ strcpy(hw->architecture, "ARM64"); break;
            case PROCESSOR_ARCHITECTURE_INTEL: strcpy(hw->architecture, "x86"); break;
            default: strcpy(hw->architecture, "Unknown"); break;
        }
    }

    /* CPU model from registry */
    {
        HKEY hKey;
        if (RegOpenKeyExA(HKEY_LOCAL_MACHINE,
                "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
                0, KEY_READ, &hKey) == ERROR_SUCCESS) {
            DWORD size = sizeof(hw->cpu_model);
            DWORD type = 0;
            if (RegQueryValueExA(hKey, "ProcessorNameString", NULL, &type,
                    (LPBYTE)hw->cpu_model, &size) != ERROR_SUCCESS)
                strcpy(hw->cpu_model, "Unknown");
            RegCloseKey(hKey);
            hwinfo_trim(hw->cpu_model);
        } else {
            strcpy(hw->cpu_model, "Unknown");
        }
    }

    /* Total physical RAM */
    {
        MEMORYSTATUSEX memInfo;
        memInfo.dwLength = sizeof(memInfo);
        if (GlobalMemoryStatusEx(&memInfo))
            hw->total_ram_gb = (double)memInfo.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
    }

    /* Logical processor count */
    {
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        hw->num_threads = si.dwNumberOfProcessors;
    }
}

static void hwinfo_detect_memory(HWInfo *hw)
{
    DWORD size = GetSystemFirmwareTable('RSMB', 0, NULL, 0);
    unsigned char *buf;
    DWORD table_len;

    if (size == 0) return;
    buf = (unsigned char*)malloc(size);
    if (!buf) return;

    if (GetSystemFirmwareTable('RSMB', 0, buf, size) == size) {
        /* Windows RSMB header: 8 bytes (version info + length), then table */
        table_len = hw_read32(buf + 4);
        parse_smbios_table(hw, buf + 8, table_len);
    }
    free(buf);
}

static void hwinfo_detect_cache(HWInfo *hw)
{
    DWORD buf_size = 0;
    unsigned char *buffer, *p;

    GetLogicalProcessorInformationEx(RelationCache, NULL, &buf_size);
    if (buf_size == 0) return;

    buffer = (unsigned char*)malloc(buf_size);
    if (!buffer) return;

    if (GetLogicalProcessorInformationEx(RelationCache,
            (PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX)buffer, &buf_size)) {
        p = buffer;
        while (p < buffer + buf_size) {
            PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX info =
                (PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX)p;
            if (info->Relationship == RelationCache) {
                int kb = (int)(info->Cache.CacheSize / 1024);
                switch (info->Cache.Level) {
                    case 1:
                        if (info->Cache.Type == CacheData ||
                            info->Cache.Type == CacheUnified) {
                            if (hw->l1d_cache_kb == 0) hw->l1d_cache_kb = kb;
                        } else if (info->Cache.Type == CacheInstruction) {
                            if (hw->l1i_cache_kb == 0) hw->l1i_cache_kb = kb;
                        }
                        break;
                    case 2:
                        if (hw->l2_cache_kb == 0) hw->l2_cache_kb = kb;
                        break;
                    case 3:
                        hw->l3_cache_kb += kb;
                        break;
                }
            }
            p += info->Size;
        }
    }
    free(buffer);
}

static void hwinfo_detect_cpu_freq(HWInfo *hw)
{
    HKEY hKey;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE,
            "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
            0, KEY_READ, &hKey) == ERROR_SUCCESS) {
        DWORD val, sz = sizeof(val);
        if (RegQueryValueExA(hKey, "~MHz", NULL, NULL,
                (LPBYTE)&val, &sz) == ERROR_SUCCESS)
            hw->cpu_base_mhz = (int)val;
        RegCloseKey(hKey);
    }
}

static void hwinfo_detect_numa(HWInfo *hw)
{
    ULONG highest = 0;
    if (GetNumaHighestNodeNumber(&highest))
        hw->numa_nodes = (int)highest + 1;
    else
        hw->numa_nodes = 1;
}

#else
/* ================================================================
 * POSIX (Linux / macOS) Implementation
 * ================================================================ */

static void hwinfo_detect_system(HWInfo *hw)
{
    /* Timestamp */
    {
        time_t now = time(NULL);
        struct tm *tm_info = localtime(&now);
        strftime(hw->timestamp, sizeof(hw->timestamp),
                 "%Y-%m-%dT%H:%M:%S", tm_info);
    }

    /* Hostname */
    if (gethostname(hw->hostname, sizeof(hw->hostname)) != 0)
        strcpy(hw->hostname, "Unknown");

    /* OS name */
#ifdef __APPLE__
    strcpy(hw->os_name, "macOS");
    {
        char ver[64] = {0};
        size_t len = sizeof(ver);
        if (sysctlbyname("kern.osproductversion", ver, &len, NULL, 0) == 0)
            sprintf(hw->os_name, "macOS %s", ver);
    }
#elif defined(__linux__)
    {
        FILE *f = fopen("/etc/os-release", "r");
        if (f) {
            char line[256];
            while (fgets(line, sizeof(line), f)) {
                if (strncmp(line, "PRETTY_NAME=", 12) == 0) {
                    char *start = strchr(line, '"');
                    if (start) {
                        start++;
                        char *end = strchr(start, '"');
                        if (end) *end = '\0';
                        hwinfo_safe_strcpy(hw->os_name, start, sizeof(hw->os_name));
                    }
                    break;
                }
            }
            fclose(f);
        }
        if (hw->os_name[0] == '\0') strcpy(hw->os_name, "Linux");
    }
#else
    strcpy(hw->os_name, "Unix");
#endif

    /* Architecture */
#if defined(__x86_64__) || defined(__amd64__)
    strcpy(hw->architecture, "x64");
#elif defined(__aarch64__) || defined(__arm64__)
    strcpy(hw->architecture, "ARM64");
#elif defined(__i386__)
    strcpy(hw->architecture, "x86");
#elif defined(__arm__)
    strcpy(hw->architecture, "ARM");
#else
    strcpy(hw->architecture, "Unknown");
#endif

    /* CPU model */
#ifdef __APPLE__
    {
        size_t len = sizeof(hw->cpu_model);
        if (sysctlbyname("machdep.cpu.brand_string", hw->cpu_model, &len, NULL, 0) != 0)
            strcpy(hw->cpu_model, "Unknown");
    }
#elif defined(__linux__)
    {
        FILE *f = fopen("/proc/cpuinfo", "r");
        if (f) {
            char line[256];
            while (fgets(line, sizeof(line), f)) {
                if (strncmp(line, "model name", 10) == 0 ||
                    strncmp(line, "Model\t", 6) == 0) {
                    char *colon = strchr(line, ':');
                    if (colon) {
                        colon++;
                        while (*colon == ' ' || *colon == '\t') colon++;
                        hwinfo_safe_strcpy(hw->cpu_model, colon, sizeof(hw->cpu_model));
                        hwinfo_trim(hw->cpu_model);
                    }
                    break;
                }
            }
            fclose(f);
        }
        if (hw->cpu_model[0] == '\0') strcpy(hw->cpu_model, "Unknown");
    }
#else
    strcpy(hw->cpu_model, "Unknown");
#endif

    /* Total RAM */
#ifdef __APPLE__
    {
        int64_t memsize = 0;
        size_t len = sizeof(memsize);
        if (sysctlbyname("hw.memsize", &memsize, &len, NULL, 0) == 0)
            hw->total_ram_gb = (double)memsize / (1024.0 * 1024.0 * 1024.0);
    }
#elif defined(__linux__)
    {
        struct sysinfo si;
        if (sysinfo(&si) == 0)
            hw->total_ram_gb = (double)si.totalram * si.mem_unit / (1024.0 * 1024.0 * 1024.0);
    }
#endif

    /* Thread count */
    hw->num_threads = (int)sysconf(_SC_NPROCESSORS_ONLN);
}

static void hwinfo_detect_memory(HWInfo *hw)
{
#ifdef __linux__
    /* Read SMBIOS table from sysfs (may require root or special perms) */
    FILE *f = fopen("/sys/firmware/dmi/tables/DMI", "rb");
    long size;
    unsigned char *buf;

    if (!f) return;
    fseek(f, 0, SEEK_END);
    size = ftell(f);
    fseek(f, 0, SEEK_SET);

    if (size <= 0 || size > 1024 * 1024) { fclose(f); return; }
    buf = (unsigned char*)malloc((size_t)size);
    if (!buf) { fclose(f); return; }

    if ((long)fread(buf, 1, (size_t)size, f) == size)
        parse_smbios_table(hw, buf, (size_t)size);

    free(buf);
    fclose(f);
#endif
    /* macOS: SMBIOS requires IOKit (complex); skip detailed module info */
}

static void hwinfo_detect_cache(HWInfo *hw)
{
#ifdef __linux__
    char path[256], buf[64];
    int i;
    for (i = 0; i < 10; i++) {
        FILE *f;
        int level = 0, size_kb = 0;
        char type[32] = {0};

        sprintf(path, "/sys/devices/system/cpu/cpu0/cache/index%d/level", i);
        f = fopen(path, "r");
        if (!f) break;
        if (fscanf(f, "%d", &level) != 1) level = 0;
        fclose(f);

        sprintf(path, "/sys/devices/system/cpu/cpu0/cache/index%d/type", i);
        f = fopen(path, "r");
        if (f) {
            if (fgets(type, sizeof(type), f)) {
                char *nl = strchr(type, '\n');
                if (nl) *nl = '\0';
            }
            fclose(f);
        }

        sprintf(path, "/sys/devices/system/cpu/cpu0/cache/index%d/size", i);
        f = fopen(path, "r");
        if (f) {
            if (fgets(buf, sizeof(buf), f))
                size_kb = atoi(buf); /* "32K" -> atoi gives 32 */
            fclose(f);
        }

        if (level == 1 &&
            (strcmp(type, "Data") == 0 || strcmp(type, "Unified") == 0))
            hw->l1d_cache_kb = size_kb;
        else if (level == 1 && strcmp(type, "Instruction") == 0)
            hw->l1i_cache_kb = size_kb;
        else if (level == 2)
            hw->l2_cache_kb = size_kb;
        else if (level == 3)
            hw->l3_cache_kb = size_kb;
    }
#elif defined(__APPLE__)
    int64_t val;
    size_t len;
    len = sizeof(val);
    if (sysctlbyname("hw.l1dcachesize", &val, &len, NULL, 0) == 0)
        hw->l1d_cache_kb = (int)(val / 1024);
    len = sizeof(val);
    if (sysctlbyname("hw.l1icachesize", &val, &len, NULL, 0) == 0)
        hw->l1i_cache_kb = (int)(val / 1024);
    len = sizeof(val);
    if (sysctlbyname("hw.l2cachesize", &val, &len, NULL, 0) == 0)
        hw->l2_cache_kb = (int)(val / 1024);
    len = sizeof(val);
    if (sysctlbyname("hw.l3cachesize", &val, &len, NULL, 0) == 0)
        hw->l3_cache_kb = (int)(val / 1024);
#endif
}

static void hwinfo_detect_cpu_freq(HWInfo *hw)
{
#ifdef __linux__
    FILE *f;
    int khz;

    f = fopen("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency", "r");
    if (f) {
        if (fscanf(f, "%d", &khz) == 1)
            hw->cpu_base_mhz = khz / 1000;
        fclose(f);
    }
    f = fopen("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "r");
    if (f) {
        if (fscanf(f, "%d", &khz) == 1)
            hw->cpu_max_mhz = khz / 1000;
        fclose(f);
    }
    /* Fallback: /proc/cpuinfo */
    if (hw->cpu_base_mhz == 0) {
        char line[256];
        f = fopen("/proc/cpuinfo", "r");
        if (f) {
            while (fgets(line, sizeof(line), f)) {
                if (strncmp(line, "cpu MHz", 7) == 0) {
                    char *colon = strchr(line, ':');
                    if (colon) hw->cpu_base_mhz = (int)atof(colon + 1);
                    break;
                }
            }
            fclose(f);
        }
    }
#elif defined(__APPLE__)
    int64_t val;
    size_t len = sizeof(val);
    if (sysctlbyname("hw.cpufrequency", &val, &len, NULL, 0) == 0)
        hw->cpu_base_mhz = (int)(val / 1000000);
    len = sizeof(val);
    if (sysctlbyname("hw.cpufrequency_max", &val, &len, NULL, 0) == 0)
        hw->cpu_max_mhz = (int)(val / 1000000);
#endif
}

static void hwinfo_detect_numa(HWInfo *hw)
{
#ifdef __linux__
    char path[64];
    int node;
    hw->numa_nodes = 0;
    for (node = 0; node < 256; node++) {
        FILE *f;
        sprintf(path, "/sys/devices/system/node/node%d/cpulist", node);
        f = fopen(path, "r");
        if (f) { hw->numa_nodes++; fclose(f); }
        else break;
    }
    if (hw->numa_nodes == 0) hw->numa_nodes = 1;
#else
    hw->numa_nodes = 1;
#endif
}

#endif /* end platform-specific */

/*-----------------------------------------------------------------------*/
/* Main Entry Points                                                      */
/*-----------------------------------------------------------------------*/

static void detect_hardware_info(HWInfo *hw)
{
    memset(hw, 0, sizeof(HWInfo));
    hwinfo_detect_system(hw);
    hwinfo_detect_memory(hw);
    hwinfo_detect_cache(hw);
    hwinfo_detect_cpu_freq(hw);
    hwinfo_detect_numa(hw);
}

/*
 * Print system identification to console
 */
static void print_system_info(const HWInfo *hw)
{
    printf("SYSTEM INFORMATION\n");
    printf(HLINE);
    printf("Hostname:       %s\n", hw->hostname);
    printf("OS:             %s\n", hw->os_name);
    printf("Architecture:   %s\n", hw->architecture);
    printf("CPU Model:      %s\n", hw->cpu_model);
    printf("Logical CPUs:   %d\n", hw->num_threads);
    printf("Total RAM:      %.1f GB\n", hw->total_ram_gb);
    printf("Timestamp:      %s\n", hw->timestamp);
}

/*
 * Print hardware details to console for side-by-side comparison.
 */
static void print_hardware_info(const HWInfo *hw)
{
    int i;

    printf("HARDWARE DETAILS\n");
    printf(HLINE);

    /* Memory */
    if (hw->num_modules > 0) {
        printf("Memory Type:    %s\n", hw->memory_type);
        printf("Memory Speed:   %d MT/s", hw->speed_mts);
        if (hw->configured_speed_mts > 0 &&
            hw->configured_speed_mts != hw->speed_mts)
            printf(" (configured: %d MT/s)", hw->configured_speed_mts);
        printf("\n");
        printf("Memory Slots:   %d of %d populated\n",
               hw->num_modules, hw->total_slots);
        for (i = 0; i < hw->total_slots; i++) {
            const HWMemModule *m = &hw->modules[i];
            if (m->size_mb > 0) {
                printf("  [%s] %d MB %s %d MT/s",
                       m->locator[0] ? m->locator : "?",
                       m->size_mb, m->type_str, m->speed_mts);
                if (m->rank > 0) printf(" Rank%d", m->rank);
                if (m->manufacturer[0]) printf(" %s", m->manufacturer);
                if (m->part_number[0]) printf(" %s", m->part_number);
                printf("\n");
            }
        }
    } else {
        printf("Memory Modules: Not available (SMBIOS access denied or N/A)\n");
    }

    /* Cache */
    if (hw->l1d_cache_kb > 0 || hw->l2_cache_kb > 0 || hw->l3_cache_kb > 0) {
        printf("Cache:         ");
        if (hw->l1d_cache_kb > 0) printf(" L1d=%dK", hw->l1d_cache_kb);
        if (hw->l1i_cache_kb > 0) printf(" L1i=%dK", hw->l1i_cache_kb);
        if (hw->l2_cache_kb > 0) {
            if (hw->l2_cache_kb >= 1024)
                printf(" L2=%dM", hw->l2_cache_kb / 1024);
            else
                printf(" L2=%dK", hw->l2_cache_kb);
        }
        if (hw->l3_cache_kb > 0) {
            if (hw->l3_cache_kb >= 1024)
                printf(" L3=%dM", hw->l3_cache_kb / 1024);
            else
                printf(" L3=%dK", hw->l3_cache_kb);
        }
        printf(" (per-core L1/L2, total L3)\n");
    }

    /* CPU frequency */
    if (hw->cpu_base_mhz > 0) {
        printf("CPU Frequency:  %d MHz", hw->cpu_base_mhz);
        if (hw->cpu_max_mhz > 0 && hw->cpu_max_mhz != hw->cpu_base_mhz)
            printf(" (max: %d MHz)", hw->cpu_max_mhz);
        printf("\n");
    }

    /* NUMA */
    if (hw->numa_nodes > 1)
        printf("NUMA Nodes:     %d\n", hw->numa_nodes);
}

#endif /* STREAM_HWINFO_H */
