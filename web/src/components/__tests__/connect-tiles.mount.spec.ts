// @vitest-environment happy-dom
//
// ADD CONNECTION'S PROVIDER TILES: one mailbox screen for Admin, Connections and a slot's Connect.
// Tiles Gmail, Outlook or Microsoft 365, iCloud, Yahoo and Other, each saying its way in; choosing one
// shows that provider's hints. Gmail through a Google app of one's own sits behind Advanced…, whose
// setup says first that it is done once, roughly how long it takes, that the ticks are only progress
// marks and that an existing project is fine, and warns before saving at an address Google refuses.
// A slot's Connect offers the kinds the slot takes, a mailbox included.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves, and of the one navigation the web
// makes. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  getConnectionNeeds,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  getConnectionNeeds: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  getConnectionNeeds,
}));

const page = vi.hoisted(() => ({ origin: 'https://instance.example.test' }));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => page.origin,
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import SlotConnect from '../SlotConnect.vue';
import type { ConnectionGuide, ConnectionNeeds, ConnectionSlot, MailboxPreset } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostProvider } from '../../test/pluginFixtures';
import { button, settle } from '../../test/formProbe';

const presets: MailboxPreset[] = [
  { id: 'gmail', name: 'Gmail', imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' } },
  { id: 'icloud', name: 'iCloud', imap: { host: 'imap.mail.me.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.me.com', port: 587, security: 'STARTTLS' } },
  { id: 'yahoo', name: 'Yahoo', imap: { host: 'imap.mail.yahoo.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.yahoo.com', port: 465, security: 'TLS' } },
  { id: 'other', name: 'Other', imap: null, smtp: null },
];

const gmailScope = 'https://www.googleapis.com/auth/gmail.modify';

const googleNeeds: ConnectionNeeds = {
  provider: 'google',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [gmailScope] }],
  scopes: [{ scope: gmailScope, words: 'Read, change and send your Gmail', plugins: ['mail-helper'] }],
  apis: [],
};

const googleGuide: ConnectionGuide = {
  steps: [
    { id: 'project', title: 'Create or pick a project', text: 'Create a project, or pick one.', link: null, copy: [] },
    { id: 'client', title: 'Create the client', text: 'Create a Web application client.', link: null, copy: [] },
    { id: 'credentials', title: 'Paste the client ID and secret', text: 'Paste them here.', link: null, copy: [] },
  ],
};

const googleBare = hostProvider({ id: 'google', guide: googleGuide });
const microsoft = hostProvider({ id: 'microsoft', configured: true });

