// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { createPoll, usePoll, type Poll } from '../../src/renderer/src/usePoll';

// The shared poller, on fake timers and a stand-in for the page's visibility.
let hidden = false;
function show(visible: boolean) {
  hidden = !visible;
  document.dispatchEvent(new Event('visibilitychange'));
}
const polls: Poll[] = [];
const made = (p: Poll) => { polls.push(p); return p; };

beforeEach(() => {
  hidden = false;
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
  vi.useFakeTimers();
});
afterEach(() => {
  for (const p of polls.splice(0)) p.stop();
  vi.useRealTimers();
  delete (document as unknown as { hidden?: boolean }).hidden;
});

describe('a poll', () => {
  it('runs at once, then every interval', async () => {
    const fn = vi.fn(async () => {});
    made(createPoll(fn, 1000)).start();
    expect(fn).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(999);
    expect(fn).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(fn).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(3000);
    expect(fn).toHaveBeenCalledTimes(5);
  });

  it('waits one interval for its first run when it is not immediate', async () => {
    const fn = vi.fn(async () => {});
    made(createPoll(fn, 500, { immediate: false })).start();
    expect(fn).toHaveBeenCalledTimes(0);
    await vi.advanceTimersByTimeAsync(500);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('never runs two at a time: a tick is skipped while the run before it is still out', async () => {
    const done: (() => void)[] = [];
    const fn = vi.fn(() => new Promise<void>((r) => done.push(r)));
    made(createPoll(fn, 1000)).start();
    await vi.advanceTimersByTimeAsync(3500);
    expect(fn).toHaveBeenCalledTimes(1);
    done[0]!();
    await vi.advanceTimersByTimeAsync(400); // not at once: at the next tick
    expect(fn).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(100);
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it('rests while the page is hidden and runs once when it shows again', async () => {
    const fn = vi.fn(async () => {});
    made(createPoll(fn, 1000)).start();
    await vi.advanceTimersByTimeAsync(1000);
    expect(fn).toHaveBeenCalledTimes(2);

    show(false);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fn).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(0); // no timer wakes a hidden page

    show(true);
    expect(fn).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(999);
    expect(fn).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(1);
    expect(fn).toHaveBeenCalledTimes(4);
  });

  it('started on a hidden page reads once and then rests until the page shows', async () => {
    hidden = true;
    const fn = vi.fn(async () => {});
    made(createPoll(fn, 1000)).start();
    expect(fn).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5000);
    expect(fn).toHaveBeenCalledTimes(1);
    show(true);
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it('showing the page while a run is out does not start a second one', async () => {
    const done: (() => void)[] = [];
    const fn = vi.fn(() => new Promise<void>((r) => done.push(r)));
    made(createPoll(fn, 1000)).start();
    show(false);
    show(true);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('slows down while its runs fail, up to ten seconds, and is back at its interval after one that works', async () => {
    let fail = true;
    const at: number[] = [];
    const t0 = Date.now();
    const fn = vi.fn(async () => { at.push(Date.now() - t0); if (fail) throw new Error('down'); });
    made(createPoll(fn, 1000)).start();
    await vi.advanceTimersByTimeAsync(44_000);
    // 2 s, 4 s, 8 s after a failure, then every 10 s.
    expect(at).toEqual([0, 2000, 6000, 14_000, 24_000, 34_000, 44_000]);

    fail = false;
    at.length = 0;
    await vi.advanceTimersByTimeAsync(12_000);
    expect(at).toEqual([54_000, 55_000, 56_000]);
  });

  it('takes an answer of false as a failed run', async () => {
    const fn = vi.fn(async () => false);
    made(createPoll(fn, 1000)).start();
    await vi.advanceTimersByTimeAsync(2000);
    expect(fn).toHaveBeenCalledTimes(2); // at 0 and at 2 s, not at 1 s
  });

  it('a poll slower than ten seconds keeps its interval while it fails', async () => {
    const fn = vi.fn(async () => { throw new Error('down'); });
    made(createPoll(fn, 30_000)).start();
    await vi.advanceTimersByTimeAsync(90_000);
    expect(fn).toHaveBeenCalledTimes(4);
  });

  it('showing the page again asks at once, whatever the back-off said', async () => {
    const fn = vi.fn(async () => { throw new Error('down'); });
    made(createPoll(fn, 1000)).start();
    await vi.advanceTimersByTimeAsync(6000); // runs at 0, 2 s, 6 s: the next is 8 s away
    expect(fn).toHaveBeenCalledTimes(3);
    show(false);
    show(true);
    expect(fn).toHaveBeenCalledTimes(4);
  });

  it('stops: no run, no timer and no listener after stop; a second start runs it again', async () => {
    const fn = vi.fn(async () => {});
    const poll = made(createPoll(fn, 1000));
    poll.start();
    poll.start(); // a second start of a running poll does nothing
    expect(fn).toHaveBeenCalledTimes(1);
    expect(poll.running).toBe(true);
    poll.stop();
    expect(poll.running).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(5000);
    show(false);
    show(true);
    expect(fn).toHaveBeenCalledTimes(1);
    poll.start();
    expect(fn).toHaveBeenCalledTimes(2);
  });
});

describe('usePoll in a component', () => {
  it('starts when the component mounts and stops when it unmounts', async () => {
    const fn = vi.fn(async () => {});
    const w = mount(defineComponent({ setup() { usePoll(fn, 1000); return () => h('div'); } }));
    expect(fn).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(2000);
    expect(fn).toHaveBeenCalledTimes(3);
    w.unmount();
    await vi.advanceTimersByTimeAsync(5000);
    show(false);
    show(true);
    expect(fn).toHaveBeenCalledTimes(3);
  });

  it('with autoStart off, runs only once the component starts it, and still stops on unmount', async () => {
    const fn = vi.fn(async () => {});
    let poll!: Poll;
    const w = mount(defineComponent({ setup() { poll = usePoll(fn, () => 1000, { autoStart: false, immediate: false }); return () => h('div'); } }));
    await vi.advanceTimersByTimeAsync(3000);
    expect(fn).toHaveBeenCalledTimes(0);
    poll.start();
    await vi.advanceTimersByTimeAsync(1000);
    expect(fn).toHaveBeenCalledTimes(1);
    w.unmount();
    await vi.advanceTimersByTimeAsync(3000);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
