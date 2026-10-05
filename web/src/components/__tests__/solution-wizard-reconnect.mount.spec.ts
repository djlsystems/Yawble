// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, A CONNECTION WITHOUT THE SCOPES ITS SLOT NEEDS. Your part says so before the
// install - which scopes, in words - and offers Reconnect for that connection with exactly the
// missing scopes. The sign-in opens in another tab, so the wizard is still here when it comes back;
// the reconnected connection is then the binding and the warning goes. An install the Host refuses
// for missing scopes offers the same Reconnect, and Install can be pressed again without starting
// over. A sign-in that comes back with fewer scopes than asked says so, and the binding stays refused.
//
// A FAKE HOST at `fetch`. No provider is called: the other tab is played by the provider-return
// channel itself, and every address here is an example one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

vi.mock('../../lib/browserNavigation', () => ({ goTo: vi.fn() }));

import SolutionWizard from '../SolutionWizard.vue';
import { goTo } from '../../lib/browserNavigation';
import { announceProviderReturn, leaveForProvider } from '../../lib/providerReturn';
import type { CallbackOutcome } from '../../lib/connections';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, hostPlan, reply, sent, steps, wizardRoutes, type Call } from '../../test/solutionFixtures';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import type { Connection, SolutionPlan } from '../../api/types';

const read = 'https://www.googleapis.com/auth/gmail.readonly';
const compose = 'https://www.googleapis.com/auth/gmail.compose';

function googlePlan(): SolutionPlan {
  const plan = hostPlan();
  plan.personConnections = [
    {
      member: 'Scout',
      slot: 'mail',
      description: 'Where postings are emailed from.',
      required: true,
      plugin: 'job-board',
      providers: ['google'],
      scopes: { google: [read, compose] },
    },
  ];
  return plan;
}

const short = hostConnection({ id: 'conn-short', provider: 'google', name: 'dana@example.test', account: 'dana@example.test', scopes: ['openid', 'email'] });
const full = hostConnection({ id: 'conn-full', provider: 'google', name: 'Work', account: 'work@example.test', scopes: ['openid', read, compose] });

const flow = {
  authorizationUrl: 'https://accounts.example.test/o/oauth2/auth?state=fake',
  state: 'fake',
  redirectUri: 'http://localhost:8080/api/connections/callback',
  expiresAt: '2026-10-05T10:10:00Z',
};

const refusalSentence =
  "'Scout': Connection 'dana@example.test' was not granted the scopes `" + read + '`, `' + compose +
  "` that slot `mail` needs. Press Reconnect to grant those scopes.";

