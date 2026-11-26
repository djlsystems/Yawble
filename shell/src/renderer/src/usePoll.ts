import { getCurrentInstance, onBeforeUnmount, onMounted } from 'vue';

/**
 * One way to ask the core again and again. A poll:
 * - runs its function at once when it starts, then every interval;
 * - never runs two at a time: a tick that comes while the previous run is still out is skipped;
 * - rests while the page is hidden (a minimised window, a background tab) and runs once when the page shows again;
 * - slows down while its runs fail (the wait doubles with each failure in a row, up to MAX_BACKOFF_MS) and returns to
 *   its interval with the first run that works;
 * - stops with the component that made it.
 *
 * A run fails when the function throws, rejects, or answers false (for a function that catches its own errors and
 * shows them). An interval longer than MAX_BACKOFF_MS is never stretched.
 */
export const MAX_BACKOFF_MS = 10_000;

export interface PollOptions {
  /** Run at once on start (the default). False: the first run is one interval after the start. */
  immediate?: boolean;
  /** usePoll only. Start when the component mounts (the default). False: the component calls start() itself. */
  autoStart?: boolean;
}

export interface Poll {
  /** Starts the poll. A second start of a running poll does nothing. */
  start(): void;
  /** Stops it: no more runs, no timer, no listener. A run that is out finishes, and its result is ignored here. */
  stop(): void;
  /** True between start and stop. */
  readonly running: boolean;
}

type PollFn = () => unknown;

const pageHidden = () => typeof document !== 'undefined' && document.hidden === true;

/** A poll with no component: the caller starts and stops it. */
export function createPoll(fn: PollFn, intervalMs: number | (() => number), options: PollOptions = {}): Poll {
  let timer: ReturnType<typeof setInterval> | null = null;
  let running = false;
  let inFlight = false;
  let failures = 0;
  // Ticks still to skip before the next run: the back-off, counted in intervals.
  let skip = 0;
  let every = 0;

  const run = async () => {
    if (!running || inFlight) return;
    inFlight = true;
    let ok = true;
    try { ok = (await fn()) !== false; } catch { ok = false; }
    inFlight = false;
    if (!running) return;
    if (ok) { failures = 0; skip = 0; return; }
    failures++;
    // After n failures in a row the wait is interval * 2^n, capped; an interval over the cap waits its own length.
    const wait = Math.min(every * 2 ** Math.min(failures, 20), Math.max(MAX_BACKOFF_MS, every));
    skip = Math.max(0, Math.ceil(wait / every) - 1);
  };

  const tick = () => {
    if (skip > 0) { skip--; return; }
    void run();
  };
  const arm = () => { if (timer === null) timer = setInterval(tick, every); };
  const disarm = () => { if (timer !== null) { clearInterval(timer); timer = null; } };

  const onVisibility = () => {
    if (!running) return;
    if (pageHidden()) { disarm(); return; }
    // Back on screen: what it shows may be old, so ask now, whatever the back-off said, and tick on from here.
    skip = 0;
    disarm();
    arm();
    void run();
  };

  return {
    start() {
      if (running) return;
      running = true;
      failures = 0;
      skip = 0;
      every = Math.max(1, typeof intervalMs === 'function' ? intervalMs() : intervalMs);
      if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVisibility);
      if (options.immediate !== false) void run();
      if (!pageHidden()) arm();
    },
    stop() {
      if (!running) return;
      running = false;
      disarm();
      if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVisibility);
    },
    get running() { return running; },
  };
}

/** A poll that belongs to the component being set up: it starts when the component mounts and stops when it unmounts. */
export function usePoll(fn: PollFn, intervalMs: number | (() => number), options: PollOptions = {}): Poll {
  const poll = createPoll(fn, intervalMs, options);
  if (getCurrentInstance()) {
    if (options.autoStart !== false) onMounted(() => poll.start());
    onBeforeUnmount(() => poll.stop());
  }
  return poll;
}
