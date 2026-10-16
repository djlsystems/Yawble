// @vitest-environment happy-dom
//
// THE GUIDED SETUP WIZARD: a package with a `setup` block is set up one question per screen -
// Welcome, Your mailbox, Your documents, Your preferences, Ready - with its defaults filled in, Next
// and Back, and the named steps as progress. Advanced… on each screen shows what the spec's table
// says and nothing of it is in the main flow, which never says a builder's word (trigger, cron,
// plugin, token, slot, member) or a folder path. The mailbox screen goes on only once the login test
// has passed. Nothing is installed before Set up, and the install is today's request: the same team
// name, settings and connections, the documents uploaded afterwards. A package without `setup`, and
// an update opened for a team, keep today's wizard.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody, textAndHelp } from '../../test/mountQuasar';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';
import { Folder, fakeHost, installPreview, installedRow, reply, sent, steps, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';
import { BuilderWords, FolderPath, MailboxPresets, savedMailbox, setupPlan } from '../../test/setupFixtures';

let calls: Call[] = [];

const mailboxRoutes: Route[] = [
  (call) => (call.method === 'GET' && call.url === '/api/connections/imap/presets' ? reply(200, MailboxPresets) : undefined),
  (call) => (call.method === 'GET' && call.url === '/api/connections' ? reply(200, []) : undefined),
  (call) => (call.method === 'GET' && call.url.startsWith('/api/connections/providers') ? reply(200, []) : undefined),
];

const loginWorks: Route = (call) =>
  call.method === 'POST' && call.url === '/api/connections/imap'
    ? reply(200, { sentence: 'Connected: 1,240 messages in Inbox', connection: savedMailbox() })
    : undefined;

const loginRefused: Route = (call) =>
  call.method === 'POST' && call.url === '/api/connections/imap'
    ? reply(422, { error: 'The server refused the password. For Gmail, make an app password: it needs 2-Step Verification.' })
    : undefined;

function serve(extra: Route[] = [], options: Parameters<typeof wizardRoutes>[0] = {}) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...extra, ...mailboxRoutes, loginWorks, ...wizardRoutes({ plan: setupPlan(), ...options })], calls)));
}

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const screen = () => bodyFind('[data-setup-screen]')?.getAttribute('data-setup-screen') ?? null;
const tabName = (tab: Element | null) => tab?.textContent?.trim().replace(/^\d+\. /, '') ?? null;
const current = () => tabName(bodyFind('[data-setup-step-tab][data-current="true"]'));
const steps_ = () => [...document.body.querySelectorAll('[data-setup-step-tab]')].map(tabName);
const wizardText = () => bodyFind('[data-setup-wizard]')?.textContent ?? '';

async function open() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  return wrapper;
}

async function next() {
  button('Next').click();
  await settle();
}

async function connectMailbox() {
  await type('Email address', 'dana@example.com');
  await type('App password', 'abcd efgh ijkl mnop');
  button('Test and connect').click();
  await settle();
}

