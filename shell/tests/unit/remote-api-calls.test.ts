// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createRemoteApi } from '../../src/renderer/src/remoteApi';

// The remote view's calls over its socket: a call is sent at once or it fails, and a failed call is never sent later.
// Fakes only: the socket is a stand-in, the reconnect waits run on fake timers.
class FakeSocket {
  static OPEN = 1;
  static all: FakeSocket[] = [];
  readyState = 0;
  sent: { id: number; method: string }[] = [];
  onopen: (() => void) | null = null;
  onmessage: ((m: { data: string }) => void) | null = null;
  onclose: ((e: { reason: string }) => void) | null = null;
  constructor(public url: string) { FakeSocket.all.push(this); }
  send(text: string) { this.sent.push(JSON.parse(text)); }
  open() { this.readyState = 1; this.onopen?.(); }
  close(reason = '') { this.readyState = 3; this.onclose?.({ reason }); }
  answer(message: unknown) { this.onmessage?.({ data: JSON.stringify(message) }); }
}
const last = () => FakeSocket.all[FakeSocket.all.length - 1];
const allSent = () => FakeSocket.all.flatMap((s) => s.sent.map((m) => m.method));
const CLOSED = 'the connection to the instance closed';

beforeEach(() => {
  FakeSocket.all = [];
  vi.useFakeTimers();
  vi.stubGlobal('WebSocket', FakeSocket);
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('remote calls and the connection', () => {
  it('a call made while the connection is closed fails at once and is not sent after the next connect', async () => {
    const api = createRemoteApi('ws://example.invalid/rpc');
    last().open();
    last().close();

    const failed = await api.call('bus.answer', { id: 7, answer: 'approve' }).catch((e: Error) => e);
    expect((failed as Error).message).toBe(CLOSED);

    await vi.advanceTimersByTimeAsync(500);
    expect(FakeSocket.all).toHaveLength(2);
    // Still opening: the screen is told "closed", so this one is not kept either.
    const whileOpening = await api.call('board.move', { id: 1 }).catch((e: Error) => e);
    expect((whileOpening as Error).message).toBe(CLOSED);
    last().open();
    expect(allSent()).toEqual([]);

    // Open again: a call goes out and is answered as usual.
    const p = api.call<number>('app.overview');
    expect(last().sent.map((m) => m.method)).toEqual(['app.overview']);
    last().answer({ id: last().sent[0].id, result: 3 });
    expect(await p).toBe(3);
  });

  it('a reconnect that fails and one that succeeds replay nothing', async () => {
    const api = createRemoteApi('ws://example.invalid/rpc');
    last().open();
    const out = api.call('teams.agents', { teamId: 1 }).catch((e: Error) => e);
    last().close('gone');
    expect(((await out) as Error).message).toBe('gone'); // sent, unanswered: fails with the close's reason

    const first = await api.call('bus.answer', { id: 7 }).catch((e: Error) => e);
    await vi.advanceTimersByTimeAsync(500);
    const second = await api.call('bus.answer', { id: 7 }).catch((e: Error) => e);
    last().close(); // the reconnect fails
    await vi.advanceTimersByTimeAsync(1000);
    expect(FakeSocket.all).toHaveLength(3);
    last().open(); // this one succeeds
    expect([first, second].map((e) => (e as Error).message)).toEqual([CLOSED, CLOSED]);
    expect(allSent()).toEqual(['teams.agents']);
  });

  it('calls made while the page\'s first connection opens wait for it and are sent once', async () => {
    const api = createRemoteApi('ws://example.invalid/rpc');
    const p = api.call<string>('core.getInfo');
    expect(last().sent).toEqual([]);
    last().open();
    expect(last().sent.map((m) => m.method)).toEqual(['core.getInfo']);
    last().answer({ id: last().sent[0].id, result: 'ok' });
    expect(await p).toBe('ok');
  });

  it('a first connection that fails drops the calls that waited for it: they fail and are never sent', async () => {
    const api = createRemoteApi('ws://example.invalid/rpc');
    const waited = api.call('bus.answer', { id: 7 }).catch((e: Error) => e);
    last().close();
    expect(((await waited) as Error).message).toBe(CLOSED);
    await vi.advanceTimersByTimeAsync(500);
    last().open();
    expect(allSent()).toEqual([]);
  });
});
