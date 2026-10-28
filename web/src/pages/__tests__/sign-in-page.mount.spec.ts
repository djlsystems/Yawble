// @vitest-environment happy-dom
//
// The sign-in page opens its row's socket, sends the person's input only while the browser is live,
// and Done asks the Host to keep the session: the page itself only ever hears the hosts kept.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';

import SignInPage from '../SignInPage.vue';
import { resetBody } from '../../test/mountQuasar';
import { button, isDisabled, settle } from '../../test/formProbe';

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
  say(message: object) {
    for (const listener of this.listeners.message ?? []) listener({ data: JSON.stringify(message) });
  }
  frame(jpeg: Blob) {
    for (const listener of this.listeners.message ?? []) listener({ data: jpeg });
  }
  send(frame: string) {
    this.sent.push(frame);
  }
  close() {}
}

beforeEach(() => {
  FakeSocket.opened = [];
  vi.stubGlobal('WebSocket', FakeSocket);
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  resetBody();
});

async function open() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/teams/:team/members/:member/sign-in/:field/:row', component: SignInPage }],
  });
  mount({ template: '<router-view />' }, { attachTo: document.body, global: { plugins: [router] } });
  await router.push('/teams/Sites/members/Creds/sign-in/entries/site-admin');
  await router.isReady();
  await settle();
  return FakeSocket.opened[0]!;
}

describe('the sign-in page', () => {
  it('opens the row’s socket and keeps Done off until the browser is live', async () => {
    const socket = await open();

    expect(socket.url.pathname).toBe('/api/teams/Sites/members/Creds/sign-in/entries/site-admin');
    expect(isDisabled('Done')).toBe(true);

    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    socket.say({ kind: 'page', url: 'https://site.example/login', title: 'Log in' });
    await settle();

    expect(isDisabled('Done')).toBe(false);
    expect(document.body.textContent).toContain('Log in');
  });

  it('sends done, shows the hosts kept and then that it is over', async () => {
    const socket = await open();
    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    await settle();

    button('Done').click();
    await settle();
    expect(socket.sent).toContain('{"type":"done"}');

    socket.say({ kind: 'saved', hosts: ['site.example', 'accounts.google.com'], providers: ['accounts.google.com'] });
    socket.say({ kind: 'ended', reason: 'Signed in: the session was saved and the browser closed.' });
    await settle();

    expect(document.body.textContent).toContain('Saved the session for site.example, accounts.google.com.');
    expect(document.body.querySelector('.sign-in-done')).toBeNull();
  });

  it('draws each frame on a canvas, never through a blob: URL the page’s CSP (img-src self) blocks', async () => {
    const drawn: unknown[] = [];
    const bitmap = { width: 1280, height: 800, close: vi.fn() };
    vi.stubGlobal('createImageBitmap', vi.fn(async () => bitmap));
    const createObjectURL = vi.fn(() => 'blob:http://localhost/frame');
    vi.stubGlobal('URL', Object.assign(class extends URL {}, { createObjectURL, revokeObjectURL: vi.fn() }));
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(
      () => ({ drawImage: (image: unknown) => drawn.push(image) }) as never,
    );

    const socket = await open();
    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    await settle();
    socket.frame(new Blob([new Uint8Array([0xff, 0xd8, 0xff, 0xe0])], { type: 'image/jpeg' }));
    await settle();

    expect(createObjectURL).not.toHaveBeenCalled();
    expect(document.body.querySelector('img[src^="blob:"]')).toBeNull();
    expect(drawn).toEqual([bitmap]);
    const canvas = document.body.querySelector<HTMLCanvasElement>('canvas.sign-in-frame')!;
    expect([canvas.width, canvas.height]).toEqual([1280, 800]);
    expect(document.body.textContent).not.toContain('Starting the browser');
  });

  it('shows why the browser ended: replaced by another sign-in, or idle', async () => {
    const socket = await open();
    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    socket.say({ kind: 'ended', reason: 'You opened Sign in somewhere else, so this browser was closed. Nothing was saved here.' });
    for (const listener of socket.listeners.close ?? []) listener({});
    await settle();

    expect(document.body.textContent).toContain('You opened Sign in somewhere else');
    expect(document.body.textContent).not.toContain('The browser closed. Nothing was saved.');
  });

  it('says why a host was not opened', async () => {
    const socket = await open();
    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    socket.say({ kind: 'refused', host: 'evil.example' });
    await settle();

    expect(document.body.textContent).toContain('evil.example is not this entry');
  });

  it('shows one note at a time: a repeated refusal is not repeated, a newer note replaces it, and it can be dismissed', async () => {
    const socket = await open();
    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    socket.say({ kind: 'refused', host: 'evil.example' });
    socket.say({ kind: 'refused', host: 'evil.example' });
    socket.say({ kind: 'refused', host: 'evil.example' });
    await settle();

    expect(document.body.querySelectorAll('.sign-in-note')).toHaveLength(1);

    socket.say({ kind: 'provider', host: 'accounts.example' });
    socket.say({ kind: 'refused', host: 'far.example' });
    await settle();
    const notes = document.body.querySelectorAll('.sign-in-note');
    expect(notes).toHaveLength(1);
    expect(notes[0]!.textContent).toContain('far.example is not this entry');
    expect(document.body.textContent).not.toContain('evil.example');

    button('Dismiss').click();
    await settle();
    expect(document.body.querySelectorAll('.sign-in-note')).toHaveLength(0);
  });

  it('says plainly why a row can’t be signed in to, without “Starting the browser…”', async () => {
    const socket = await open();
    socket.say({ kind: 'ended', cause: 'refused', reason: "'site-admin' can't be signed in to: it is switched off. Turn Active on in the member's settings first." });
    for (const listener of socket.listeners.close ?? []) listener({});
    await settle();

    expect(document.body.textContent).toContain("can't be signed in to: it is switched off");
    expect(document.body.textContent).not.toContain('Starting the browser');
    expect(document.body.textContent).not.toContain('Nothing was saved');
  });
});
