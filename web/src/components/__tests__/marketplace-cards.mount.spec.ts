// @vitest-environment happy-dom
//
// MARKETPLACE > BROWSE, A CARD SAYS WHAT A PACKAGE IS: a solution card in the solution colour and a
// plugin card in the plugin colour (edge and kind icon), each opening with the catalog's `contents`
// in bullets - a solution's plugins, members, skills, triggers and sites; a plugin's settings, keys,
// sign-ins, events and skills (a plugin stands alone: it never names a solution) - and ONE Documentation link to
// the package's page on the catalog's own site, in a new tab. A catalog without `contents` shows the
// card as it was. Fixture packages only.
//
// SEEN TO FAIL: before the change no card had a kind class, a contents bullet or a Documentation link.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionsLauncher from '../SolutionsLauncher.vue';
import type { MarketplaceCatalog, MarketplacePackage } from '../../api/types';
import { resetFirstOpenRefresh } from '../../lib/marketplaceRefresh';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, wizardRoutes, type Call } from '../../test/solutionFixtures';

const Site = 'https://catalog.example.test';

function pkg(fields: Partial<MarketplacePackage> & Pick<MarketplacePackage, 'id' | 'name'>): MarketplacePackage {
  return {
    kind: 'solution',
    summary: `${fields.name} does its one job.`,
    description: `Everything ${fields.name} does, at length, for its own page.`,
    version: '1.0.0',
    needs: [],
    catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
    installed: false,
    installedVersion: null,
    installedOn: [],
    updateAvailable: false,
    page: `${Site}/marketplace/${fields.id}`,
    ...fields,
  };
}

const empty = { plugins: [], members: [], skills: [], triggers: [], sites: [], settings: [], secrets: [], connections: [], events: [] };

const SampleSolution = pkg({
  id: 'sample-solution',
  name: 'Sample Solution',
  version: '1.2.0',
  catalogNeeds: { connections: [], secrets: [], inputs: [{ name: 'Notes', kind: 'documents', required: true }], runtimes: [] },
  contents: {
    ...empty,
    plugins: [{ id: 'sample-plugin', version: '2.4.0', minimum: '2.3.0' }],
    members: [{ name: 'Scout', role: 'Finds the work', agent: false }, { name: 'Writer', role: null, agent: true }],
    skills: ['drafting', 'sorting'],
    triggers: [{ name: 'morning', words: 'every weekday at 8:00' }],
    sites: ['Tracker'],
  },
});

const SamplePlugin = pkg({
  id: 'sample-plugin',
  name: 'Sample Plugin',
  kind: 'plugin',
  version: '2.4.0',
  catalogNeeds: { connections: [{ slot: 'mailbox', providers: ['google'], required: true }], secrets: [{ key: 'API_KEY', when: null }], inputs: [], runtimes: [] },
  contents: {
    ...empty,
    skills: ['sample-plugin'],
    settings: [{ name: 'folder', label: 'Folder to watch', kind: 'string', required: true, default: 'Inbox' }],
    secrets: [{ name: 'API_KEY', label: 'Service key', required: true, masked: true }],
    connections: [{ slot: 'mailbox', providers: ['google', 'microsoft'], required: true }],
    events: [{ type: 'sample.found', summary: 'A new item was found' }],
  },
});

// AS A CATALOG WITHOUT THE NEW FIELDS ANSWERS: no contents, no page key at all.
const { page: _page, ...plainFields } = pkg({
  id: 'plain-solution',
  name: 'Plain Solution',
  needs: ['Needs a file in Notes.'],
  catalogNeeds: { connections: [], secrets: [], inputs: [{ name: 'Notes', kind: 'documents', required: true }], runtimes: [] },
});
const PlainSolution = plainFields as MarketplacePackage;

const read = (packages: MarketplacePackage[]): MarketplaceCatalog => ({ checked: true, reason: null, checkedAt: '2026-10-09T08:00:00Z', packages });

let calls: Call[] = [];

function serve(packages: MarketplacePackage[]) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) => (call.method === 'GET' && call.url === '/api/marketplace' ? reply(200, read(packages)) : undefined),
          ...wizardRoutes({ installed: [] }),
        ],
        calls,
      ),
    ),
  );
}

// The first open's refresh is its own spec: here the page load has had it.
beforeEach(() => resetFirstOpenRefresh(true));

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
  resetFirstOpenRefresh();
});

async function browse(packages: MarketplacePackage[]) {
  serve(packages);
  await mountDialog(SolutionsLauncher);
  await settle();
  bodyFind('[data-solutions-tab="get-started"]')!.click();
  await settle();
}

const card = (id: string) => bodyFind(`[data-catalog-package="${id}"]`)!;
const bullets = (id: string) => [...card(id).querySelectorAll('[data-package-content]')].map((li) => li.textContent!.trim());
const docs = (id: string) => [...card(id).querySelectorAll('a')].filter((a) => a.textContent!.trim() === 'Documentation');

