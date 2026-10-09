// @vitest-environment happy-dom
//
// SOLUTIONS > GET STARTED: the package catalog the Host read (`GET /api/marketplace`), one tile per
// package - name, summary, kind, version, what it needs in words, and Installed / Update available -
// with Refresh (`POST /api/marketplace/refresh`). Get fetches the package (`POST
// /api/marketplace/{id}/fetch`) and opens on the folder it answered what Install from a folder
// opens: the wizard for a solution, the plugin install dialog for a plugin. Nothing is installed by
// Get. A catalog not checked, one the console cannot reach and a refused Get each read as a
// sentence, and package text is text.
import { afterEach, describe, expect, it, vi } from 'vitest';

import SolutionsLauncher from '../SolutionsLauncher.vue';
import type { MarketplaceCatalog, MarketplacePackage } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle } from '../../test/formProbe';
import { fakeHost, reply, sent, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

const Root = '/data/documents';

function pkg(fields: Partial<MarketplacePackage> & Pick<MarketplacePackage, 'id' | 'name'>): MarketplacePackage {
  return {
    kind: 'solution',
    summary: `${fields.name} does its one job.`,
    version: '1.0.0',
    needs: [],
    installed: false,
    installedVersion: null,
    installedOn: [],
    updateAvailable: false,
    ...fields,
  };
}

const Mail = pkg({
  id: 'mail',
  name: 'Mail',
  version: '1.2.0',
  summary: 'Reads your mailbox and drafts replies.',
  needs: ['A mailbox connection: IMAP, Microsoft or Google.', 'python3 in the image.'],
});
const JobTracker = pkg({
  id: 'job-tracker',
  name: 'Job Tracker',
  version: '1.2.0',
  installed: true,
  installedVersion: '1.1.0',
  installedOn: ['job-tracker'],
  updateAvailable: true,
});
const WhoAmI = pkg({ id: 'whoami', name: 'WhoAmI', kind: 'plugin', version: '0.3.0', installed: true, installedVersion: '0.3.0' });

const read = (packages: MarketplacePackage[]): MarketplaceCatalog => ({
  checked: true,
  reason: null,
  checkedAt: '2026-10-09T08:00:00Z',
  packages,
});

let calls: Call[] = [];

function serve(...routes: Route[]) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          ...routes,
          (call) => (call.method === 'GET' && call.url === '/api/documents' ? reply(200, { folders: [], root: Root }) : undefined),
          ...wizardRoutes(),
        ],
        calls,
      ),
    ),
  );
}

const catalogRoute = (answer: MarketplaceCatalog): Route => (call) =>
  call.method === 'GET' && call.url === '/api/marketplace' ? reply(200, answer) : undefined;

const fetchRoute = (id: string, status: number, body: unknown): Route => (call) =>
  call.method === 'POST' && call.url === `/api/marketplace/${id}/fetch` ? reply(status, body) : undefined;

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function getStarted() {
  await mountDialog(SolutionsLauncher);
  await settle();
  bodyFind('[data-solutions-tab="get-started"]')!.click();
  await settle();
}

const tile = (id: string) => bodyFind(`[data-catalog-package="${id}"]`)!;
const part = (id: string, name: string) => tile(id).querySelector(`[data-package-${name}]`)?.textContent?.trim();

