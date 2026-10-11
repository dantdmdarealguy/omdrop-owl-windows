/*
 * ev.h's event loop: timers, ev_io watchers over wait handles, fed events,
 * and Ctrl-C, which ends ev_run so owl.c can tear the adapter down.
 *
 * Timing matters: AWDL slots are 16 TU (16.4 ms), and Windows' default timer
 * tick is 15.6 ms. Waits use a high-resolution waitable timer where Windows
 * has one (10 1803 and later), else timeBeginPeriod(1).
 */
#include <windows.h>
#include <mmsystem.h>
#include <string.h>

#include <ev.h>

#include "log.h"

#define MAX_TIMERS 16
#define MAX_IOS 8
#define MAX_FDS 8
#define MAX_PENDING 64

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

struct fd_map {
	int fd;
	int n;
	HANDLE h[2];
};

struct ev_loop {
	int init;
	ev_timer *timers[MAX_TIMERS];
	int ntimers;
	ev_io *ios[MAX_IOS];
	int nios;
	ev_watcher *pending[MAX_PENDING];
	int npending;
	volatile LONG stop;
	HANDLE wake;  /* Ctrl-C */
	HANDLE timer; /* high-resolution waitable timer, or NULL */
	LARGE_INTEGER qpf;
};

static struct ev_loop default_loop;
static struct fd_map fds[MAX_FDS];
static int nfds;

static BOOL WINAPI on_ctrl(DWORD type)
{
	if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT) {
		ev_break(&default_loop, 0);
		return TRUE;
	}
	return FALSE;
}