/** The shared tile stylesheet, compiled: the kind class is what sets the colour the card wears. */
const tiles = compileString(readFileSync(join(import.meta.dirname, '..', '..', 'css', 'tiles.scss'), 'utf8')).css;

describe('Marketplace > Browse, the package cards', () => {
  it('paints each kind class from its own theme token, on the edge and the kind icon', () => {
    expect(tiles).toMatch(/\.os-tile-kind-solution \{\s*--os-tile-kind: var\(--os-kind-solution\);\s*\}/);
    expect(tiles).toMatch(/\.os-tile-kind-plugin \{\s*--os-tile-kind: var\(--os-kind-plugin\);\s*\}/);
    expect(tiles).toMatch(/\.os-tile-kind-solution,\s*\.os-tile-kind-plugin \{\s*border-left: 4px solid var\(--os-tile-kind\);\s*\}/);
    expect(tiles).toMatch(/\.os-tile-kind-icon \{\s*color: var\(--os-tile-kind, var\(--os-ink-muted\)\);\s*\}/);
  });

  it('shows a solution card in the solution colour, opening with its contents in bullets', async () => {
    await browse([SampleSolution, SamplePlugin]);

    const tile = card('sample-solution');
    expect(tile.classList.contains('os-tile-kind-solution')).toBe(true);
    expect(tile.classList.contains('os-tile-kind-plugin')).toBe(false);
    expect(tile.querySelector('[data-package-kind-icon]')!.classList.contains('os-tile-kind-icon')).toBe(true);
    expect(tile.querySelector('[data-package-name]')!.textContent).toBe('Sample Solution');
    expect(tile.querySelector('[data-package-version]')!.textContent).toBe('1.2.0');

    expect(bullets('sample-solution')).toEqual([
      'Plugin sample-plugin 2.4.0',
      'Member Scout: Finds the work',
      'Member Writer: Agent',
      'Skills: drafting, sorting',
      'Runs every weekday at 8:00',
      'Site Tracker',
    ]);
    // FIRST: the bullets come before the summary.
    const contents = tile.querySelector('[data-package-contents]')!;
    const summary = tile.querySelector('[data-package-summary]')!;
    expect(contents.compareDocumentPosition(summary) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // A solution keeps what it needs at install.
    expect(tile.querySelector('[data-package-needs]')!.textContent).toContain('Needs a file in Notes');
  });

  it('shows a plugin card in the plugin colour, with what a person sets up, its events and skills, and no solution named', async () => {
    await browse([SampleSolution, SamplePlugin]);

    const tile = card('sample-plugin');
    expect(tile.classList.contains('os-tile-kind-plugin')).toBe(true);
    expect(tile.classList.contains('os-tile-kind-solution')).toBe(false);
    expect(tile.querySelector('[data-package-kind-icon]')!.classList.contains('os-tile-kind-icon')).toBe(true);

    expect(bullets('sample-plugin')).toEqual([
      'Setting: Folder to watch (required)',
      'Key: Service key (required)',
      'Sign-in: mailbox (Google or Microsoft) (required)',
      'Announces: A new item was found',
      'Skill: sample-plugin',
    ]);
    // A plugin is independent: its card never names a solution, though one in the catalog ships it.
    expect(tile.textContent).not.toContain('Sample Solution');
    expect(tile.textContent).not.toContain('Included in');
    // The bullets say what it asks for: the short needs lines give way to them.
    expect(tile.querySelector('[data-package-needs]')).toBeNull();
  });

  it('gives each card exactly one Documentation link, to its page on the catalog site, in a new tab', async () => {
    await browse([SampleSolution, SamplePlugin]);

    for (const id of ['sample-solution', 'sample-plugin']) {
      const links = docs(id);
      expect(links, id).toHaveLength(1);
      expect(links[0]!.getAttribute('href')).toBe(`${Site}/marketplace/${id}`);
      expect(links[0]!.getAttribute('target')).toBe('_blank');
      expect(links[0]!.getAttribute('rel')).toContain('noopener');
    }
  });

  it('leaves the long description off the card, and keeps Get', async () => {
    await browse([SampleSolution, SamplePlugin]);

    for (const id of ['sample-solution', 'sample-plugin']) {
      expect(card(id).textContent).not.toContain('at length, for its own page');
      expect(card(id).querySelector('[data-package-get]')!.textContent).toContain('Get');
    }
  });

  it('renders a catalog without contents as before: no bullets, no link, its needs and Get', async () => {
    await browse([PlainSolution]);

    const tile = card('plain-solution');
    expect(tile.classList.contains('os-tile-kind-solution')).toBe(true);
    expect(tile.querySelector('[data-package-contents]')).toBeNull();
    expect(docs('plain-solution')).toHaveLength(0);
    expect(tile.querySelector('[data-package-summary]')!.textContent).toBe('Plain Solution does its one job.');
    expect(tile.querySelector('[data-package-needs]')!.textContent).toContain('Needs a file in Notes');
    expect(tile.querySelector('[data-package-details-toggle]')).not.toBeNull();
    expect(tile.querySelector('[data-package-get]')).not.toBeNull();
  });
});
