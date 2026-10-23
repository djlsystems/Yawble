// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, A MICROSOFT CONNECTION WITHOUT THE SCOPES ITS SLOT NEEDS. Microsoft signs in
// only with a code, so Your part's Reconnect asks the Host for one (`flow: "device"`, the
// connection's ID, the missing scopes) and shows it in place - no other tab, no trip away - so the
// person's answers are kept. Once approved the connection is read again and stays the binding, and
// the warning goes.
//
// A FAKE HOST at `fetch`. No provider is called, and every code and address here is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

vi.mock('../../lib/browserNavigation', () => ({ goTo: vi.fn() }));

import SolutionWizard from '../SolutionWizard.vue';
import { goTo } from '../../lib/browserNavigation';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, hostPlan, reply, sent, wizardRoutes, type Call } from '../../test/solutionFixtures';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import type { Connection, ConnectionFlow, SolutionPlan } from '../../api/types';

const send = 'https://graph.microsoft.com/Mail.Send';

function microsoftPlan(): SolutionPlan {
  const plan = hostPlan();
  plan.personConnections = [
    {
      member: 'Scout',
      slot: 'mail',
      description: 'Where postings are emailed from.',
      required: true,
      plugin: 'job-board',
      providers: ['microsoft'],
      scopes: { microsoft: [send] },
    },
  ];
  return plan;
}

const short = hostConnection({ id: 'conn-ms', provider: 'microsoft', name: 'Office', account: 'person@example.test', scopes: ['openid', 'email'] });

let calls: Call[] = [];
let listed: Connection[] = [];
let flowRead: ConnectionFlow = { state: 'waiting', sentence: 'Waiting for you to sign in.' };

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  listed = [short];
  flowRead = { state: 'waiting', sentence: 'Waiting for you to sign in.' };
  vi.mocked(goTo).mockClear();
  const plan = microsoftPlan();
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.method === 'GET' && call.url.startsWith('/api/connections/providers')
              ? reply(200, [hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft', configured: true, clientSource: 'default' })])
              : undefined,
          (call) => (call.method === 'GET' && call.url === '/api/connections' ? reply(200, listed) : undefined),
          (call) =>
            call.method === 'POST' && call.url === '/api/connections/start'
              ? reply(200, { flowId: 'flow-1', userCode: 'FAKE-CODE-ONE', verificationUri: 'https://login.example.test/device', expiresAt: '2026-10-04T10:15:00Z' })
              : undefined,
          (call) => (call.method === 'GET' && call.url === '/api/connections/flows/flow-1' ? reply(200, flowRead) : undefined),
          (call) => {
            if (call.method !== 'POST' || call.url !== '/api/solutions/preview') return undefined;
            return reply(200, {
              ok: true,
              mode: 'install',
              teamName: 'Job Tracker',
              nameRefusal: null,
              plan,
              connections: [short].map(({ id, name, provider, account, status }) => ({ id, name, provider, account, status })),
              secrets: [],
            });
          },
          ...wizardRoutes({ plan }),
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  sessionStorage.clear();
  resetBody();
});

async function settled() {
  if (!vi.isFakeTimers()) return settle();
  await flushPromises();
  await vi.advanceTimersByTimeAsync(0);
  await flushPromises();
}

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

const picker = (wrapper: VueWrapper) =>
  wrapper.findAllComponents(QSelect).find((select) => select.props('label') === 'Connection for Scout: mail')!;

const input = () => bodyFind('[data-connection-input="Scout/mail"]')!;

function reconnectIn(root: Element): HTMLElement {
  const found = [...root.querySelectorAll('button')].find((candidate) => candidate.textContent?.trim() === 'Reconnect');
  if (!found) throw new Error('no Reconnect');
  return found as HTMLElement;
}

describe('Solution wizard - Reconnect on a Microsoft connection', () => {
  it('shows the code in place, keeps the wizard where it is, and binds the connection once approved', async () => {
    const wrapper = await yourPart();
    picker(wrapper).vm.$emit('update:modelValue', 'conn-ms');
    await settle();
    expect(input().querySelector('[data-binding-refusal]')?.textContent).toContain(send);

    vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    reconnectIn(input()).click();
    await settled();

    expect(sent(calls, 'POST', '/api/connections/start').map((call) => call.body)).toEqual([
      { reconnectId: 'conn-ms', scopes: [send], flow: 'device' },
    ]);
    // In place: no trip away, and no other tab to open.
    expect(goTo).not.toHaveBeenCalled();
    expect(input().querySelector('a[data-reconnect-link]')).toBeNull();
    expect(input().querySelector('[data-device-code]')?.textContent).toContain('FAKE-CODE-ONE');
    expect(input().querySelector('a[data-device-link]')?.getAttribute('href')).toBe('https://login.example.test/device');
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();

    listed = [{ ...short, scopes: ['openid', 'email', send] }];
    flowRead = { state: 'done', sentence: 'Reconnected.', connection: listed[0]! };
    await vi.advanceTimersByTimeAsync(3_000);
    await settled();

    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
    expect(picker(wrapper).props('modelValue')).toBe('conn-ms');
    expect(input().querySelector('[data-binding-refusal]')).toBeNull();
    expect(goTo).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
