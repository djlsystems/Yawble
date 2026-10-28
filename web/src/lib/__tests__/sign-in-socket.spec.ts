import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { connectSignIn, keyInput, mouseInput, pointAt, signInSocketUrl } from '../sign-in-socket';

/** Records every socket opened and what was sent through it, and lets a test speak for the Host. */
class FakeSocket {
  static OPEN = 1;
  static opened: FakeSocket[] = [];

  readonly url: URL;
  readyState = FakeSocket.OPEN;
  binaryType = '';
  sent: string[] = [];
  listeners: Record<string, ((event: unknown) => void)[]> = {};

  constructor(url: URL | string) {
    this.url = new URL(String(url));
    FakeSocket.opened.push(this);
  }

  addEventListener(name: string, listener: (event: unknown) => void) {
    (this.listeners[name] ??= []).push(listener);
  }
  emit(name: string, event: unknown) {
    for (const listener of this.listeners[name] ?? []) listener(event);
  }
  send(frame: string) {
    this.sent.push(frame);
  }
  close() {}
}

beforeEach(() => {
  FakeSocket.opened = [];
  vi.stubGlobal('WebSocket', FakeSocket);
  vi.stubGlobal('window', { location: { href: 'https://harness.example/#/teams/t' } });
});

afterEach(() => vi.unstubAllGlobals());

describe('the sign-in socket', () => {
  it('opens the row’s socket on the same host, each part escaped', () => {
    const url = signInSocketUrl('Sites', 'Creds', 'entries', 'site-admin', 'https://harness.example/console');
    expect(url.toString()).toBe('wss://harness.example/api/teams/Sites/members/Creds/sign-in/entries/site-admin');
    expect(signInSocketUrl('a b', 'm', 'f', 'r', 'http://h/').pathname).toBe('/api/teams/a%20b/members/m/sign-in/f/r');
  });

  it('carries the person’s languages and time zone, so the browser the site sees has their locale', () => {
    const url = signInSocketUrl('T', 'M', 'entries', 'r1', 'https://h/', {
      languages: ['de-DE', 'de', 'en-US'],
      timeZone: 'Europe/Berlin',
    });
    expect(url.searchParams.get('lang')).toBe('de-DE,de,en-US');
    expect(url.searchParams.get('tz')).toBe('Europe/Berlin');
  });

  it('opens the socket with this browser’s own languages and time zone', () => {
    connectSignIn({ team: 'T', member: 'M', field: 'entries', row: 'r1' }, {
      onMessage: () => {},
      onFrame: () => {},
      onClose: () => {},
    });
    const url = FakeSocket.opened[0]!.url;
    expect(url.searchParams.get('lang')).toBe(navigator.languages.join(','));
    expect(url.searchParams.get('tz')).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone);
  });

  it('hands frames and notices apart, and sends done and cancel as commands', () => {
    const messages: unknown[] = [];
    const frames: unknown[] = [];
    const socket = connectSignIn({ team: 'T', member: 'M', field: 'entries', row: 'r1' }, {
      onMessage: (m) => messages.push(m),
      onFrame: (f) => frames.push(f),
      onClose: () => {},
    });
    const fake = FakeSocket.opened[0]!;

    fake.emit('message', { data: '{"kind":"page","url":"https://site.example/"}' });
    fake.emit('message', { data: new Blob(['jpeg']) });
    fake.emit('message', { data: 'not json' });
    expect(messages).toEqual([{ kind: 'page', url: 'https://site.example/' }]);
    expect(frames).toHaveLength(1);

    socket.done();
    socket.cancel();
    expect(fake.sent).toEqual(['{"type":"done"}', '{"type":"cancel"}']);
  });

  it('scales a point on the shown screen to the browser’s own pixels, inside it', () => {
    const rect = { left: 10, top: 20, width: 640, height: 400 };
    expect(pointAt(330, 220, rect, { width: 1280, height: 800 })).toEqual({ x: 640, y: 400 });
    expect(pointAt(0, 9999, rect, { width: 1280, height: 800 })).toEqual({ x: 0, y: 800 });
  });

  it('sends a click with its button and count, and a move with none', () => {
    const rect = { left: 0, top: 0, width: 1280, height: 800 };
    const base = { clientX: 5, clientY: 6, button: 2, detail: 2, altKey: false, ctrlKey: false, metaKey: false, shiftKey: true };
    expect(mouseInput('mousePressed', base, rect, { width: 1280, height: 800 })).toEqual({
      type: 'mouse', event: 'mousePressed', x: 5, y: 6, button: 'right', clickCount: 2, modifiers: 8,
    });
    expect(mouseInput('mouseMoved', base, rect, { width: 1280, height: 800 })).toMatchObject({ button: 'none', clickCount: 0 });
  });

  it('types a printable key, and sends Ctrl or a named key without text', () => {
    const plain = { key: 'a', code: 'KeyA', keyCode: 65, altKey: false, ctrlKey: false, metaKey: false, shiftKey: false };
    expect(keyInput('keyDown', plain)).toEqual({ type: 'key', event: 'keyDown', key: 'a', code: 'KeyA', keyCode: 65, text: 'a', modifiers: 0 });
    expect(keyInput('keyUp', plain)).not.toHaveProperty('text');
    expect(keyInput('keyDown', { ...plain, ctrlKey: true })).not.toHaveProperty('text');
    expect(keyInput('keyDown', { ...plain, key: 'Tab', code: 'Tab', keyCode: 9 })).not.toHaveProperty('text');
    expect(keyInput('keyDown', { ...plain, key: 'Enter', code: 'Enter', keyCode: 13 })).toMatchObject({ text: '\r' });
  });
});
