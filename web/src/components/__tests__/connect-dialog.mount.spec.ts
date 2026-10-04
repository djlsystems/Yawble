// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS > ADD CONNECTION: the guided dialog. Three steps - the service, with what the
// installed plugins will ask of it in words, each line naming the plugin that wants it and any of
// them untickable; setting up the provider's app, only when its client is not set up yet, as a
// checklist of the provider's own guide; and signing in through the existing web flow, which comes
// back to the dialog for an optional name. Today's Providers tab and Connect form are under Advanced,
// unchanged.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves, of the one navigation the web makes
// and of the clipboard, so where the browser would go and what was copied are asserted, not done.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  renameConnection,
  saveConnectionProvider,
  goTo,
  copyText,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  getConnectionNeeds: vi.fn(),
  startConnection: vi.fn(),
  renameConnection: vi.fn(),
  saveConnectionProvider: vi.fn(),
  goTo: vi.fn(),
  copyText: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  renameConnection,
  saveConnectionProvider,
}));

const page = vi.hoisted(() => ({ origin: 'https://instance.example.test' }));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => page.origin,
}));

vi.mock('../../lib/clipboard', () => ({ copyText }));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionGuide, ConnectionNeeds } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const gmail = 'https://www.googleapis.com/auth/gmail.modify';
const drive = 'https://www.googleapis.com/auth/drive.file';
const odd = 'https://www.googleapis.com/auth/something.unknown';

const needs: ConnectionNeeds = {
  provider: 'google',
  needs: [
    { plugin: 'mail-helper', slot: 'mail', description: 'The mailbox it reads and answers', scopes: [gmail] },
    { plugin: 'drive-filer', slot: 'files', description: null, scopes: [drive, odd] },
  ],
  scopes: [
    { scope: gmail, words: 'Read, change and send your Gmail', plugins: ['mail-helper'] },
    { scope: drive, words: 'See and change the Drive files it made', plugins: ['drive-filer'] },
    { scope: odd, words: null, plugins: ['drive-filer'] },
  ],
  apis: [
    { api: 'gmail.googleapis.com', link: 'https://console.cloud.google.com/apis/library/gmail.googleapis.com?project={projectId}' },
    { api: 'drive.googleapis.com', link: 'https://console.cloud.google.com/apis/library/drive.googleapis.com?project={projectId}' },
  ],
};

// The Host's own wording of the address warning, at the start of the client step's text.
const servedWarning =
  "Google and Microsoft refuse a redirect URI on an IP address such as 172.31.242.154. Open this page at http://localhost:8080 on the machine running the container, where the providers accept http, and register that address instead; or connect from that machine with the operator CLI's `connect`.";

const googleGuide: ConnectionGuide = {
  steps: [
    {
      id: 'project',
      title: 'Project',
      text: 'Create a project, or pick one you have.',
      link: 'https://console.cloud.google.com/projectcreate',
      copy: [],
    },
    {
      id: 'apis',
      title: 'Turn on the APIs',
      text: 'Turn on each API the scopes need, in the same project.',
      link: 'https://console.cloud.google.com/flows/enableapi?apiid=gmail.googleapis.com,drive.googleapis.com&project={projectId}',
      copy: [
        { label: 'Gmail API', value: 'gmail.googleapis.com' },
        { label: 'Google Drive API', value: 'drive.googleapis.com' },
      ],
    },
    {
      id: 'branding',
      title: 'Branding and audience',
      text: 'Name the app, set the audience to External and publish it.',
      link: 'https://console.cloud.google.com/auth/branding?project={projectId}',
      copy: [],
    },
    {
      id: 'data-access',
      title: 'Data access',
      text: 'Add these scopes.',
      link: 'https://console.cloud.google.com/auth/scopes?project={projectId}',
      copy: [{ label: 'Scope', value: gmail }],
    },
    {
      id: 'client',
      title: 'Client',
      text: 'Create a Web application client and register this redirect URI.',
      link: 'https://console.cloud.google.com/auth/clients/create?project={projectId}',
      copy: [{ label: 'Redirect URI', value: 'https://instance.example.test/api/connections/callback' }],
    },
    {
      id: 'credentials',
      title: 'Client ID and secret',
      text: 'Paste the client ID and secret Google showed you.',
      link: null,
      copy: [],
    },
  ],
};

const googleReady = hostProvider({
  id: 'google',
  clientId: '123.apps.googleusercontent.com',
  clientSecretSet: true,
  configured: true,
  guide: googleGuide,
});
const googleBare = hostProvider({ id: 'google', guide: googleGuide });