async function chooseFile(name = 'resume.docx') {
  const input = bodyFind('[data-setup-screen="documents"] input[type="file"]') as HTMLInputElement;
  const file = new File(['resume'], name, { type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' });
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  input.dispatchEvent(new Event('change', { bubbles: true }));
  await settle();
}

async function openAdvanced() {
  button('Advanced…').click();
  await settle();
}

describe('Guided setup wizard - the five screens', () => {
  it('walks Welcome, Your mailbox, Your documents, Your preferences and Ready in order, with Next and Back', async () => {
    await open();

    expect(steps_()).toEqual(['Welcome', 'Your mailbox', 'Your documents', 'Your preferences', 'Ready']);
    expect(screen()).toBe('welcome');
    expect(current()).toBe('Welcome');
    expect(wizardText()).toContain('Finds jobs that match what you are looking for.');
    expect(wizardText()).toContain('a resume (.docx) and a mailbox');
    // The first screen has nowhere to go back to.
    expect(() => button('Back')).toThrow();

    await next();
    expect(screen()).toBe('mailbox');
    expect(current()).toBe('Your mailbox');
    expect(wizardText()).toContain('Which mailbox should job emails come from?');
    await connectMailbox();
    await next();

    expect(screen()).toBe('documents');
    expect(current()).toBe('Your documents');
    expect(wizardText()).toContain('Upload your resume');
    await chooseFile();
    await next();

    expect(screen()).toBe('preferences');
    expect(current()).toBe('Your preferences');
    expect(wizardText()).toContain('Where would you like to work?');
    // The default is filled in.
    expect((bodyFind('[data-setup-question="Scout/region"] input') as HTMLInputElement).value).toBe('Remote');
    await next();

    expect(screen()).toBe('ready');
    expect(current()).toBe('Ready');
    expect(() => button('Next')).toThrow();
    expect(button('Set up')).toBeTruthy();

    button('Back').click();
    await settle();
    expect(screen()).toBe('preferences');
    button('Back').click();
    await settle();
    expect(screen()).toBe('documents');
    button('Back').click();
    await settle();
    expect(screen()).toBe('mailbox');
    // Back keeps what was answered: the mailbox is still connected.
    expect(bodyFind('[data-mailbox-sentence]')!.textContent?.trim()).toBe('Connected: 1,240 messages in Inbox');
    expect(isDisabled('Next')).toBe(false);
  });

  it('installs nothing before Set up, then sends today\'s install request and uploads the document', async () => {
    serve([
      loginWorks,
      (call) =>
        call.url === '/api/solutions/install'
          ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
          : undefined,
      (call) => (call.method === 'POST' && call.url.includes('/upload') ? reply(200, { name: 'resume.docx', path: 'Resume/resume.docx' }) : undefined),
      (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
    ]);
    await open();
    await next();
    await connectMailbox();
    await next();
    await chooseFile();
    await next();
    await type('Where would you like to work?', 'Lisbon');
    await next();

    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
    expect(wizardText()).toContain('dana@example.com');
    expect(wizardText()).toContain('resume.docx');
    expect(wizardText()).toContain('Lisbon');

    button('Set up').click();
    await settle();

    const install = sent(calls, 'POST', '/api/solutions/install');
    expect(install).toHaveLength(1);
    expect(install[0]!.body).toEqual({
      folder: Folder,
      teamName: 'Job Tracker',
      settings: { Scout: { region: 'Lisbon', limit: 10, folder: 'INBOX' } },
      connections: { Scout: { mail: 'conn-mail' } },
    });
    const upload = calls.filter((call) => call.method === 'POST' && call.url.includes('/upload'));
    expect(upload).toHaveLength(1);
    expect(upload[0]!.url).toContain('job-tracker');
    expect((upload[0]!.body as FormData).get('path')).toBe('Resume');
    expect(bodyFind('[data-setup-done]')).not.toBeNull();
    expect(button('Check now')).toBeTruthy();
  });

  it('names the team after the package, and "Job Tracker 2" when that name is taken', async () => {
    serve([
      (call) => {
        if (call.method !== 'POST' || call.url !== '/api/solutions/preview') return undefined;
        const team = (call.body as { team?: string }).team ?? '';
        return reply(200, installPreview(team, team === 'Job Tracker' ? 'A team named Job Tracker exists.' : null, setupPlan()));
      },
    ]);
    await open();
    await openAdvanced();

    expect(field('Team name').value).toBe('Job Tracker 2');
  });

  it('keeps today\'s wizard for a package without setup, and for an update opened from a team', async () => {
    serve([], { plan: { ...setupPlan(), setup: null } });
    await open();
    expect(bodyFind('[data-setup-wizard]')).toBeNull();
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
    resetBody();

    serve([], { installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] });
    await mountDialog(SolutionWizard, { folder: Folder, team: 'job-tracker' }, { pinia: false });
    await settle();
    expect(bodyFind('[data-setup-wizard]')).toBeNull();
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
  });
});

describe('Guided setup wizard - the mailbox screen', () => {
  it('does not go on until the login test passes, and says what to fix in one sentence', async () => {
    serve([loginRefused]);
    await open();
    await next();

    expect(isDisabled('Next')).toBe(true);
    await connectMailbox();
    expect(bodyFind('[data-mailbox-sentence]')!.textContent?.trim()).toBe(
      'The server refused the password. For Gmail, make an app password: it needs 2-Step Verification.',
    );
    expect(isDisabled('Next')).toBe(true);
    await next();
    expect(screen()).toBe('mailbox');

    serve([loginWorks]);
    await type('App password', 'qrst uvwx yzab cdef');
    button('Test and connect').click();
    await settle();
    expect(bodyFind('[data-mailbox-sentence]')!.textContent?.trim()).toBe('Connected: 1,240 messages in Inbox');
    expect(isDisabled('Next')).toBe(false);
    await next();
    expect(screen()).toBe('documents');
  });
});

describe('Guided setup wizard - Advanced…', () => {
  it('shows on each screen exactly what the table says, and none of it before it is opened', async () => {
    await open();

    // 1. Welcome: team name, what it adds, every schedule and daily cap.
    expect(bodyFind('[data-setup-advanced]')).toBeNull();
    await openAdvanced();
    let advanced = bodyFind('[data-setup-advanced]')!;
    expect(field('Team name').closest('[data-setup-advanced]')).not.toBeNull();
    expect(advanced.querySelector('[data-setup-adds="Scout"]')).not.toBeNull();
    expect(advanced.querySelector('[data-setup-adds="Writer"]')).not.toBeNull();
    expect(advanced.querySelector('[data-setup-adds-plugin="job-board"]')).not.toBeNull();
    expect(textAndHelp(advanced.querySelector('[data-setup-schedule="Morning summary"]')!)).toContain('weekdays at 8:00');
    expect(textAndHelp(advanced.querySelector('[data-setup-schedule="Apply pressed"]')!)).toContain('400,000');

    // 2. Your mailbox: a Google app of one's own, server and port, which folder to watch.
    await next();
    expect(bodyFind('[data-setup-advanced]')).toBeNull();
    expect(bodyFind('[data-mailbox-servers]')).toBeNull();
    await openAdvanced();
    advanced = bodyFind('[data-setup-advanced]')!;
    expect(advanced.querySelector('[data-setup-google-app]')).not.toBeNull();
    expect(bodyFind('[data-mailbox-servers]')).not.toBeNull();
    expect(advanced.querySelector('[data-setup-question="Scout/folder"]')).not.toBeNull();
    expect((advanced.querySelector('[data-setup-question="Scout/folder"] input') as HTMLInputElement).value).toBe('INBOX');
    expect(advanced.querySelector('[data-setup-question="Scout/limit"]')).toBeNull();

    // 3. Your documents: the folder it is kept in.
    await connectMailboxWith();
    await next();
    expect(bodyFind('[data-setup-advanced]')).toBeNull();
    await openAdvanced();
    expect(textAndHelp(bodyFind('[data-setup-advanced]')!)).toContain('Resume/');

    // 4. Your preferences: every setting with its bounds.
    await chooseFile();
    await next();
    expect(bodyFind('[data-setup-question="Scout/limit"]')).toBeNull();
    await openAdvanced();
    advanced = bodyFind('[data-setup-advanced]')!;
    expect(textAndHelp(advanced.querySelector('[data-setup-question="Scout/limit"]')!)).toContain('Between 1 and 100, whole numbers only.');
    expect(advanced.querySelector('[data-setup-question="Scout/sources"]')).not.toBeNull();
    expect(advanced.querySelector('[data-setup-question="Scout/region"]')).toBeNull();

    // 5. Ready: the full plan, as today's Review shows it.
    await next();
    expect(bodyFind('[data-setup-advanced]')).toBeNull();
    await openAdvanced();
    advanced = bodyFind('[data-setup-advanced]')!;
    expect(advanced.querySelector('[data-member="Coordinator"]')).not.toBeNull();
    expect(advanced.querySelector('[data-trigger="Morning summary"]')).not.toBeNull();
    expect(advanced.querySelector('[data-skill="job-search-playbook"]')).not.toBeNull();
    expect(advanced.querySelector('[data-plugin="job-board"]')).not.toBeNull();
  });
});

describe('Guided setup wizard - their words', () => {
  it('never shows a builder word or a folder path in the main flow, on any screen', async () => {
    serve([loginWorks]);
    await open();

    const seen: string[] = [];
    const check = () => {
      const text = wizardText();
      seen.push(screen() ?? '');
      expect(text, `screen ${screen()}`).not.toMatch(BuilderWords);
      expect(text, `screen ${screen()}`).not.toMatch(FolderPath);
    };

    check();
    await next();
    check();
    await connectMailbox();
    check();
    await next();
    check();
    await chooseFile();
    check();
    await next();
    check();
    await next();
    check();

    expect(seen).toEqual(['welcome', 'mailbox', 'mailbox', 'documents', 'documents', 'preferences', 'ready']);
  });
});

async function connectMailboxWith() {
  serve([loginWorks]);
  await connectMailbox();
}
