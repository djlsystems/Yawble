// @vitest-environment happy-dom
//
// MARKETPLACE, THE FIRST OPEN IN A PAGE LOAD: Browse shows the catalog the Host has (`GET
// /api/marketplace`) and asks it to read the catalog again (`POST /api/marketplace/refresh`) once,
// with Refresh's loading indicator while that is out, and shows the answer - on Browse and in
// Installed's update offers. A later open in the same page load does not refresh; a refresh that
// fails keeps the catalog shown with the Host's sentence; a catalog not checked asks nothing more.
//
// SEEN TO FAIL: before the first-open refresh no test here saw a POST, the refreshed package or the
// refreshed version's Update.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionsLauncher from '../SolutionsLauncher.vue';
import type { InstalledSolution, MarketplaceCatalog, MarketplacePackage } from '../../api/types';
import { resetFirstOpenRefresh } from '../../lib/marketplaceRefresh';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, installedRow, reply, sent, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

function pkg(fields: Partial<MarketplacePackage> & Pick<MarketplacePackage, 'id' | 'name'>): MarketplacePackage {
  return {
    kind: 'solution',
    summary: `${fields.name} does its one job.`,
    description: '',
    version: '1.0.0',
    needs: [],
    catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
    installed: false,
    installedVersion: null,
    installedOn: [],
    updateAvailable: false,
    ...fields,
  };
}

const Mail = pkg({ id: 'mail', name: 'Mail', version: '1.2.0' });
const JobTracker = pkg({ id: 'job-tracker', name: 'Job Tracker', version: '1.0.0', installed: true, installedVersion: '1.0.0', installedOn: ['job-tracker'] });
// Published minutes ago: only the refresh lists it.
const JobTrackerNewer = { ...JobTracker, version: '1.1.0', updateAvailable: true };
const WhoAmI = pkg({ id: 'whoami', name: 'WhoAmI', kind: 'plugin', version: '0.3.0' });

const read = (packages: MarketplacePackage[]): MarketplaceCatalog => ({ checked: true, reason: null, checkedAt: '2026-10-09T08:00:00Z', packages });

let calls: Call[] = [];
let releaseRefresh: (() => void) | null = null;

/**
 * The Host: `GET /api/marketplace` answers `held`, the refresh answers `refreshed` (a Response) -
 * or, with `hold`, waits until `releaseRefresh` is called, so a spec can look while it is out.
 */
function serve(held: MarketplaceCatalog, refreshed: () => Response, options: { hold?: boolean; installed?: InstalledSolution[] } = {}) {
  calls = [];
  const host = fakeHost(
    [
      (call) => (call.method === 'GET' && call.url === '/api/marketplace' ? reply(200, held) : undefined),
      (call) => (call.method === 'POST' && call.url === '/api/marketplace/refresh' ? refreshed() : undefined),
      ...wizardRoutes({ installed: options.installed }),
    ] as Route[],
    calls,
  );
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      const answer = host(url, init);
      if (!options.hold || url !== '/api/marketplace/refresh') return answer;
      return new Promise<Response>((resolve) => {
        releaseRefresh = () => void answer.then(resolve);
      });
    }),
  );
}

beforeEach(() => resetFirstOpenRefresh());

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
  releaseRefresh = null;
  resetFirstOpenRefresh();
});

async function openTab(name: string) {
  bodyFind(`[data-solutions-tab="${name}"]`)!.click();
  await settle();
}

const shown = () => [...document.body.querySelectorAll('[data-catalog-package]')].map((tile) => tile.getAttribute('data-catalog-package'));
const refreshLoading = () => bodyFind('[data-catalog-refresh]')!.classList.contains('q-btn--loading')
  || bodyFind('[data-catalog-refresh] .q-spinner') !== null;

describe('Marketplace, the first open in a page load', () => {
  it('reads the catalog again once and shows its packages', async () => {
    serve(read([Mail]), () => reply(200, read([Mail, WhoAmI])));

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');

    expect(sent(calls, 'GET', '/api/marketplace')).toHaveLength(1);
    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);
    expect(shown()).toEqual(['mail', 'whoami']);
  });

  it('shows the catalog it has, with the loading indicator, while the refresh is out', async () => {
    serve(read([Mail]), () => reply(200, read([Mail, WhoAmI])), { hold: true });

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');

    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);
    expect(shown()).toEqual(['mail']);
    expect(refreshLoading()).toBe(true);

    releaseRefresh!();
    await settle();

    expect(shown()).toEqual(['mail', 'whoami']);
    expect(refreshLoading()).toBe(false);
  });

  it('does not refresh again when opened a second time in the same page load; Refresh still does', async () => {
    serve(read([Mail]), () => reply(200, read([Mail, WhoAmI])));

    const wrapper = await mountDialog(SolutionsLauncher);
    await settle();
    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();
    await openTab('get-started');

    expect(sent(calls, 'GET', '/api/marketplace')).toHaveLength(2);
    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);

    wrapper.unmount();
    resetBody();
    await mountDialog(SolutionsLauncher);
    await settle();
    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);

    await openTab('get-started');
    bodyFind('[data-catalog-refresh]')!.click();
    await settle();
    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(2);
  });

  it("keeps the catalog shown when the refresh fails, with the Host's sentence as Refresh says it", async () => {
    serve(read([Mail, WhoAmI]), () => reply(502, { error: 'The catalog address did not answer' }));

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');

    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);
    expect(bodyFind('[data-catalog-unreachable]')?.textContent?.trim()).toBe('Could not reach the package catalog: The catalog address did not answer.');
    expect(shown()).toEqual(['mail', 'whoami']);
  });

  it('asks nothing more when the catalog is off, and says why as before', async () => {
    serve({ checked: false, reason: 'marketplace.check is off.', checkedAt: null, packages: [] }, () => reply(200, read([Mail])));

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');

    expect(sent(calls, 'GET', '/api/marketplace')).toHaveLength(1);
    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(0);
    expect(bodyFind('[data-catalog-not-checked]')?.textContent?.trim()).toBe('The package catalog has not been checked: marketplace.check is off.');
    expect(shown()).toEqual([]);
  });

  it('offers on Installed the update only the refreshed catalog lists', async () => {
    serve(read([JobTracker]), () => reply(200, read([JobTrackerNewer])), { installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] });

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('installed');

    expect(sent(calls, 'POST', '/api/marketplace/refresh')).toHaveLength(1);
    const tracker = bodyFind('[data-solution-tile="job-tracker"]')!;
    expect(tracker.querySelector('[data-tile-update-available]')?.textContent?.trim()).toBe('Update available: 1.1.0');
    expect(tracker.querySelector('[data-tile-update]')).not.toBeNull();
  });
});
