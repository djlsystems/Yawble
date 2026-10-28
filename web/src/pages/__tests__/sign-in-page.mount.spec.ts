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

  it('says why a host was not opened', async () => {
    const socket = await open();
    socket.say({ kind: 'started', width: 1280, height: 800, site: 'https://site.example' });
    socket.say({ kind: 'refused', host: 'evil.example' });
    await settle();

    expect(document.body.textContent).toContain('evil.example is not this entry');
  });
});