/** Google's guide as the Host serves it at an address the providers refuse: the warning opens the client step. */
const warnedGuide: ConnectionGuide = {
  steps: googleGuide.steps.map((guideStep) =>
    guideStep.id === 'client' ? { ...guideStep, text: `${servedWarning} ${guideStep.text}` } : guideStep,
  ),
};
const microsoft = hostProvider({ id: 'microsoft' });

const authorizationUrl = 'https://accounts.google.com/o/oauth2/v2/auth?client_id=123&state=abc';

beforeEach(() => {
  for (const mock of [
    listConnections,
    listConnectionProviders,
    getConnectionNeeds,
    startConnection,
    renameConnection,
    saveConnectionProvider,
    goTo,
    copyText,
  ]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([googleReady, microsoft]);
  getConnectionNeeds.mockResolvedValue(needs);
  copyText.mockResolvedValue(undefined);
  startConnection.mockResolvedValue({
    authorizationUrl,
    state: 'abc',
    redirectUri: 'https://instance.example.test/api/connections/callback',
    expiresAt: '2026-09-28T10:10:00Z',
  });
});

afterEach(resetBody);

async function mountConnections(props: Record<string, unknown> = {}) {
  const wrapper = await mountDialog(ConnectionsDialog, props);
  await settle();
  return wrapper;
}

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;
const scopeLine = (scope: string) => bodyFind(`[data-connect-scope="${scope}"]`)!;
const ticked = (scope: string) => scopeLine(scope).querySelector('[role="checkbox"]')!.getAttribute('aria-checked') === 'true';

/** Add connection, then Google: the first step with Google's needs read. */
async function chooseGoogle() {
  await click(button('Add connection'));
  await click(bodyFind('[data-connect-service="google"]'));
}

describe('Add connection', () => {
  it('sits at the top of Admin > Connections and opens the guided dialog at its first step', async () => {
    const wrapper = await mountConnections();

    expect(bodyFind('[data-guided-connect]')).toBeNull();
    await click(button('Add connection'));

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(step()).toBe('service');
    expect(bodyFind('[data-connect-service="google"]')!.textContent).toContain('Google');
    expect(bodyFind('[data-connect-service="microsoft"]')!.textContent).toContain('Microsoft');

    wrapper.unmount();
  });
});

describe('The service step', () => {
  it('lists what will be asked in words, each line naming the plugin that wants it, all ticked', async () => {
    const wrapper = await mountConnections();
    await chooseGoogle();

    expect(getConnectionNeeds).toHaveBeenCalledWith('google');

    const mail = scopeLine(gmail);
    expect(mail.querySelector('[data-scope-words]')!.textContent).toContain('Read, change and send your Gmail');
    expect(mail.querySelector('[data-scope-plugins]')!.textContent).toContain('mail-helper');
    // The raw scope is behind "details", not the line's words.
    expect(mail.querySelector('[data-scope-words]')!.textContent).not.toContain(gmail);
    expect(mail.querySelector('[data-scope-raw]')).toBeNull();
    await click([...mail.querySelectorAll('button')].find((candidate) => candidate.textContent?.includes('details'))!);
    expect(scopeLine(gmail).querySelector('[data-scope-raw]')!.textContent).toContain(gmail);

    expect(scopeLine(drive).querySelector('[data-scope-plugins]')!.textContent).toContain('drive-filer');
    // No wording known: the scope reads as itself.
    expect(scopeLine(odd).querySelector('[data-scope-words]')!.textContent).toContain(odd);

    expect([gmail, drive, odd].every(ticked)).toBe(true);

    wrapper.unmount();
  });

  it('lets any line be unticked, and signs in without it', async () => {
    const wrapper = await mountConnections();
    await chooseGoogle();

    await click(scopeLine(drive).querySelector('[role="checkbox"]'));
    expect(ticked(drive)).toBe(false);
    expect(ticked(gmail)).toBe(true);

    await click(button('Next'));
    await click(button('Sign in with Google'));

    expect(startConnection).toHaveBeenCalledWith({ provider: 'google', scopes: [gmail, odd] });
    expect(goTo).toHaveBeenCalledWith(authorizationUrl);

    wrapper.unmount();
  });

  it('sends Microsoft to Advanced, with Microsoft chosen in the Connect form', async () => {
    const wrapper = await mountConnections();
    await click(button('Add connection'));
    await click(bodyFind('[data-connect-service="microsoft"]'));

    expect(bodyFind('[data-guided-connect]')).toBeNull();
    expect(bodyFind('[data-connect-dialog]')).not.toBeNull();
    expect(bodyFind('[data-needs-client]')!.textContent).toContain('Its client is not set up yet.');

    wrapper.unmount();
  });
});

describe('The set-up step', () => {
  it('is skipped when the client is set up', async () => {
    const wrapper = await mountConnections();
    await chooseGoogle();
    await click(button('Next'));

    expect(step()).toBe('signin');
    expect(bodyFind('[data-guide-step]')).toBeNull();

    wrapper.unmount();
  });

  it("shows Google's guide as a checklist, in order, with links in the project, copy buttons and Done ticks", async () => {
    listConnectionProviders.mockResolvedValue([googleBare, microsoft]);
    const wrapper = await mountConnections();
    await chooseGoogle();
    await click(button('Next'));

    expect(step()).toBe('setup');
    const ids = [...document.body.querySelectorAll('[data-guide-step]')].map((element) => element.getAttribute('data-guide-step'));
    expect(ids).toEqual(['project', 'apis', 'branding', 'data-access', 'client', 'credentials']);

    // Before a project id, a link that wants one goes without it; after, it opens in that project.
    const branding = () => bodyFind('[data-guide-step="branding"] a[data-guide-link]')!.getAttribute('href');
    expect(branding()).toBe('https://console.cloud.google.com/auth/branding');
    await type('Project ID (optional)', 'my-project-42');
    expect(branding()).toBe('https://console.cloud.google.com/auth/branding?project=my-project-42');

    await click(bodyFind('[data-guide-step="data-access"] button[aria-label="Copy Scope"]'));
    expect(copyText).toHaveBeenCalledWith(gmail);

    const done = bodyFind('[data-guide-step="project"] [data-guide-done]')!;
    expect(done.getAttribute('aria-checked')).toBe('false');
    await click(done);
    expect(bodyFind('[data-guide-step="project"] [data-guide-done]')!.getAttribute('aria-checked')).toBe('true');

    wrapper.unmount();
  });

  it('turns every API on with one link, names each to copy, and links each one too', async () => {
    listConnectionProviders.mockResolvedValue([googleBare, microsoft]);
    const wrapper = await mountConnections();
    await chooseGoogle();
    await click(button('Next'));

    const apis = () => bodyFind('[data-guide-step="apis"]')!;
    const all = () => apis().querySelectorAll('a[data-guide-link]');
    expect(all()).toHaveLength(1);
    // No project id yet: the flow opens without one, every API still named.
    expect(all()[0]!.getAttribute('href')).toBe('https://console.cloud.google.com/flows/enableapi?apiid=gmail.googleapis.com,drive.googleapis.com');

    expect(apis().textContent).toContain('Gmail API');
    expect(apis().textContent).toContain('Google Drive API');
    await click(apis().querySelector('button[aria-label="Copy Google Drive API"]'));
    expect(copyText).toHaveBeenCalledWith('drive.googleapis.com');

    await type('Project ID (optional)', 'my-project-42');
    expect(all()[0]!.getAttribute('href')).toBe(
      'https://console.cloud.google.com/flows/enableapi?apiid=gmail.googleapis.com,drive.googleapis.com&project=my-project-42',
    );
    const each = [...apis().querySelectorAll('[data-guide-api] a')].map((link) => link.getAttribute('href'));
    expect(each).toEqual([
      'https://console.cloud.google.com/apis/library/gmail.googleapis.com?project=my-project-42',
      'https://console.cloud.google.com/apis/library/drive.googleapis.com?project=my-project-42',
    ]);

    wrapper.unmount();
  });

  it('builds the guide for the ticked scopes', async () => {
    listConnectionProviders.mockResolvedValue([googleBare, microsoft]);
    const wrapper = await mountConnections();
    await chooseGoogle();
    await click(scopeLine(drive).querySelector('[role="checkbox"]'));

    // The guide read for what is ticked lists only what is ticked.
    const forTicked: ConnectionGuide = {
      steps: googleGuide.steps.map((guideStep) =>
        guideStep.id === 'data-access' ? { ...guideStep, copy: [{ label: 'Scope', value: odd }] } : guideStep,
      ),
    };
    listConnectionProviders.mockResolvedValue([{ ...googleBare, guide: forTicked }, microsoft]);
    await click(button('Next'));

    expect(listConnectionProviders).toHaveBeenLastCalledWith([gmail, odd]);
    expect(step()).toBe('setup');
    expect(bodyFind('[data-guide-step="data-access"]')!.textContent).toContain(odd);
    expect(bodyFind('[data-guide-step="data-access"]')!.textContent).not.toContain(gmail);

    wrapper.unmount();
  });

  it("checks the client ID's shape before saving, then saves it and goes on to sign in", async () => {
    listConnectionProviders.mockResolvedValue([googleBare, microsoft]);
    const wrapper = await mountConnections();
    await chooseGoogle();
    await click(button('Next'));

    await type('Client ID', 'not-a-google-id');
    await type('Client secret', 'typed-in-the-test');
    expect(bodyFind('[data-client-id-shape]')!.textContent).toContain('.apps.googleusercontent.com');
    expect(isDisabled('Save client')).toBe(true);

    await type('Client ID', '987-abc.apps.googleusercontent.com');
    expect(bodyFind('[data-client-id-shape]')).toBeNull();
    saveConnectionProvider.mockResolvedValue({ ...googleBare, clientId: '987-abc.apps.googleusercontent.com', clientSecretSet: true, configured: true });
    listConnectionProviders.mockResolvedValue([{ ...googleReady, clientId: '987-abc.apps.googleusercontent.com' }, microsoft]);
    await click(button('Save client'));

    expect(saveConnectionProvider).toHaveBeenCalledWith('google', {
      clientId: '987-abc.apps.googleusercontent.com',
      clientSecret: 'typed-in-the-test',
    });
    expect(step()).toBe('signin');

    wrapper.unmount();
  });

  it("says first, on Google's client step, that this address will be refused", async () => {
    page.origin = 'http://172.31.242.154:8080';
    try {
      listConnectionProviders.mockResolvedValue([{ ...googleBare, guide: warnedGuide }, microsoft]);
      const wrapper = await mountConnections();
      await chooseGoogle();
      await click(button('Next'));

      const client = bodyFind('[data-guide-step="client"]')!;
      const body = client.querySelector('[data-guide-body]')!;
      // Once, and before anything else the step says.
      expect(body.textContent!.trim().startsWith('Google and Microsoft refuse a redirect URI')).toBe(true);
      expect(client.textContent!.split('refuse a redirect URI').length - 1).toBe(1);
      expect(client.textContent!.split('http://localhost:8080').length - 1).toBe(1);
      expect(bodyFind('[data-guide-step="project"]')!.textContent).not.toContain('refuse a redirect URI');

      wrapper.unmount();
    } finally {
      page.origin = 'https://instance.example.test';
    }
  });
});

describe('The sign-in step', () => {
  it('comes back to the dialog with the account, and takes an optional name', async () => {
    listConnections.mockResolvedValue([
      hostConnection({ id: 'conn-new', provider: 'google', name: 'person@example.com', account: 'person@example.com' }),
    ]);
    const first = await mountConnections();
    await chooseGoogle();
    await click(button('Next'));
    await click(button('Sign in with Google'));
    first.unmount();
    resetBody();

    // The provider's round trip lands back on the Console with what it came back with.
    const wrapper = await mountConnections({ notice: { outcome: 'connected', id: 'conn-new' } });

    expect(step()).toBe('result');
    expect(bodyFind('[data-guided-connect]')!.textContent).toContain('person@example.com');
    renameConnection.mockResolvedValue({});
    await type('Name (optional)', 'Work mail');
    await click(button('Finish'));

    expect(renameConnection).toHaveBeenCalledWith('conn-new', 'Work mail');
    expect(bodyFind('[data-guided-connect]')).toBeNull();

    wrapper.unmount();
  });

  it('finishes without a name and renames nothing', async () => {
    listConnections.mockResolvedValue([hostConnection({ id: 'conn-new', provider: 'google' })]);
    const first = await mountConnections();
    await chooseGoogle();
    await click(button('Next'));
    await click(button('Sign in with Google'));
    first.unmount();
    resetBody();

    const wrapper = await mountConnections({ notice: { outcome: 'connected', id: 'conn-new' } });
    await click(button('Finish'));

    expect(renameConnection).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('a connection made from Advanced does not open the guided dialog on return', async () => {
    const wrapper = await mountConnections({ notice: { outcome: 'connected', id: 'conn-new' } });

    expect(bodyFind('[data-guided-connect]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Advanced', () => {
  it("holds today's Providers tab and Connect form, unchanged", async () => {
    const wrapper = await mountConnections();

    expect(bodyFind('[data-connections-tab="providers"]')).toBeNull();
    expect(bodyText()).not.toContain('Connect an account…');

    await click(bodyFind('[data-connections-advanced]'));

    expect(bodyFind('[data-connections-tab="providers"]')!.textContent).toContain('Providers (2)');
    await click(button('Connect an account…'));
    expect(bodyFind('[data-connect-dialog]')).not.toBeNull();
    // Today's form: a provider, free-text scopes, a name, the redirect URI.
    expect(field('Scopes')).toBeDefined();
    expect(field('Name')).toBeDefined();
    expect(bodyFind('[data-connect-dialog] [data-redirect-uri]')!.textContent).toBe('https://instance.example.test/api/connections/callback');

    wrapper.unmount();
  });
});
