// THE STATUS CONTROL'S WORDS, without a mount: the uptime from the Host's own two instants, the
// connection line, a check's line and a worker's line. The control's wiring is live-status.mount.spec.ts.
import { describe, expect, it } from 'vitest';
import { LiveIcons, checkWords, connectionWords, liveWord, upWords, workerWords } from '../serviceStatus';

describe('the chip', () => {
  it('says live or offline and shows a radiating dot, struck through when offline', () => {
    expect(liveWord(true)).toBe('live');
    expect(liveWord(false)).toBe('offline');
    expect(LiveIcons).toEqual({ live: 'sensors', offline: 'sensors_off' });
  });
});

describe('upWords', () => {
  const at = (iso: string) => iso;

  it('is days and hours, hours and minutes, minutes, or under a minute, from the two instants', () => {
    expect(upWords(at('2026-10-17T06:00:00Z'), at('2026-10-20T10:07:00Z'))).toBe('3 days 4 hours');
    expect(upWords(at('2026-10-20T08:02:00Z'), at('2026-10-20T10:07:00Z'))).toBe('2 hours 5 minutes');
    expect(upWords(at('2026-10-20T10:02:00Z'), at('2026-10-20T10:07:00Z'))).toBe('5 minutes');
    expect(upWords(at('2026-10-20T10:06:30Z'), at('2026-10-20T10:07:00Z'))).toBe('under a minute');
    expect(upWords(at('2026-10-19T10:07:00Z'), at('2026-10-20T10:07:00Z'))).toBe('1 day');
    expect(upWords(at('2026-10-20T09:06:00Z'), at('2026-10-20T10:07:00Z'))).toBe('1 hour 1 minute');
  });

  it('is not known for a start the Host could not read, one after now, or one that does not parse', () => {
    expect(upWords(null, '2026-10-20T10:07:00Z')).toBe('not known');
    expect(upWords('2026-10-20T11:00:00Z', '2026-10-20T10:07:00Z')).toBe('not known');
    expect(upWords('yesterday', '2026-10-20T10:07:00Z')).toBe('not known');
  });
});

describe('connectionWords', () => {
  it('says live with its since, connecting before the hub ever connected, and offline with its since', () => {
    expect(connectionWords(true, true, '10:04:12 AM')).toBe('Live: this window receives updates, since 10:04:12 AM.');
    expect(connectionWords(true, true, null)).toBe('Live: this window receives updates.');
    expect(connectionWords(false, false, null)).toBe('Connecting: this window has not received updates yet.');
    expect(connectionWords(false, true, '10:09:00 AM')).toBe('Offline since 10:09:00 AM: what is on screen may be out of date.');
    expect(connectionWords(false, true, null)).toBe('Offline: what is on screen may be out of date.');
  });
});

describe('a check and a worker', () => {
  it("name the check in a person's words with the Host's own detail", () => {
    expect(checkWords({ name: 'database', ok: true, detail: 'open' })).toBe('Database: open');
    expect(checkWords({ name: 'dataRoot', ok: true, detail: 'writable' })).toBe('Data folder: writable');
    expect(checkWords({ name: 'pump', ok: false, detail: 'no pass for over a minute' })).toBe('Delivery pump: no pass for over a minute');
    expect(checkWords({ name: 'other', ok: true, detail: 'fine' })).toBe('other: fine');
  });

  it('name the worker, its state, its time and its build', () => {
    const worker = { id: 'worker-1', version: '2026.10.19.1', state: 'connected' as const, connectedSince: 'x', droppedAt: null };
    expect(workerWords(worker, '9:00:00 AM', null)).toBe('worker-1: connected since 9:00:00 AM, 2026.10.19.1');
    expect(workerWords({ ...worker, state: 'dropped', version: null }, '9:00:00 AM', '9:30:00 AM')).toBe('worker-1: dropped at 9:30:00 AM');
  });
});
