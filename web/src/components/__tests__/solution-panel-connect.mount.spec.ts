// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S "CONNECT X'S SLOT" FIX: beside the picker, Connect <Provider> opens Add
// connection started from that member's slot - its providers and scopes - and a completed sign-in
// binds the new connection through the member's own settings route, keeping its other settings.
//
// A FAKE HOST at `fetch`. No provider is called: the sign-in is Microsoft's sign-in with a code,
// faked at the flow read. Every code here is an obviously fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call } from '../../test/solutionFixtures';
import { panelRead, panelRoutes, scoutSettings } from '../../test/solutionPanelFixtures';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';

const mailSend = 'https://graph.microsoft.com/Mail.Send';
const fresh = hostConnection({ id: 'conn-new', provider: 'microsoft', name: 'Dana', account: 'dana@example.test', scopes: [mailSend] });

let calls: Call[] = [];
let signedIn = false;
/** The mailbox slot's providers and scopes. */
let mailbox: { providers: string[]; scopes: Record<string, string[]> };

beforeEach(() => {
  calls = [];
  signedIn = false;
  mailbox = { providers: ['microsoft'], scopes: { microsoft: [mailSend] } };
  const settings = () =>
    scoutSettings({
      connectionFields: {
        mailbox: {
          description: 'Where postings are emailed from.',
          ...mailbox,
          required: true,
          summary: 'needs a connection',
        },
      },
    });
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.url === '/api/teams/job-tracker/members/scout/plugin-settings'
              ? call.method === 'PUT'
                ? reply(200, { ...settings(), connections: { mailbox: 'conn-new' } })
                : reply(200, settings())
              : undefined,
          (call) =>
            call.method === 'GET' && call.url.startsWith('/api/connections/providers')
              ? reply(200, [hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft', clientId: '00000000-0000-0000-0000-00000000c1d0', configured: true })])
              : undefined,
          (call) =>
            call.method === 'GET' && call.url.startsWith('/api/connections/needs?')
              ? reply(200, {
                  provider: 'microsoft',
                  needs: [{ plugin: 'job-board', slot: 'mailbox', description: null, scopes: [mailSend] }],
                  scopes: [{ scope: mailSend, words: 'Send mail as you', plugins: ['Job Board'] }],
                  apis: [],
                })
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
          ...panelRoutes(() => panelRead()),
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

describe("the control panel's connection fix", () => {
  it('offers Connect <Provider> that opens Add connection for that slot, and binds the new connection on completion', async () => {
    const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    const connect = bodyFind('[data-blocked="mailbox"] [data-fix-connection] [data-slot-connect]');
    expect(connect?.textContent).toContain('Connect Microsoft');
    await click(connect?.querySelector('button'));

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(sent(calls, 'GET', '/api/connections/needs?provider=microsoft&plugin=job-board&slot=mailbox')).toHaveLength(1);
    expect(bodyFind('[data-guided-connect] [data-connect-step]')?.getAttribute('data-connect-step')).toBe('signin');

    vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    await click(button('Sign in with Microsoft'));
    await vi.advanceTimersByTimeAsync(3000);
    await settled();
    vi.useRealTimers();

    const put = sent(calls, 'PUT', '/api/teams/job-tracker/members/scout/plugin-settings');
    expect(put).toHaveLength(1);
    expect(put[0]!.body).toEqual({
      config: { keywords: ['dotnet'], region: 'EU' },
      secrets: { apiKey: 'JOB_BOARD_KEY' },
      connections: { mailbox: 'conn-new' },
    });

    wrapper.unmount();
  });

  it("says where a custom provider's connection is made, with no Connect to offer", async () => {
    mailbox = { providers: ['custom-crm'], scopes: { 'custom-crm': ['read'] } };
    const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    const fix = bodyFind('[data-blocked="mailbox"] [data-fix-connection]');
    expect(fix?.querySelector('[data-slot-connect]')).toBeNull();
    expect(fix?.textContent).toContain('No account yet? Connect one in Admin → Connections.');

    wrapper.unmount();
  });
});