struct ev_loop *ev_default_loop(unsigned int flags)
{
	(void) flags;
	struct ev_loop *loop = &default_loop;
	if (!loop->init) {
		QueryPerformanceFrequency(&loop->qpf);
		loop->wake = CreateEventW(NULL, TRUE, FALSE, NULL);
		loop->timer = CreateWaitableTimerExW(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
		if (!loop->timer) {
			log_info("no high-resolution timer; using timeBeginPeriod(1)");
			timeBeginPeriod(1);
		}
		SetConsoleCtrlHandler(on_ctrl, TRUE);
		loop->init = 1;
	}
	return loop;
}

double ev_now(struct ev_loop *loop)
{
	LARGE_INTEGER c;
	QueryPerformanceCounter(&c);
	return (double) c.QuadPart / (double) loop->qpf.QuadPart;
}

void ev_win_watch(int fd, void *handle)
{
	for (int i = 0; i < nfds; i++) {
		if (fds[i].fd == fd) {
			if (fds[i].n < 2)
				fds[i].h[fds[i].n++] = handle;
			return;
		}
	}
	if (nfds < MAX_FDS)
		fds[nfds++] = (struct fd_map) {.fd = fd, .n = 1, .h = {handle}};
}

static struct fd_map *fd_lookup(int fd)
{
	for (int i = 0; i < nfds; i++)
		if (fds[i].fd == fd)
			return &fds[i];
	return NULL;
}

static void unpend(struct ev_loop *loop, ev_watcher *w)
{
	if (!w->pending)
		return;
	for (int i = 0; i < loop->npending; i++) {
		if (loop->pending[i] == w) {
			memmove(&loop->pending[i], &loop->pending[i + 1], (loop->npending - i - 1) * sizeof(loop->pending[0]));
			loop->npending--;
			break;
		}
	}
	w->pending = 0;
}

void ev_feed_event(struct ev_loop *loop, void *watcher, int revents)
{
	ev_watcher *w = watcher;
	if (w->pending) {
		w->revents |= revents;
		return;
	}
	if (loop->npending == MAX_PENDING) {
		log_error("ev: pending queue full");
		return;
	}
	w->pending = 1;
	w->revents = revents;
	loop->pending[loop->npending++] = w;
}

void ev_timer_start(struct ev_loop *loop, ev_timer *w)
{
	if (w->active)
		return;
	if (loop->ntimers == MAX_TIMERS) {
		log_error("ev: too many timers");
		return;
	}
	w->at = ev_now(loop) + w->after;
	w->active = 1;
	loop->timers[loop->ntimers++] = w;
}

void ev_timer_stop(struct ev_loop *loop, ev_timer *w)
{
	unpend(loop, (ev_watcher *) w);
	if (!w->active)
		return;
	for (int i = 0; i < loop->ntimers; i++) {
		if (loop->timers[i] == w) {
			loop->timers[i] = loop->timers[--loop->ntimers];
			break;
		}
	}
	w->active = 0;
}

/* libev: restart a repeating timer at `repeat` from now; stop a one-shot. */
void ev_timer_again(struct ev_loop *loop, ev_timer *w)
{
	if (w->repeat > 0) {
		if (!w->active) {
			w->after = w->repeat;
			ev_timer_start(loop, w);
		} else {
			w->at = ev_now(loop) + w->repeat;
		}
	} else {
		ev_timer_stop(loop, w);
	}
}

void ev_io_start(struct ev_loop *loop, ev_io *w)
{
	if (w->active || loop->nios == MAX_IOS)
		return;
	if (!fd_lookup(w->fd))
		log_warn("ev: no handle for fd %d", w->fd);
	w->active = 1;
	loop->ios[loop->nios++] = w;
}

void ev_io_stop(struct ev_loop *loop, ev_io *w)
{
	unpend(loop, (ev_watcher *) w);
	if (!w->active)
		return;
	for (int i = 0; i < loop->nios; i++) {
		if (loop->ios[i] == w) {
			loop->ios[i] = loop->ios[--loop->nios];
			break;
		}
	}
	w->active = 0;
}

void ev_signal_start(struct ev_loop *loop, ev_signal *w)
{
	(void) loop;
	w->active = 1; /* nothing raises signals here; Ctrl-C ends the loop instead */
}

void ev_break(struct ev_loop *loop, int how)
{
	(void) how;
	InterlockedExchange(&loop->stop, 1);
	if (loop->wake)
		SetEvent(loop->wake);
}

/* Fire due timers into the pending queue; returns seconds until the next. */
static double run_timers(struct ev_loop *loop)
{
	double now = ev_now(loop), next = 1e9;
	/* A callback may start or stop timers, so collect first. */
	ev_timer *due[MAX_TIMERS];
	int ndue = 0;
	for (int i = 0; i < loop->ntimers; i++)
		if (loop->timers[i]->at <= now)
			due[ndue++] = loop->timers[i];
	for (int i = 0; i < ndue; i++) {
		ev_timer *w = due[i];
		if (w->repeat > 0) {
			w->at += w->repeat;
			if (w->at < now)
				w->at = now;
		} else {
			ev_timer_stop(loop, w);
		}
		ev_feed_event(loop, w, EV_TIMER);
	}
	for (int i = 0; i < loop->ntimers; i++)
		if (loop->timers[i]->at - now < next)
			next = loop->timers[i]->at - now;
	return next;
}

static void wait_handles(struct ev_loop *loop, double timeout)
{
	HANDLE hs[MAXIMUM_WAIT_OBJECTS];
	ev_io *owner[MAXIMUM_WAIT_OBJECTS] = {NULL}; /* the watcher each handle belongs to */
	DWORD n = 0, ms = INFINITE;
	hs[n++] = loop->wake;
	for (int i = 0; i < loop->nios; i++) {
		struct fd_map *m = fd_lookup(loop->ios[i]->fd);
		for (int k = 0; m && k < m->n && n < MAXIMUM_WAIT_OBJECTS - 1; k++) {
			owner[n] = loop->ios[i];
			hs[n++] = m->h[k];
		}
	}
	if (timeout <= 0) {
		ms = 0;
	} else if (timeout < 1e8) {
		if (loop->timer) {
			LARGE_INTEGER due = {.QuadPart = -(LONGLONG) (timeout * 1e7)}; /* relative, 100 ns */
			if (due.QuadPart == 0)
				due.QuadPart = -1;
			SetWaitableTimer(loop->timer, &due, 0, NULL, NULL, FALSE);
			hs[n++] = loop->timer;
		} else {
			ms = (DWORD) (timeout * 1000.0 + 0.999);
		}
	}
	DWORD r = WaitForMultipleObjects(n, hs, FALSE, ms);
	if (loop->timer)
		CancelWaitableTimer(loop->timer);

	/* The handle that ended the wait counts as ready even though it may read
	 * as unsignaled now: an auto-reset event (Wintun's read event is one) is
	 * consumed by the wait that sees it, and polling it again loses the
	 * wakeup. Wintun signals once per empty-to-nonempty edge, so a lost one
	 * stalled awdl0 for good. */
	if (r >= WAIT_OBJECT_0 && r < WAIT_OBJECT_0 + n && owner[r - WAIT_OBJECT_0])
		ev_feed_event(loop, owner[r - WAIT_OBJECT_0], EV_READ);

	/* Every other ready watcher, not only the first handle Windows reports. */
	for (int i = 0; i < loop->nios; i++) {
		struct fd_map *m = fd_lookup(loop->ios[i]->fd);
		for (int k = 0; m && k < m->n; k++) {
			if (WaitForSingleObject(m->h[k], 0) == WAIT_OBJECT_0) {
				ev_feed_event(loop, loop->ios[i], EV_READ);
				break;
			}
		}
	}
}

int ev_run(struct ev_loop *loop, int flags)
{
	(void) flags;
	while (!loop->stop) {
		double next = run_timers(loop);
		wait_handles(loop, loop->npending ? 0 : next);
		run_timers(loop);

		/* Events fed by these callbacks run on the next pass. A watcher still
		 * marked pending here just gains revents; one stopped by an earlier
		 * callback (pending cleared) is skipped, as libev would. */
		ev_watcher *batch[MAX_PENDING];
		int n = loop->npending;
		memcpy(batch, loop->pending, n * sizeof(batch[0]));
		loop->npending = 0;
		for (int i = 0; i < n && !loop->stop; i++) {
			if (!batch[i]->pending)
				continue;
			batch[i]->pending = 0;
			batch[i]->cb(loop, batch[i], batch[i]->revents);
		}
	}
	log_info("event loop stopped");
	return 0;
}