beforeEach(() => {
  for (const mock of [listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets, getConnectionNeeds]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([googleBare, microsoft]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue(presets);
  getConnectionNeeds.mockResolvedValue(googleNeeds);
});

afterEach(resetBody);

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;
const tile = (id: string) => bodyFind(`[data-provider-tile="${id}"]`);
const tileNames = () => [...document.body.querySelectorAll('[data-provider-tile] [data-tile-name]')].map((n) => n.textContent?.trim());
const links = (selector: string) => [...document.body.querySelectorAll(`${selector} a`)].map((a) => a.getAttribute('href'));

async function openAdd() {
  const wrapper = await mountDialog(ConnectionsDialog);
  await settle();
  await click(button('Add connection'));
  return wrapper;
}

describe('The provider tiles', () => {
  it('are Gmail, Outlook or Microsoft 365, iCloud, Yahoo and Other, each saying its way in', async () => {
    const wrapper = await openAdd();

    expect(tileNames()).toEqual(['Gmail', 'Outlook or Microsoft 365', 'iCloud', 'Yahoo', 'Other']);
    const way = (id: string) => tile(id)!.querySelector('[data-tile-way]')!.textContent!.trim();
    expect(way('gmail')).toBe('App password');
    expect(way('outlook')).toBe('Sign in with a code');
    expect(way('icloud')).toBe('App-specific password');
    expect(way('yahoo')).toBe('App password');
    expect(way('other')).toBe('Server settings');

    wrapper.unmount();
  });

  it("Gmail shows 2-Step Verification, the app password page and the 16 characters, and who cannot make one is pointed to Advanced", async () => {
    const wrapper = await openAdd();
    await click(tile('gmail'));

    expect(step()).toBe('mailbox');
    expect(links('[data-mailbox-hints]')).toEqual([
      'https://myaccount.google.com/signinoptions/twosv',
      'https://myaccount.google.com/apppasswords',
    ]);
    const hints = bodyFind('[data-mailbox-hints]')!.textContent!;
    expect(hints).toContain('2-Step Verification');
    expect(hints).toContain('16 characters');
    expect(bodyFind('[data-mailbox-cannot]')!.textContent).toContain('cannot make one');

    await click(bodyFind('[data-mailbox-advanced]'));
    expect(step()).toBe('service');
    expect(getConnectionNeeds).toHaveBeenCalledWith('google');
    expect(bodyFind('[data-tile-hints="google"]')).not.toBeNull();

    wrapper.unmount();
  });

  it('Outlook signs in with a code, and says a work account may need IT approval', async () => {
    getConnectionNeeds.mockResolvedValue({ ...googleNeeds, provider: 'microsoft', scopes: [], needs: [] });
    const wrapper = await openAdd();
    await click(tile('outlook'));

    expect(getConnectionNeeds).toHaveBeenCalledWith('microsoft');
    const hints = bodyFind('[data-tile-hints="outlook"]')!.textContent!;
    expect(hints).toContain('short code');
    expect(hints).toContain('IT department to approve');

    wrapper.unmount();
  });

  it('iCloud names account.apple.com, says the password is shown once and the user name is often before the @', async () => {
    const wrapper = await openAdd();
    await click(tile('icloud'));

    expect(links('[data-mailbox-hints]')).toEqual(['https://account.apple.com']);
    const hints = bodyFind('[data-mailbox-hints]')!.textContent!;
    expect(hints).toContain('account.apple.com');
    expect(hints).toContain('only once');
    expect(hints).toContain('before the @');
    expect(bodyFind('[data-mailbox-cannot]')).toBeNull();

    wrapper.unmount();
  });

  it('Yahoo names Account security and Generate app password; Other asks for the server settings', async () => {
    const wrapper = await openAdd();
    await click(tile('yahoo'));

    expect(links('[data-mailbox-hints]')).toEqual(['https://login.yahoo.com/account/security']);
    expect(bodyFind('[data-mailbox-hints]')!.textContent).toContain('Account security, choose Generate app password');

    // Switching tile inside the form shows that tile's hints.
    await click(bodyFind('[data-mailbox-form] [data-mailbox-preset="other"]'));
    expect(bodyFind('[data-mailbox-hints]')!.textContent).toContain('server settings');

    wrapper.unmount();
  });
});

describe('Advanced…, Gmail through a Google app of your own', () => {
  async function toSetup() {
    const wrapper = await openAdd();
    await click(bodyFind('[data-connect-advanced]'));
    expect(getConnectionNeeds).toHaveBeenCalledWith('google');
    await click(button('Next'));
    expect(step()).toBe('setup');
    return wrapper;
  }

  it('sits behind Advanced… and is not a tile of its own when Gmail is the app password', async () => {
    const wrapper = await openAdd();

    expect(tile('google')).toBeNull();
    expect(button('Advanced…')).toBeDefined();

    wrapper.unmount();
  });

  it('says up front it is a one-time setup and how long, that the ticks are progress marks, and that an existing project is fine', async () => {
    const wrapper = await toSetup();

    const intro = bodyFind('[data-setup-intro]')!;
    expect(intro.textContent).toContain('one-time setup');
    expect(intro.textContent).toContain('20 to 30 minutes');
    expect(bodyFind('[data-setup-marks]')!.textContent).toContain('only mark your own progress');
    expect(bodyFind('[data-setup-project]')!.textContent).toContain('A project you already have is fine');
    // Up front: before the first step of the guide.
    const first = bodyFind('[data-guide-step="project"]')!;
    expect(intro.compareDocumentPosition(first) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    wrapper.unmount();
  });

  it('warns before saving when Google will refuse the address in use', async () => {
    page.origin = 'http://192.168.1.20:8080';
    try {
      const wrapper = await toSetup();

      const warning = bodyFind('[data-guide-step="credentials"] [data-save-warning]')!;
      expect(warning.textContent).toContain('refuse a redirect URI on an IP address such as 192.168.1.20');
      expect(warning.textContent).toContain('http://localhost:8080');

      wrapper.unmount();
    } finally {
      page.origin = 'https://instance.example.test';
    }
  });

  it('does not warn at http://127.0.0.1, which Google takes as loopback', async () => {
    page.origin = 'http://127.0.0.1:8080';
    try {
      const wrapper = await toSetup();

      expect(bodyFind('[data-guide-step="credentials"]')).not.toBeNull();
      expect(bodyFind('[data-save-warning]')).toBeNull();

      wrapper.unmount();
    } finally {
      page.origin = 'https://instance.example.test';
    }
  });
});

describe("A slot's Connect", () => {
  const slot = (providers: string[]): ConnectionSlot => ({
    description: null,
    providers,
    scopes: { google: [gmailScope] },
    required: true,
    summary: 'needs a mailbox',
  });

  async function mountSlot(providers: string[]) {
    const wrapper = await mountDialog(SlotConnect, {
      plugin: 'mail-helper',
      slotName: 'mail',
      spec: slot(providers),
      providers: [googleBare, microsoft],
      connections: [],
    }, { attachTo: document.body });
    await settle();
    return wrapper;
  }

  it('offers the mailbox tiles for a slot that takes imap alone', async () => {
    const wrapper = await mountSlot(['imap']);

    await click(button('Connect a mailbox'));
    expect(step()).toBe('service');
    expect(tileNames()).toEqual(['Gmail', 'iCloud', 'Yahoo', 'Other']);
    expect(bodyFind('[data-connect-advanced]')).toBeNull();

    wrapper.unmount();
  });

  it('offers the mailbox beside Google for a slot that takes both, and does not skip to Google', async () => {
    const wrapper = await mountSlot(['google', 'imap']);

    await click(button('Connect Google or a mailbox'));
    expect(step()).toBe('service');
    expect(tileNames()).toEqual(['Gmail', 'iCloud', 'Yahoo', 'Other']);
    expect(bodyFind('[data-connect-advanced]')).not.toBeNull();
    expect(bodyText()).not.toContain('Outlook');

    wrapper.unmount();
  });
});
