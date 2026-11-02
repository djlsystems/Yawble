// @vitest-environment happy-dom
//
// MARKETPLACE INSTALLED: A PLUGIN WITH A NEWER REPLACEMENT IS LISTED UNDER THE REPLACEMENT'S UPDATE
// (PLG 3, rename). Only the newer plugin's own manifest says it replaces an id - an installed plugin's
// `replacedBy`, or a catalog package's `replaces` - never a guess from a name: a catalog plugin of the
// same name that says nothing replaces nothing. The old tile reads "Fixture Cloud 1.1.4 is replaced by
// Fixture Cloud 3.0.0 (fixture-new)" and offers Get (not installed) or Update (installed, newer listed)
// for the replacement instead of its own update; two same-named plugins each show their id.
//
// SEEN TO FAIL on 63506ba: the tile offered only the old id's own update, and no replacement.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { enableAutoUnmount } from '@vue/test-utils';
import SolutionsLauncher from '../SolutionsLauncher.vue';
import { resetFirstOpenRefresh } from '../../lib/marketplaceRefresh';
import { hostPlugin } from '../../test/pluginFixtures';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, wizardRoutes, type Call } from '../../test/solutionFixtures';
import type { MarketplacePackage } from '../../api/types';

enableAutoUnmount(afterEach);

beforeEach(() => resetFirstOpenRefresh(true));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

function listed(fields: Partial<MarketplacePackage> & Pick<MarketplacePackage, 'id' | 'version'>): MarketplacePackage {
  return {
    kind: 'plugin', name: 'Fixture Cloud', summary: 'Cloud.', description: '', needs: [],
    catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
    installed: false, installedVersion: null, installedOn: [], updateAvailable: false, replaces: [],
    ...fields,
  };
}

async function installedTab(plugins: unknown[], packages: MarketplacePackage[]) {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.method === 'GET' && call.url === '/api/marketplace'
              ? reply(200, { checked: true, reason: null, checkedAt: '2026-11-02T08:00:00Z', packages })
              : undefined,
          ...wizardRoutes({ installed: [], plugins }),
        ],
        calls,
      ),
    ),
  );
  await mountDialog(SolutionsLauncher);
  await settle();
  bodyFind('[data-solutions-tab="installed"]')!.click();
  await settle();
}

const tile = (id: string) => bodyFind(`[data-plugin-tile="${id}"]`)!;

describe('Marketplace Installed: a plugin with a newer replacement', () => {
  it('is listed under the catalog replacement it does not have yet, with Get, and not its own update', async () => {
    await installedTab(
      [hostPlugin({ id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4' })],
      [
        listed({ id: 'fixture-old', version: '1.1.5', installed: true, installedVersion: '1.1.4', updateAvailable: true }),
        listed({ id: 'fixture-new', version: '3.0.0', replaces: ['fixture-old'] }),
      ],
    );

    expect(tile('fixture-old').querySelector('[data-tile-replaced-by="fixture-new"]')!.textContent!.trim())
      .toBe('Fixture Cloud 1.1.4 is replaced by Fixture Cloud 3.0.0 (fixture-new).');
    const get = tile('fixture-old').querySelector('[data-plugin-replacement-get]')!;
    expect(get.getAttribute('aria-label')).toBe('Get Fixture Cloud 3.0.0 (fixture-new)');
    expect(tile('fixture-old').querySelector('[data-tile-update-available]')).toBeNull();
    expect(tile('fixture-old').querySelector('[data-plugin-update]')).toBeNull();
  });

  it("is listed under an installed replacement, with that one's newer version as Update, and each same-named tile shows its id", async () => {
    await installedTab(
      [
        hostPlugin({ id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4', replacedBy: [{ id: 'fixture-new', name: 'Fixture Cloud', version: '3.0.0' }] }),
        hostPlugin({ id: 'fixture-new', name: 'Fixture Cloud', version: '3.0.0', replaces: [{ id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4', members: [] }] }),
      ],
      [listed({ id: 'fixture-new', version: '3.1.0', installed: true, installedVersion: '3.0.0', updateAvailable: true, replaces: ['fixture-old'] })],
    );

    expect(tile('fixture-old').querySelector('[data-tile-replaced-by]')!.textContent).toContain(
      'Fixture Cloud 1.1.4 is replaced by Fixture Cloud 3.1.0 (fixture-new)');
    expect(tile('fixture-old').querySelector('[data-plugin-replacement-get]')!.getAttribute('aria-label')).toBe('Update Fixture Cloud 3.1.0 (fixture-new)');
    expect(tile('fixture-old').querySelector('[data-tile-id]')!.textContent).toBe('fixture-old');
    expect(tile('fixture-new').querySelector('[data-tile-id]')!.textContent).toBe('fixture-new');
    expect(tile('fixture-new').querySelector('[data-tile-replaced-by]')).toBeNull();
  });

  it('is never guessed from a name: a same-named catalog plugin that says nothing replaces nothing', async () => {
    await installedTab(
      [hostPlugin({ id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4' }), hostPlugin({ id: 'fixture-mail', name: 'Fixture Mail', version: '1.0.0' })],
      [listed({ id: 'fixture-old-plugin', version: '3.0.0' })],
    );

    expect(tile('fixture-old').querySelector('[data-tile-replaced-by]')).toBeNull();
    expect(tile('fixture-old').querySelector('[data-plugin-replacement-get]')).toBeNull();
    // A name nobody else installed has reads as before: no id line.
    expect(tile('fixture-mail').querySelector('[data-tile-id]')).toBeNull();
  });
});
