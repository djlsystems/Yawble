import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { connectConcierge } from '../concierge-socket';

/** Records every socket opened, with the URL it was opened on and the frames sent through it. */
class FakeSocket {
  static OPEN = 1;
  static opened: FakeSocket[] = [];

  readonly url: URL;
  readyState = FakeSocket.OPEN;
  binaryType = '';
  sent: unknown[] = [];

  constructor(url: URL | string) {
    this.url = new URL(String(url));
    FakeSocket.opened.push(this);
  }

  addEventListener() {}
  send(frame: unknown) {
    this.sent.push(frame);
  }
  close() {}
}

const handlers = { onOpen: () => {}, onData: () => {}, onClose: () => {} };

beforeEach(() => {
  FakeSocket.opened = [];
  vi.stubGlobal('WebSocket', FakeSocket);
  vi.stubGlobal('window', { location: { href: 'https://harness.example/console' } });
});

afterEach(() => vi.unstubAllGlobals());

describe('connectConcierge', () => {
  it('opens the per-person socket, which names no team, with the geometry on the connect', () => {
    connectConcierge({ cols: 120, rows: 40 }, handlers);

    const { url } = FakeSocket.opened[0]!;
    expect(url.protocol).toBe('wss:');
    expect(url.pathname).toBe('/api/concierge/ws');
    expect(url.searchParams.get('cols')).toBe('120');
    expect(url.searchParams.get('rows')).toBe('40');
  });

  it('sends a keystroke as binary and a resize as control JSON', () => {
    const socket = connectConcierge({ cols: 80, rows: 24 }, handlers);
    socket.sendInput('a');
    socket.sendResize(100, 30);

    const [keystroke, resize] = FakeSocket.opened[0]!.sent;
    expect(keystroke).toBeInstanceOf(Uint8Array);
    expect(JSON.parse(resize as string)).toEqual({ type: 'resize', cols: 100, rows: 30 });
  });
});