describe('Solutions > Get started', () => {
  it('lists each package with its name, summary, kind, version, needs and whether it is installed', async () => {
    serve(catalogRoute(read([JobTracker, Mail, WhoAmI])));

    await getStarted();

    expect(sent(calls, 'GET', '/api/marketplace')).toHaveLength(1);
    expect(part('mail', 'name')).toBe('Mail');
    expect(part('mail', 'version')).toBe('1.2.0');
    expect(part('mail', 'kind')).toBe('Solution');
    expect(part('mail', 'summary')).toBe('Reads your mailbox and drafts replies.');
    expect([...tile('mail').querySelectorAll('[data-package-needs] li')].map((li) => li.textContent)).toEqual([
      'A mailbox connection: IMAP, Microsoft or Google.',
      'python3 in the image.',
    ]);
    expect(part('mail', 'installed')).toBe('Not installed');

    expect(part('job-tracker', 'installed')).toBe('Installed 1.1.0 on job-tracker · Update available: 1.2.0');
    expect(part('job-tracker', 'needs')).toBe('It needs nothing more.');

    expect(part('whoami', 'kind')).toBe('Plugin');
    expect(part('whoami', 'installed')).toBe('Installed 0.3.0');
  });

  it('reads the catalog again through the refresh route', async () => {
    serve(
      catalogRoute(read([Mail])),
      (call) => (call.method === 'POST' && call.url === '/api/marketplace/refresh' ? reply(200, read([Mail, WhoAmI])) : undefined),
    );
    await getStarted();
    expect(bodyFind('[data-catalog-package="whoami"]')).toBeNull();

    bodyFind('[data-catalog-refresh]')!.click();
    await settle();

    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);
    expect(part('whoami', 'name')).toBe('WhoAmI');
  });

  it('Get on a solution fetches it and opens the install wizard on the folder it answered', async () => {
    serve(catalogRoute(read([Mail])), fetchRoute('mail', 200, { id: 'mail', version: '1.2.0', kind: 'solution', folder: 'Marketplace/mail-1.2.0' }));
    await getStarted();

    tile('mail').querySelector<HTMLElement>('[data-package-get]')!.click();
    await settle();
    await settle();

    expect(sent(calls, 'POST', '/api/marketplace/mail/fetch')).toHaveLength(1);
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(sent(calls, 'POST', '/api/solutions/check').map((call) => call.body)).toEqual([{ folder: `${Root}/Marketplace/mail-1.2.0` }]);
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it('Get on a plugin fetches it and opens the plugin install dialog on the folder, installing nothing yet', async () => {
    serve(catalogRoute(read([WhoAmI])), fetchRoute('whoami', 200, { id: 'whoami', version: '0.3.0', kind: 'plugin', folder: 'Marketplace/whoami-0.3.0' }));
    await getStarted();

    tile('whoami').querySelector<HTMLElement>('[data-package-get]')!.click();
    await settle();
    await settle();

    expect(sent(calls, 'POST', '/api/marketplace/whoami/fetch')).toHaveLength(1);
    expect(bodyFind('[data-install-dialog]')).not.toBeNull();
    expect(bodyText()).toContain('A built plugin folder');
    expect(field('Folder').value).toBe(`${Root}/Marketplace/whoami-0.3.0`);
    expect(button('Install').disabled).toBe(false);
    expect(sent(calls, 'POST', '/api/plugins/install')).toHaveLength(0);
    expect(bodyFind('[data-solution-wizard]')).toBeNull();
  });

  it('says a catalog was not checked, and why, rather than showing an empty list', async () => {
    serve(catalogRoute({ checked: false, reason: 'No catalog address is set.', checkedAt: null, packages: [] }));

    await getStarted();

    expect(bodyFind('[data-catalog-not-checked]')?.textContent?.trim()).toBe('The package catalog has not been checked: No catalog address is set.');
    expect(bodyFind('[data-catalog-empty]')).toBeNull();
    expect(bodyFind('[data-catalog-package]')).toBeNull();
  });

  it('says so in a sentence when the catalog cannot be reached', async () => {
    serve((call) => (call.url === '/api/marketplace' ? reply(502, { error: 'The Host could not be reached' }) : undefined));

    await getStarted();

    expect(bodyFind('[data-catalog-unreachable]')?.textContent?.trim()).toBe('Could not reach the package catalog: The Host could not be reached.');
    expect(bodyFind('[data-catalog-package]')).toBeNull();
  });

  it('says a refused Get in a sentence, and opens nothing', async () => {
    serve(catalogRoute(read([Mail])), fetchRoute('mail', 400, { error: 'The download did not match its sha256.' }));
    await getStarted();

    tile('mail').querySelector<HTMLElement>('[data-package-get]')!.click();
    await settle();

    expect(part('mail', 'refused')).toBe('Mail was not fetched: The download did not match its sha256. Nothing was installed.');
    expect(bodyFind('[data-solution-wizard]')).toBeNull();
    expect(bodyFind('[data-install-dialog]')).toBeNull();
    expect(sent(calls, 'POST', '/api/solutions/check')).toHaveLength(0);
  });

  it('shows package text that looks like HTML as its characters', async () => {
    serve(catalogRoute(read([pkg({ id: 'odd', name: '<i>Odd</i>', summary: '<img src=x onerror=alert(1)>', needs: ['<b>a key</b>'] })])));

    await getStarted();

    expect(part('odd', 'name')).toBe('<i>Odd</i>');
    expect(part('odd', 'summary')).toBe('<img src=x onerror=alert(1)>');
    expect(tile('odd').querySelector('[data-package-needs] li')?.textContent).toBe('<b>a key</b>');
    expect(tile('odd').querySelector('img, b, [data-package-name] i')).toBeNull();
  });
});
