/*-----------------------------------------------------------------------*/
/* Console Color Support for STREAM Benchmark                            */
/*                                                                       */
/* Provides ANSI escape code macros for colored terminal output.         */
/* Works on Windows 10+ (enables VT100 processing), Linux, and macOS.   */
/* Cross-platform: MSVC, GCC, Clang                                     */
/*-----------------------------------------------------------------------*/

#ifndef STREAM_COLORS_H
#define STREAM_COLORS_H

#include <stdio.h>

/*-----------------------------------------------------------------------*/
/* ANSI COLOR CODE MACROS                                                */
/*-----------------------------------------------------------------------*/

#define COL_RESET   "\033[0m"
#define COL_BOLD    "\033[1m"
#define COL_DIM     "\033[2m"

/* Standard colors */
#define COL_RED     "\033[31m"
#define COL_GREEN   "\033[32m"
#define COL_YELLOW  "\033[33m"
#define COL_BLUE    "\033[34m"
#define COL_CYAN    "\033[36m"
#define COL_WHITE   "\033[37m"

/* Bright colors */
#define COL_BRED    "\033[91m"
#define COL_BGREEN  "\033[92m"
#define COL_BYELLOW "\033[93m"
#define COL_BCYAN   "\033[96m"
#define COL_BWHITE  "\033[97m"

/*-----------------------------------------------------------------------*/
/* SEMANTIC COLOR ALIASES (consistent look across CPU & GPU benchmarks)  */
/*-----------------------------------------------------------------------*/

#define C_TITLE     COL_BOLD COL_BCYAN    /* Banner / version headers     */
#define C_SECTION   COL_BOLD COL_BWHITE   /* Section titles               */
#define C_LABEL     COL_CYAN              /* Field labels (key: value)    */
#define C_VALUE     COL_BWHITE            /* Important values             */
#define C_HLINE     COL_DIM               /* Horizontal dividers          */
#define C_DIM       COL_DIM               /* Dimmed / secondary text      */
#define C_OK        COL_GREEN             /* Success messages             */
#define C_WARN      COL_YELLOW            /* Warnings / notes             */
#define C_ERR       COL_BRED              /* Error messages               */
#define C_RATE      COL_BOLD COL_BGREEN   /* Benchmark rate numbers       */
#define C_HDR       COL_BOLD COL_WHITE    /* Table headers                */
#define C_FILE      COL_CYAN              /* File paths / output info     */
#define C_DEVICE    COL_BOLD COL_BCYAN    /* Device / hardware names      */
#define C_R         COL_RESET             /* Shorthand for reset          */

/*-----------------------------------------------------------------------*/
/* Enable ANSI escape sequences on Windows 10+                           */
/*-----------------------------------------------------------------------*/

static void enable_colors(void)
{
#ifdef _WIN32
    /* Windows 10 1607+ supports ANSI escape codes when
     * ENABLE_VIRTUAL_TERMINAL_PROCESSING (0x0004) is set. */
    HANDLE hOut = GetStdHandle((DWORD)-11); /* STD_OUTPUT_HANDLE */
    if (hOut != INVALID_HANDLE_VALUE) {
        DWORD mode = 0;
        if (GetConsoleMode(hOut, &mode)) {
            SetConsoleMode(hOut, mode | 0x0004);
        }
    }
#endif
    /* On Linux/macOS, ANSI codes work natively — nothing to do. */
}

#endif /* STREAM_COLORS_H */
