// @vitest-environment happy-dom
//
// UNINSTALL ASKS FIRST: it names what goes, says the sites are taken offline and kept with their
// data so a reinstall brings them back, and says the team and its documents stay; it offers
// the package's plugins only when it has some, sends nothing until confirmed, sends `removePlugins`
// as chosen, and then says what it did - plugins kept and who uses them, the documents folder kept,
// and anything it could not remove.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { openSection, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];
let plugins: string[];

const record: Route = (call) =>
  call.method === 'GET' && call.url === '/api/teams/job-tracker/solution'
    ? reply(200, {
        team: 'job-tracker',
        id: 'job-tracker',
        name: 'Job Tracker',
        version: '1.1.0',
        installedAt: '2026-09-29T10:00:00Z',
        installedBy: 'dana@example.com',
        plugins,
        missing: [],
      })
    : undefined;

const uninstall: Route = (call) =>
  call.method === 'POST' && call.url === '/api/teams/job-tracker/solution/uninstall'
    ? reply(200, {
        ok: false,
        team: 'job-tracker',
        id: 'job-tracker',
        version: '1.1.0',
        removed: { triggers: ['Scan for postings', 'Apply pressed'], members: ['scout'], skills: ['job-search-playbook'], tools: true },
        sitesKept: ['tracker'],
        plugins: { removed: [], kept: [{ id: 'job-board', usedBy: ['other-team'] }] },
        teamName: 'Job Tracker',
        documentsKept: '/data/documents/job-tracker',
        failures: ['The site tracker could not be unpublished: it is locked.'],
      })
    : undefined;

beforeEach(() => {
  plugins = ['job-board'];
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([record, uninstall, ...panelRoutes()], calls)));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function ask() {
  const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
  await settle();
  await openSection('maintenance');
  bodyFind('[data-uninstall]')!.click();
  await settle();
  return wrapper;
}

const click = async (selector: string) => {
  bodyFind(selector)!.click();
  await settle();
};

describe('Uninstall', () => {
  it('asks first, naming what goes and what stays, and sends nothing yet', async () => {
    await ask();

    const asking = bodyFind('[data-uninstall-ask]')!;
    expect(bodyText()).toContain('Uninstall Job Tracker?');
    expect(asking.textContent).toContain('Triggers: Scan for postings, Apply pressed');
    expect(asking.textContent).toContain('Members: Scout');
    expect(asking.textContent).toContain('tools folder');
    expect(asking.textContent).toContain('all of its documents');
    expect(sent(calls, 'POST', '/api/teams/job-tracker/solution/uninstall')).toHaveLength(0);
  });

  it('says the sites are taken offline and kept with their data, so a reinstall brings them back', async () => {
    await ask();

    const words = (bodyFind('[data-uninstall-sites-kept]')!.textContent ?? '').replace(/\s+/g, ' ');
    expect(words).toContain('It takes offline and keeps the site tracker, with all their data and versions');
    expect(words).toContain('nobody can open them while the package is uninstalled');
    expect(words).toContain('installing it onto team Job Tracker again brings them back as they were');
    // Nothing in the removal list says a site goes.
    expect(bodyFind('[data-uninstall-ask] ul')!.textContent).not.toMatch(/site/i);
    expect(bodyFind('[data-uninstall-team-delete]')!.textContent).toContain('Deleting the team later removes the kept sites and their data for good.');
  });

  it('sends nothing when cancelled', async () => {
    await ask();
    await click('[data-uninstall-cancel]');

    expect(sent(calls, 'POST', '/api/teams/job-tracker/solution/uninstall')).toHaveLength(0);
  });

  it('keeps the plugins unless asked, and then says what it did', async () => {
    const wrapper = await ask();
    await click('[data-uninstall-confirm]');

    expect(sent(calls, 'POST', '/api/teams/job-tracker/solution/uninstall').map((call) => call.body)).toEqual([
      { removePlugins: false },
    ]);
    const result = bodyFind('[data-uninstall-result]')!;
    expect(result.textContent).toContain('Members removed: scout');
    expect(result.textContent).toContain('except what is named below');
    expect(result.textContent).toContain('Team Job Tracker stays, with its Manager.');
    expect(bodyFind('[data-sites-kept]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Taken offline and kept with their data: tracker. Reinstall the package onto this team to bring them back.',
    );
    expect(result.textContent).not.toContain('Sites removed');
    expect(bodyFind('[data-plugin-kept]')?.textContent).toContain('Plugin job-board kept: used by other-team');
    expect(bodyFind('[data-documents-kept]')?.textContent).toContain('/data/documents/job-tracker');
    expect(bodyFind('[data-uninstall-failure]')?.textContent).toBe('The site tracker could not be unpublished: it is locked.');
    expect(wrapper.emitted('uninstalled')).toEqual([['job-tracker']]);

    await click('[data-uninstall-done]');
    expect(wrapper.emitted('launcher')).toHaveLength(1);
  });

  it("offers to remove the package's plugins and sends that choice", async () => {
    await ask();

    const offer = bodyFind('[data-uninstall-plugins]')!;
    expect(offer.textContent).toContain('job-board');
    offer.click();
    await settle();
    await click('[data-uninstall-confirm]');

    expect(sent(calls, 'POST', '/api/teams/job-tracker/solution/uninstall').map((call) => call.body)).toEqual([
      { removePlugins: true },
    ]);
  });

  it('offers no plugin removal for a package with no plugins', async () => {
    plugins = [];
    await ask();
    expect(bodyFind('[data-uninstall-plugins]')).toBeNull();
  });

  it("says the Host's refusal and stays open", async () => {
    calls = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(fakeHost([record, (call) => (call.url.endsWith('/uninstall') ? reply(409, { error: 'A run is going; try again when it ends.' }) : undefined), ...panelRoutes()], calls)),
    );
    await ask();
    await click('[data-uninstall-confirm]');

    expect(bodyFind('[data-uninstall-problem]')?.textContent).toBe('A run is going; try again when it ends.');
    expect(bodyFind('[data-uninstall-confirm]')).not.toBeNull();
  });
});