let calls: Call[] = [];
/** What GET /api/connections answers now: the other tab's sign-in changes it. */
let listed: Connection[] = [];
/** The install's answers, in turn. */
let installs: Response[] = [];

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  listed = [short, full];
  installs = [];
  vi.mocked(goTo).mockClear();
  const plan = googlePlan();
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.url === '/api/solutions/install'
              ? installs.shift() ??
                reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          (call) =>
            call.method === 'GET' && call.url.startsWith('/api/connections/providers')
              ? reply(200, [hostProvider({ id: 'google', clientId: 'fake.apps.googleusercontent.com', configured: true }), hostProvider({ id: 'microsoft' })])
              : undefined,
          (call) => (call.method === 'GET' && call.url === '/api/connections' ? reply(200, listed) : undefined),
          (call) => (call.method === 'POST' && call.url === '/api/connections/start' ? reply(200, flow) : undefined),
          (call) => {
            if (call.method !== 'POST' || call.url !== '/api/solutions/preview') return undefined;
            return reply(200, {
              ok: true,
              mode: 'install',
              teamName: 'Job Tracker',
              nameRefusal: null,
              plan,
              connections: [short, full].map(({ id, name, provider, account, status }) => ({ id, name, provider, account, status })),
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
  vi.unstubAllGlobals();
  sessionStorage.clear();
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

const picker = (wrapper: VueWrapper) =>
  wrapper.findAllComponents(QSelect).find((select) => select.props('label') === 'Connection for Scout: mail')!;

async function choose(wrapper: VueWrapper, id: string) {
  picker(wrapper).vm.$emit('update:modelValue', id);
  await settle();
}

const input = () => bodyFind('[data-connection-input="Scout/mail"]')!;

function buttonIn(root: Element | null, label: string): HTMLElement | undefined {
  return [...(root?.querySelectorAll('button') ?? [])].find((candidate) => candidate.textContent?.trim() === label) as
    | HTMLElement
    | undefined;
}

async function click(element: Element | null | undefined) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

/** The other tab: opened from the link, sent on to the provider, and back with `outcome`. */
async function otherTab(href: string, outcome: CallbackOutcome) {
  const tag = new URLSearchParams(href.slice(href.indexOf('?'))).get('signin')!;
  const went = await leaveForProvider(tag);
  expect(await announceProviderReturn(outcome)).toBe(true);
  await new Promise((resolve) => setTimeout(resolve, 20));
  await settle();
  return went;
}

describe('Solution wizard - a connection without its slot\'s scopes, on Your part', () => {
  it('names the missing scopes and offers Reconnect; a connection holding them shows neither', async () => {
    const wrapper = await yourPart();

    await choose(wrapper, 'conn-short');
    const refusal = input().querySelector('[data-binding-refusal]');
    expect(refusal?.textContent).toContain(read);
    expect(refusal?.textContent).toContain(compose);
    expect(refusal?.textContent).toContain('Reconnect');
    expect(refusal?.textContent).not.toContain('Admin → Connections');
    expect(buttonIn(input(), 'Reconnect')).toBeDefined();

    await choose(wrapper, 'conn-full');
    expect(input().querySelector('[data-binding-refusal]')).toBeNull();
    expect(buttonIn(input(), 'Reconnect')).toBeUndefined();
  });

  it('reconnects that connection in another tab with exactly the missing scopes, then binds it with no warning', async () => {
    const wrapper = await yourPart();
    await choose(wrapper, 'conn-short');

    await click(buttonIn(input(), 'Reconnect'));

    expect(sent(calls, 'POST', '/api/connections/start').map((call) => call.body)).toEqual([
      { reconnectId: 'conn-short', scopes: [read, compose] },
    ]);
    // The wizard is not left: the provider opens in another tab.
    expect(goTo).not.toHaveBeenCalled();
    const link = input().querySelector('a[data-reconnect-link]');
    expect(link?.getAttribute('target')).toBe('_blank');

    listed = [{ ...short, scopes: ['openid', 'email', read, compose] }, full];
    const went = await otherTab(link!.getAttribute('href')!, { outcome: 'reconnected', id: 'conn-short' });

    expect(went).toBe(flow.authorizationUrl);
    expect(picker(wrapper).props('modelValue')).toBe('conn-short');
    expect(input().querySelector('[data-binding-refusal]')).toBeNull();
    expect(buttonIn(input(), 'Reconnect')).toBeUndefined();

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();
    expect((sent(calls, 'POST', '/api/solutions/install')[0]!.body as { connections: unknown }).connections).toEqual({
      Scout: { mail: 'conn-short' },
    });
  });

  it('says so when the sign-in comes back with fewer scopes than asked, and the binding stays refused', async () => {
    const wrapper = await yourPart();
    await choose(wrapper, 'conn-short');
    await click(buttonIn(input(), 'Reconnect'));
    const link = input().querySelector('a[data-reconnect-link]')!;

    // The person unticked the compose scope at the provider.
    listed = [{ ...short, scopes: ['openid', 'email', read] }, full];
    await otherTab(link.getAttribute('href')!, { outcome: 'reconnected', id: 'conn-short' });

    const shortfall = input().querySelector('[data-reconnect-short]');
    expect(shortfall?.textContent).toContain('came back without');
    expect(shortfall?.textContent).toContain(compose);
    expect(shortfall?.textContent).not.toContain(read);
    const refusal = input().querySelector('[data-binding-refusal]');
    expect(refusal?.textContent).toContain(compose);
    expect(buttonIn(input(), 'Reconnect')).toBeDefined();
  });
});

describe('Solution wizard - an install refused for missing scopes', () => {
  it('offers Reconnect for that connection and those scopes, and Install can be pressed again', async () => {
    installs = [reply(400, { error: refusalSentence, reconnect: { connectionId: 'conn-short', scopes: [read, compose] } })];
    const wrapper = await yourPart();
    await choose(wrapper, 'conn-short');
    button('Next').click();
    await settle();

    button('Install').click();
    await settle();

    const step = bodyFind('[data-step="install"]')!;
    expect(step.querySelector('[data-install-error]')?.textContent).toContain('Press Reconnect');
    await click(buttonIn(step, 'Reconnect'));
    expect(sent(calls, 'POST', '/api/connections/start').map((call) => call.body)).toEqual([
      { reconnectId: 'conn-short', scopes: [read, compose] },
    ]);
    const link = step.querySelector('a[data-reconnect-link]')!;

    listed = [{ ...short, scopes: ['openid', 'email', read, compose] }, full];
    await otherTab(link.getAttribute('href')!, { outcome: 'reconnected', id: 'conn-short' });

    expect(bodyFind('[data-install-error]')).toBeNull();
    expect(bodyFind('[data-install-retry]')?.textContent).toContain('Press Install again');

    button('Install').click();
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(2);
    expect(bodyFind('[data-install-success]')).not.toBeNull();
    wrapper.unmount();
  });

  it('keeps the refusal when the sign-in comes back without every scope', async () => {
    installs = [reply(400, { error: refusalSentence, reconnect: { connectionId: 'conn-short', scopes: [read, compose] } })];
    const wrapper = await yourPart();
    await choose(wrapper, 'conn-short');
    button('Next').click();
    await settle();
    button('Install').click();
    await settle();

    const step = bodyFind('[data-step="install"]')!;
    await click(buttonIn(step, 'Reconnect'));
    listed = [{ ...short, scopes: ['openid', 'email'] }, full];
    await otherTab(step.querySelector('a[data-reconnect-link]')!.getAttribute('href')!, { outcome: 'reconnected', id: 'conn-short' });

    expect(bodyFind('[data-install-error]')).not.toBeNull();
    expect(bodyFind('[data-reconnect-short]')?.textContent).toContain(compose);
    expect(bodyFind('[data-install-retry]')).toBeNull();
    wrapper.unmount();
  });
});
