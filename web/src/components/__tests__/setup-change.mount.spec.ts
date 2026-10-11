// @vitest-environment happy-dom
//
// COME BACK ANY TIME: a solution installed from a package with a `setup` block offers Change
// setup on its panel, which reopens the guided wizard on the team's CURRENT answers and saves each
// change through the routes a person's other screens use - the member's plugin settings route (for
// answers and the mailbox binding) and the documents upload - never an install, an update or a new
// route. A skipped required step shows as one line, "Finish setup: connect a mailbox", read from the
// same live `missing` the blocked banner reads; it opens the wizard on that screen, and goes once
// the step is provided.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QSelect } from 'quasar';
import type { VueWrapper } from '@vue/test-utils';

import SolutionPanel from '../SolutionPanel.vue';
import type { PluginMemberSettings, SolutionMissing } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import { fakeHost, okCheck, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { panelRoutes, scoutSettings } from '../../test/solutionPanelFixtures';
import { MailboxPresets, savedMailbox, setupPlan } from '../../test/setupFixtures';

let calls: Call[] = [];
let missing: SolutionMissing[] = [];
let stored: PluginMemberSettings;

const SettingsPath = '/api/teams/job-tracker/members/scout/plugin-settings';

function scout(connections: Record<string, string>): PluginMemberSettings {
  return scoutSettings({
    config: { region: 'EU', limit: 25 },
    fields: {
      region: { type: 'string', description: 'Where to look.', required: true, default: null, enum: null, setBy: 'person' },
      limit: { type: 'number', description: 'Most postings a scan keeps.', required: false, default: 20, enum: null, setBy: 'anyone', min: 1, max: 100, integer: true },
      folder: { type: 'string', description: 'The mail folder to watch.', required: false, default: 'INBOX', enum: null, setBy: 'anyone' },
      sources: { type: 'list', description: 'Which job boards to read.', required: false, default: [], enum: null, setBy: 'anyone' },
    },
    connections,
    connectionFields: {
      mail: { description: 'Where postings are emailed from.', providers: ['imap', 'microsoft', 'google'], scopes: {}, required: true, summary: 'needs a mailbox' },
    },
  } as Partial<PluginMemberSettings>);
}

const routes: Route[] = [
  (call) => (call.method === 'POST' && call.url === '/api/solutions/check' ? reply(200, okCheck(setupPlan())) : undefined),
  (call) =>
    call.method === 'GET' && call.url === '/api/teams/job-tracker/solution'
      ? reply(200, { team: 'job-tracker', id: 'job-tracker', name: 'Job Tracker', version: '1.1.0', plugins: [], missing })
      : undefined,
  (call) => (call.method === 'GET' && call.url === SettingsPath ? reply(200, stored) : undefined),
  (call) => {
    if (call.method !== 'PUT' || call.url !== SettingsPath) return undefined;
    const body = call.body as { config: Record<string, unknown>; connections?: Record<string, string> };
    stored = { ...stored, config: body.config as PluginMemberSettings['config'], connections: body.connections ?? {} };
    // The binding provides the mailbox: the live read no longer lists it.
    if (body.connections?.mail) missing = missing.filter((item) => item.kind !== 'connection');
    return reply(200, stored);
  },
  (call) => {
    if (call.method !== 'POST' || !call.url.includes('/upload')) return undefined;
    // The upload provides the document: the live read no longer lists it.
    missing = missing.filter((item) => item.kind !== 'document');
    return reply(200, { name: 'resume.docx', path: 'Resume/resume.docx' });
  },
  (call) => (call.method === 'GET' && call.url === '/api/connections' ? reply(200, [savedMailbox()]) : undefined),
  (call) => (call.method === 'GET' && call.url === '/api/connections/imap/presets' ? reply(200, MailboxPresets) : undefined),
];

beforeEach(() => {
  calls = [];
  missing = [];
  stored = scout({ mail: 'conn-mail' });
  vi.stubGlobal('fetch', vi.fn(fakeHost([...routes, ...panelRoutes()], calls)));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const screenId = () => bodyFind('[data-setup-id]')?.getAttribute('data-setup-id') ?? null;

function pickFile(selector: string, name: string) {
  const input = bodyFind(selector) as HTMLInputElement;
  Object.defineProperty(input, 'files', { value: [new File(['cv'], name)], configurable: true });
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

async function next() {
  button('Next').click();
  await settle();
}

/** Every request that writes: anything but a read (a GET, or the package check, which writes nothing). */
const writes = () => calls.filter((call) => call.method !== 'GET' && call.url !== '/api/solutions/check');

describe('Change setup', () => {
  it('opens the wizard on the current answers and saves each change through the existing routes', async () => {
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    (bodyFind('[data-change-setup]') as HTMLElement).click();
    await settle();
    expect(screenId()).toBe('welcome');

    await next();
    // The mailbox bound now, connected.
    expect(screenId()).toBe('mailbox:Scout/mail');
    expect(bodyFind('[data-mailbox-sentence]')!.textContent).toContain('dana@example.com');
    await next();

    // A new resume goes through the documents upload, into the folder the package names.
    const input = bodyFind('[data-setup-screen="documents"] input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [new File(['cv'], 'resume.docx')], configurable: true });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await settle();
    await next();
    const uploads = calls.filter((call) => call.method === 'POST' && call.url.includes('/upload'));
    expect(uploads).toHaveLength(1);
    expect((uploads[0]!.body as FormData).get('path')).toBe('Resume');

    // The current answer, not the package's default.
    expect(screenId()).toBe('preferences');
    expect(field('Where would you like to work?').value).toBe('EU');
    await type('Where would you like to work?', 'Lisbon');
    await next();

    const saves = sent(calls, 'PUT', SettingsPath);
    expect(saves).toHaveLength(1);
    // Only the changed answer changes: the stored limit and the mailbox binding are sent back as they were.
    expect(saves[0]!.body).toMatchObject({ config: { region: 'Lisbon', limit: 25 }, connections: { mail: 'conn-mail' } });
    expect(screenId()).toBe('ready');

    // Nothing but the existing routes wrote: no install, no update, nothing new.
    expect(writes().map((call) => `${call.method} ${call.url.replace(/\?.*$/, '')}`)).toEqual([
      'POST /api/teams/job-tracker/documents/upload',
      `PUT ${SettingsPath}`,
    ]);
  });

  it('saves a connection picked for an unasked input under Your preferences > Advanced… through the binding route', async () => {
    // The package asks nothing about the alerts input: it is offered only under Advanced….
    const plan = setupPlan();
    const alerts = { ...plan.setup!.connections[0]!, slot: 'alerts', question: null, hint: null, advanced: true, required: false, description: 'Where alerts are sent from.' };
    vi.stubGlobal(
      'fetch',
      vi.fn(
        fakeHost(
          [
            (call) =>
              call.method === 'POST' && call.url === '/api/solutions/check'
                ? reply(200, okCheck({ ...plan, setup: { ...plan.setup!, connections: [...plan.setup!.connections, alerts] } }))
                : undefined,
            ...routes,
            ...panelRoutes(),
          ],
          calls,
        ),
      ),
    );
    stored = scout({ mail: 'conn-mail' });
    stored.connectionFields = { ...stored.connectionFields, alerts: { ...stored.connectionFields!.mail!, required: false, description: 'Where alerts are sent from.' } };
    const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    (bodyFind('[data-change-setup]') as HTMLElement).click();
    await settle();
    await next();
    await next();
    await next();
    expect(screenId()).toBe('preferences');
    button('Advanced…').click();
    await settle();

    const picker = (wrapper as VueWrapper).findAllComponents(QSelect).find((select) => select.props('label') === 'Where alerts are sent from.');
    expect(picker).toBeDefined();
    picker!.vm.$emit('update:modelValue', 'conn-mail');
    await settle();
    await next();

    const saves = sent(calls, 'PUT', SettingsPath);
    expect(saves).toHaveLength(1);
    // The new binding, with the mailbox's binding and the stored answers sent back as they were.
    expect(saves[0]!.body).toMatchObject({ config: { region: 'EU', limit: 25 }, connections: { mail: 'conn-mail', alerts: 'conn-mail' } });
    expect(screenId()).toBe('ready');
  });
});

describe('Finish setup', () => {
  it('shows one line for a skipped required step, opens the wizard on it, and goes once it is provided', async () => {
    stored = scout({});
    missing = [{ kind: 'connection', name: 'mail', member: 'scout', description: 'Where postings are emailed from.' }];
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    const line = bodyFind('[data-finish-setup]');
    expect(line).not.toBeNull();
    expect(line!.textContent?.trim()).toBe('Finish setup: connect a mailbox');

    line!.click();
    await settle();
    expect(screenId()).toBe('mailbox:Scout/mail');

    // The mailbox already connected elsewhere is bound through the member's settings route.
    (bodyFind('[data-setup-use="conn-mail"]') as HTMLElement).click();
    await settle();
    const saves = sent(calls, 'PUT', SettingsPath);
    expect(saves).toHaveLength(1);
    expect(saves[0]!.body).toMatchObject({ connections: { mail: 'conn-mail' } });

    (bodyFind('[data-setup-wizard] [aria-label="Close"]') as HTMLElement).click();
    await settle();

    expect(bodyFind('[data-finish-setup]')).toBeNull();
    expect(bodyFind('[data-change-setup]')).not.toBeNull();
  });

  it('shows one line for a skipped required document, opens the wizard on it, and goes once it is uploaded', async () => {
    // The Host names a missing document by its folder with a trailing slash.
    missing = [{ kind: 'document', name: 'Resume/', member: null, description: 'Your reference resume, .docx or PDF.' }];
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    const line = bodyFind('[data-finish-setup]');
    expect(line).not.toBeNull();
    expect(line!.textContent?.trim()).toBe('Finish setup: upload your resume');

    line!.click();
    await settle();
    expect(screenId()).toBe('documents:Resume');

    pickFile('[data-setup-screen="documents"] input[type="file"]', 'resume.docx');
    await settle();
    await next();
    const uploads = calls.filter((call) => call.method === 'POST' && call.url.includes('/upload'));
    expect(uploads).toHaveLength(1);
    expect((uploads[0]!.body as FormData).get('path')).toBe('Resume');

    (bodyFind('[data-setup-wizard] [aria-label="Close"]') as HTMLElement).click();
    await settle();

    expect(bodyFind('[data-finish-setup]')).toBeNull();
    expect(bodyFind('[data-change-setup]')).not.toBeNull();
  });

  it('shows nothing for a package without a setup block', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        fakeHost(
          [
            (call) => (call.method === 'POST' && call.url === '/api/solutions/check' ? reply(200, okCheck({ ...setupPlan(), setup: null })) : undefined),
            ...routes,
            ...panelRoutes(),
          ],
          calls,
        ),
      ),
    );
    missing = [{ kind: 'connection', name: 'mail', member: 'scout', description: 'Where postings are emailed from.' }];
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    expect(bodyFind('[data-setup-entry]')).toBeNull();
    expect(bodyFind('[data-finish-setup]')).toBeNull();
  });
});
