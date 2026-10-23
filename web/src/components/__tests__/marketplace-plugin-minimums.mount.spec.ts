// @vitest-environment happy-dom
//
// THE INSTALLED TAB'S PLUGIN LIST NAMES EACH SOLUTION TEAM THAT USES A PLUGIN AND THE MINIMUM IT
// NEEDS: `solutions` on `GET /api/plugins`, one line per team ("Inbox (Fixture Mail 1.0.0) needs
// 2.3.0 or newer"), because a version older than that is refused at install while the team is
// installed. A plugin no solution team uses, and a Host older than the field, show no such line.
//
// SEEN TO FAIL: before the field the tile said only who is hired on the plugin.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { enableAutoUnmount } from '@vue/test-utils';
import SolutionsLauncher from '../SolutionsLauncher.vue';
import { resetFirstOpenRefresh } from '../../lib/marketplaceRefresh';
import { hostPlugin } from '../../test/pluginFixtures';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, wizardRoutes, type Call } from '../../test/solutionFixtures';

enableAutoUnmount(afterEach);

beforeEach(() => resetFirstOpenRefresh(true));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function installedTab(plugins: unknown[]) {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.method === 'GET' && call.url === '/api/marketplace'
              ? reply(200, { checked: true, reason: null, checkedAt: '2026-10-23T08:00:00Z', packages: [] })
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

const lines = (tile: Element) =>
  [...tile.querySelectorAll('[data-plugin-solution]')].map((line) => [line.getAttribute('data-plugin-solution'), line.textContent?.trim()]);

describe('Marketplace Installed: the plugin list', () => {
  it('names each solution team that uses a plugin and the minimum it needs', async () => {
    const legacy = hostPlugin({ id: 'legacy-echo', name: 'Legacy Echo' });
    delete legacy.solutions;

    await installedTab([
      hostPlugin({
        id: 'fixture-mail',
        name: 'Fixture Mail plugin',
        version: '2.4.0',
        members: [{ team: 'inbox', member: 'mailer' }],
        solutions: [
          { team: 'inbox', teamName: 'Inbox', packageId: 'fixture-mail-pack', package: 'Fixture Mail', version: '1.0.0', minimum: '2.3.0' },
          { team: 'desk', teamName: 'Desk', packageId: 'fixture-desk', package: 'Fixture Desk', version: '0.2.0', minimum: '2.4.0' },
        ],
      }),
      hostPlugin({ id: 'echo', name: 'Echo' }),
      legacy,
    ]);

    expect(lines(bodyFind('[data-plugin-tile="fixture-mail"]')!)).toEqual([
      ['inbox', 'Inbox (Fixture Mail 1.0.0) needs 2.3.0 or newer'],
      ['desk', 'Desk (Fixture Desk 0.2.0) needs 2.4.0 or newer'],
    ]);
    expect(lines(bodyFind('[data-plugin-tile="echo"]')!)).toEqual([]);
    expect(lines(bodyFind('[data-plugin-tile="legacy-echo"]')!)).toEqual([]);
  });
});
