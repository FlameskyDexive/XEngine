/* XTS vendored MSVC shim (not upstream QuickJS code).
 * quickjs.c unconditionally includes <sys/time.h> and calls gettimeofday()
 * for Date-now timestamps. cl.exe has neither, so the MSVC build adds this
 * directory to the include path. Recorded in Native/source-manifest.json. */
#ifndef XEngine_Xts_Msvc_Sys_Time_H
#define XEngine_Xts_Msvc_Sys_Time_H

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* winsock.h (pulled in by windows.h on some configs) defines the same
 * struct under this guard; keep both orders working. */
#ifndef _TIMEVAL_DEFINED
#define _TIMEVAL_DEFINED
struct timeval {
    long tv_sec;
    long tv_usec;
};
#endif

__inline int gettimeofday(struct timeval *tv, void *tz)
{
    FILETIME ft;
    ULARGE_INTEGER u;
    /* 100ns ticks between 1601-01-01 and 1970-01-01 */
    const unsigned long long epoch_diff = 116444736000000000ULL;

    (void)tz;
    if (tv != NULL) {
        GetSystemTimePreciseAsFileTime(&ft);
        u.LowPart = ft.dwLowDateTime;
        u.HighPart = ft.dwHighDateTime;
        if (u.QuadPart < epoch_diff) {
            tv->tv_sec = 0;
            tv->tv_usec = 0;
        } else {
            unsigned long long t = u.QuadPart - epoch_diff;
            tv->tv_sec = (long)(t / 10000000ULL);
            tv->tv_usec = (long)((t % 10000000ULL) / 10ULL);
        }
    }
    return 0;
}

#endif /* XEngine_Xts_Msvc_Sys_Time_H */
