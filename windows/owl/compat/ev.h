/*
 * The part of libev OWL's core.c and owl.c use, over Windows wait handles
 * (ev_win.c). libev itself can't wait on a HANDLE, and that is all a serial
 * port or a Wintun session gives us.
 *
 * An ev_io's "fd" is a number io_esp.c makes up and maps to handles with
 * ev_win_watch(). It counts as readable while any of them is signaled, so
 * those handles must behave like a readable fd: signaled while data waits.
 */
#pragma once

struct ev_loop;

#define EV_READ 0x01
#define EV_TIMER 0x100
#define EV_SIGNAL 0x400

#define EV_WATCHER_FIELDS(type) \
	int active;                 \
	int pending;                \
	int revents;                \
	void *data;                 \
	void (*cb)(struct ev_loop *loop, struct type *w, int revents);

typedef struct ev_watcher {
	EV_WATCHER_FIELDS(ev_watcher)
} ev_watcher;

typedef struct ev_timer {
	EV_WATCHER_FIELDS(ev_timer)
	double after, repeat, at;
} ev_timer;

typedef struct ev_io {
	EV_WATCHER_FIELDS(ev_io)
	int fd, events;
} ev_io;

typedef struct ev_signal {
	EV_WATCHER_FIELDS(ev_signal)
	int signum;
} ev_signal;

#define ev_watcher_init_(w, cb_) \
	do { (w)->active = (w)->pending = (w)->revents = 0; (w)->cb = (cb_); } while (0)
#define ev_timer_init(w, cb_, after_, repeat_) \
	do { ev_watcher_init_(w, cb_); ev_timer_set(w, after_, repeat_); } while (0)
#define ev_timer_set(w, after_, repeat_) \
	do { (w)->after = (after_); (w)->repeat = (repeat_); } while (0)
#define ev_io_init(w, cb_, fd_, events_) \
	do { ev_watcher_init_(w, cb_); (w)->fd = (fd_); (w)->events = (events_); } while (0)
#define ev_signal_init(w, cb_, signum_) \
	do { ev_watcher_init_(w, cb_); (w)->signum = (signum_); } while (0)

struct ev_loop *ev_default_loop(unsigned int flags);
#define EV_DEFAULT ev_default_loop(0)

void ev_timer_start(struct ev_loop *loop, ev_timer *w);
void ev_timer_stop(struct ev_loop *loop, ev_timer *w);
void ev_timer_again(struct ev_loop *loop, ev_timer *w);
void ev_io_start(struct ev_loop *loop, ev_io *w);
void ev_io_stop(struct ev_loop *loop, ev_io *w);
void ev_signal_start(struct ev_loop *loop, ev_signal *w);
void ev_feed_event(struct ev_loop *loop, void *w, int revents);
double ev_now(struct ev_loop *loop);
int ev_run(struct ev_loop *loop, int flags);
void ev_break(struct ev_loop *loop, int how);

/* Not libev: "fd" is readable while `handle` (a Windows HANDLE) is signaled.
 * Up to two handles per fd. */
void ev_win_watch(int fd, void *handle);
