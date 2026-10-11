// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD'S YOUR PART: each connection input has Connect <Provider> beside its picker,
// opening Add connection started from that member's slot - the providers and scopes the plan's
// personConnections name, since a bundled plugin is not installed before the install and neither the
// installed plugins nor the needs read know its slots. The member does not exist yet, so a completed
// sign-in only selects the new connection in that picker; the install binds it, exactly as a
// connection chosen there is bound.
//
// A FAKE HOST at `fetch`. No provider is called: the sign-in is Microsoft's sign-in with a code,
// faked at the flow read. Every code here is an obviously fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, reply, sent, steps, wizardRoutes, type Call } from '../../test/solutionFixtures';
import { hostConnection, hostList, hostPlugin, hostProvider, hostSlot } from '../../test/pluginFixtures';

const mailSend = 'https://graph.microsoft.com/Mail.Send';

const jobBoard = hostPlugin({
  id: 'job-board',
  version: '0.2.0',
  connections: { mail: hostSlot({ providers: ['microsoft'], scopes: { microsoft: [mailSend] }, required: true }) },
});

const fresh = hostConnection({ id: 'conn-new', provider: 'microsoft', name: 'Dana', account: 'dana@example.test', scopes: [mailSend] });

let calls: Call[] = [];
let signedIn = false;
/** Whether the package's plugin is already installed on the Host. */
let installed = true;

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  signedIn = false;
  installed = true;
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.url === '/api/solutions/install'
              ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          (call) => (call.method === 'GET' && call.url === '/api/plugins' ? reply(200, hostList(installed ? [jobBoard] : [])) : undefined),
          (call) =>
            call.method === 'GET' && call.url.startsWith('/api/connections/providers')
              ? reply(200, [hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft', clientId: '00000000-0000-0000-0000-00000000c1d0', configured: true })])
              : undefined,
          (call) =>
            call.method === 'GET' && call.url.startsWith('/api/connections/needs?')
              ? reply(
                  200,
                  installed
                    ? {
                        provider: 'microsoft',
                        needs: [{ plugin: 'job-board', slot: 'mail', description: null, scopes: [mailSend] }],
                        scopes: [{ scope: mailSend, words: 'Send mail as you', plugins: ['Job Board (sample)'] }],
                        apis: [],
                      }
                    : { provider: 'microsoft', needs: [], scopes: [], apis: [] },
                )
              : undefined,
          (call) => (call.method === 'GET' && call.url === '/api/connections/flows/open' ? reply(200, []) : undefined),
          (call) =>
            call.method === 'POST' && call.url === '/api/connections/start'
              ? reply(200, {
                  flowId: 'flow-1',
                  userCode: 'FAKE-CODE',
                  verificationUri: 'https://microsoft.example.test/devicelogin',
                  expiresAt: '2026-10-04T10:15:00Z',
                })
              : undefined,
          (call) => {
            if (call.method !== 'GET' || call.url !== '/api/connections/flows/flow-1') return undefined;
            signedIn = true;
            return reply(200, { state: 'done', sentence: 'Signed in.', connection: fresh });
          },
          (call) => (call.method === 'GET' && call.url === '/api/connections' ? reply(200, signedIn ? [fresh] : []) : undefined),
          ...wizardRoutes(),
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

async function yourPart() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  for (let index = 0; index < 2; index++) {
    button('Next').click();
    await settle();
  }
  expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
  return wrapper;
}

async function settled() {
  if (!vi.isFakeTimers()) return settle();
  await flushPromises();
  await vi.advanceTimersByTimeAsync(0);
  await flushPromises();
}

async function click(element: Element | null | undefined) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settled();
}

const picker = (wrapper: VueWrapper) =>
  wrapper.findAllComponents(QSelect).find((select) => select.props('label') === 'Connection for Scout: mail')!;

async function signIn() {
  vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
  await click(button('Sign in with Microsoft'));
  await vi.advanceTimersByTimeAsync(3000);
  await settled();
  vi.useRealTimers();
}

describe('Solution wizard - Connect beside a connection input', () => {
  it("takes a bundled plugin's slot from the plan: its providers and scopes, before anything is installed", async () => {
    installed = false;
    const wrapper = await yourPart();

    const connect = bodyFind('[data-connection-input="Scout/mail"] [data-slot-connect]');
    expect(connect?.textContent).toContain('Connect Microsoft');
    await click(connect?.querySelector('button'));
    expect(bodyFind('[data-guided-connect] [data-connect-step]')?.getAttribute('data-connect-step')).toBe('signin');

    await signIn();

    // The sign-in asked for the slot's scope, though no installed plugin wants it yet.
    expect((sent(calls, 'POST', '/api/connections/start')[0]!.body as { scopes: string[] }).scopes).toEqual([mailSend]);
    await click(button('Finish'));
    expect(picker(wrapper).props('modelValue')).toBe('conn-new');

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();
    expect((sent(calls, 'POST', '/api/solutions/install')[0]!.body as { connections: unknown }).connections).toEqual({
      Scout: { mail: 'conn-new' },
    });
  });

  it("opens Add connection for that member's slot, selects the new connection, and the install binds it", async () => {
    const wrapper = await yourPart();

    const connect = bodyFind('[data-connection-input="Scout/mail"] [data-slot-connect]');
    expect(connect?.textContent).toContain('Connect Microsoft');
    await click(connect?.querySelector('button'));

    expect(sent(calls, 'GET', '/api/connections/needs?provider=microsoft&plugin=job-board&slot=mail')).toHaveLength(1);
    expect(bodyFind('[data-guided-connect] [data-connect-step]')?.getAttribute('data-connect-step')).toBe('signin');

    vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    await click(button('Sign in with Microsoft'));
    await vi.advanceTimersByTimeAsync(3000);
    await settled();
    vi.useRealTimers();

    expect(bodyFind('[data-guided-connect] [data-connect-step]')?.getAttribute('data-connect-step')).toBe('result');
    await click(button('Finish'));

    expect(picker(wrapper).props('modelValue')).toBe('conn-new');
    expect((picker(wrapper).props('options') as { value: string }[]).map((option) => option.value)).toContain('conn-new');
    // Nothing is bound yet: there is no member until the install makes it.
    expect(calls.some((call) => call.url.includes('/plugin-settings'))).toBe(false);

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();

    expect((sent(calls, 'POST', '/api/solutions/install')[0]!.body as { connections: unknown }).connections).toEqual({
      Scout: { mail: 'conn-new' },
    });
  });
});
