/*
 * Force-included into every OWL source built for Windows (-include). OWL's
 * own files stay as upstream wrote them; what POSIX gives them, this and the
 * headers beside it give them on MinGW.
 */
#pragma once

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <stdint.h> /* radiotap.h counts on it arriving from elsewhere */
#include <sys/types.h>
#include <time.h>
#include <io.h>     /* close, umask */
#include <direct.h> /* chdir */

static inline struct tm *localtime_r(const time_t *t, struct tm *out)
{
	return localtime_s(out, t) ? NULL : out;
}

/* owl.c's -D daemonize(): there is no fork on Windows, so -D fails cleanly. */
static inline int fork(void) { return -1; }
static inline int setsid(void) { return -1; }
#define _SC_OPEN_MAX 0
static inline long sysconf(int name) { (void) name; return 0; }

/* core.c prints statistics on SIGUSR1; nothing sends it here. */
#ifndef SIGUSR1
#define SIGUSR1 30
#endif
#ifndef SIGHUP
#define SIGHUP 1
#endif
#ifndef SIGCHLD
#define SIGCHLD 17
#endif
