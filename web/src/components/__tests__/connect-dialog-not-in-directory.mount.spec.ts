// @vitest-environment happy-dom
//
// ADD CONNECTION, MICROSOFT, AN ACCOUNT NOT IN THE DIRECTORY THE SIGN-IN USED. Before signing in, the
// Sign in step and the Directory field's help say what Microsoft's page shows for such an account and
// that Directory is where to name its directory. A refusal Microsoft gives for it reads as one sentence
// (`NotInDirectorySentence`), never its code or its own words; a code that ran out says where to name
// the directory on a line of its own after the expiry sentence; coming back from a browser sign-in
// refused for it says the same sentence.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every code and ID here is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
  renameConnection,
  saveConnectionProvider,
  goTo,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  getConnectionNeeds: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  renameConnection: vi.fn(),
  saveConnectionProvider: vi.fn(),
  goTo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
  renameConnection,
  saveConnectionProvider,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionGuide, ConnectionNeeds } from '../../api/types';
import { ExpiredNotInDirectoryLine, NotInDirectorySentence, UsernameIncorrectHint } from '../../lib/connections';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostProvider } from '../../test/pluginFixtures';
import { button, settle } from '../../test/formProbe';

const mailRead = 'https://graph.microsoft.com/Mail.Read';

const needs: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailRead] }],
  scopes: [{ scope: mailRead, words: 'Read your mail', plugins: ['mail-helper'] }],
  apis: [],
};

const guide: ConnectionGuide = {
  steps: [
    { id: 'register', title: 'Register an app', text: 'Register a new application.', link: null, copy: [] },
    { id: 'credentials', title: 'Paste the Application (client) ID', text: 'Paste it here.', link: null, copy: [] },
  ],
};

const google = hostProvider({ id: 'google' });
const microsoftDefault = hostProvider({ id: 'microsoft', clientSource: 'default', configured: true, tenant: 'common', guide });

beforeEach(() => {
  for (const mock of [
    listConnections,
    listConnectionProviders,
    getConnectionNeeds,
    startConnection,
    startDeviceConnection,
    getConnectionFlow,
    listOpenConnectionFlows,
    listMailboxPresets,
    renameConnection,
    saveConnectionProvider,
    goTo,
  ]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([google, microsoftDefault]);
  getConnectionNeeds.mockResolvedValue(needs);
  startDeviceConnection.mockResolvedValue({
    flowId: 'flow-1',
    userCode: 'FAKE-CODE',
    verificationUri: 'https://microsoft.example.test/devicelogin',
    expiresAt: '2026-10-04T10:15:00Z',
  });
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

async function settled() {
  if (!vi.isFakeTimers()) return settle();
  await flushPromises();
  await vi.advanceTimersByTimeAsync(0);
  await flushPromises();
}

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settled();
}

const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;
const stepList = () => bodyFind('.connect-steps')?.textContent ?? '';

async function chooseMicrosoft() {
  await mountDialog(ConnectionsDialog, {});
  await settle();
  await click(button('Add connection'));
  await click(bodyFind('[data-connect-service="microsoft"]'));
  await click(button('Next'));
}


const expiry = 'The code expired before the sign-in was finished. Nothing was stored; try again.';

async function signIn() {
  await chooseMicrosoft();
  vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
  await click(button('Sign in with Microsoft'));
}

describe('before signing in', () => {
  it('the Sign in step and the Directory field say where to name the directory if Microsoft says the username may be incorrect', async () => {
    await chooseMicrosoft();

    expect(step()).toBe('signin');
    expect(bodyFind('[data-signin-directory-hint]')!.textContent!.trim()).toBe(UsernameIncorrectHint);
    expect(UsernameIncorrectHint).toContain('username may be incorrect');
    expect(UsernameIncorrectHint).toContain('name that directory here');
    const help = bodyFind('[data-connect-step="signin"] [data-help]')!.getAttribute('aria-label')!;
    expect(help).toContain(UsernameIncorrectHint);
  });
});

describe('a refusal for an account not in the directory', () => {
  for (const sentence of [
    'Microsoft refused the sign-in: invalid_grant: AADSTS50020: User account from identity provider does not exist in tenant. Nothing was stored; try again.',
    'Microsoft refused the sign-in: invalid_grant: AADSTS50034: The user account does not exist in the directory. Nothing was stored; try again.',
    'Microsoft refused the sign-in: invalid_grant: AADSTS90072: The external account does not exist on the tenant. Nothing was stored; try again.',
    NotInDirectorySentence,
  ]) {
    it(`reads as the one sentence: ${sentence.slice(0, 60)}`, async () => {
      await signIn();

      getConnectionFlow.mockResolvedValue({ state: 'refused', sentence });
      await vi.advanceTimersByTimeAsync(10_000);
      await settled();

      const shown = bodyFind('[data-device-sentence]')!.textContent!.trim();
      expect(shown).toBe(NotInDirectorySentence);
      expect(shown).toContain('not found in the directory this sign-in used');
      expect(shown).not.toMatch(/AADSTS|invalid_grant|does not exist/);
    });
  }

  it('leaves a code the list does not confirm as the Host said it', async () => {
    await signIn();

    const other = 'Microsoft refused the sign-in: invalid_grant: AADSTS500200: Something else. Nothing was stored; try again.';
    getConnectionFlow.mockResolvedValue({ state: 'refused', sentence: other });
    await vi.advanceTimersByTimeAsync(10_000);
    await settled();

    expect(bodyFind('[data-device-sentence]')!.textContent!.trim()).toBe(other);
  });

  it('says the sentence when a browser sign-in comes back refused for it', async () => {
    await mountDialog(ConnectionsDialog, {
      notice: { outcome: 'refused', reason: 'Microsoft refused the sign-in: invalid_grant: AADSTS50034: The user account does not exist.' },
    });
    await settle();

    const notice = bodyFind('[data-connections-notice]')!.textContent!.trim();
    expect(notice).toBe(`The account was not connected: ${NotInDirectorySentence}`);
    expect(notice).not.toMatch(/AADSTS/);
  });
});

describe('a Microsoft code that ran out', () => {
  it('says where to name the directory on its own line after the expiry sentence', async () => {
    await signIn();

    getConnectionFlow.mockResolvedValue({ state: 'expired', sentence: `${expiry}\n${ExpiredNotInDirectoryLine}` });
    await vi.advanceTimersByTimeAsync(10_000);
    await settled();

    const sentence = bodyFind('[data-device-sentence]')!;
    expect(sentence.textContent!.trim().split('\n')).toEqual([expiry, ExpiredNotInDirectoryLine]);
    expect(ExpiredNotInDirectoryLine).toContain('username may be incorrect');
    expect(ExpiredNotInDirectoryLine).toContain('under Directory');
    expect(sentence.classList.contains('code-sign-in-sentence')).toBe(true);
  });
});
