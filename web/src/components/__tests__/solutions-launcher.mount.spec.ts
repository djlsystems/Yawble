// @vitest-environment happy-dom
//
// THE SOLUTIONS LAUNCHER: one tile per installed solution - name, version, team, status line
// and a state badge - with Open only when the package has a primary site (a real link, new tab) and
// Manage. Package text is text: a status that looks like HTML makes no element. Nothing installed
// explains how solutions arrive.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import SolutionsLauncher from '../SolutionsLauncher.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call } from '../../test/solutionFixtures';
import { HtmlLooking, launcherRow } from '../../test/solutionPanelFixtures';
import type { InstalledSolution } from '../../api/types';
import { localTime } from '../../lib/localTime';

let calls: Call[] = [];

function serve(rows: InstalledSolution[]) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(fakeHost([(call) => (call.method === 'GET' && call.url === '/api/solutions/installed' ? reply(200, rows) : undefined)], calls)),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function launcher(rows: InstalledSolution[]) {
  serve(rows);
  const wrapper = await mountDialog(SolutionsLauncher);
  await settle();
  return wrapper;
}

const tile = (team: string) => bodyFind(`[data-solution-tile="${team}"]`)!;

describe('the solutions launcher', () => {
  it('shows one tile per installed solution with its name, version, team, status and state', async () => {
    await launcher([
      launcherRow(),
      launcherRow({
        team: 'news',
        teamName: 'News desk',
        id: 'news',
        name: 'News',
        version: '0.3.0',
        state: { kind: 'blocked', reason: 'Upload a file to Resume/' },
        status: 'No runs yet · blocked',
      }),
    ]);

    expect(sent(calls, 'GET', '/api/solutions/installed')).toHaveLength(1);
    const first = tile('job-tracker');
    expect(first.querySelector('[data-tile-name]')?.textContent).toBe('Job Tracker');
    expect(first.querySelector('[data-tile-version]')?.textContent).toBe('1.1.0');
    expect(first.querySelector('[data-tile-team]')?.textContent).toContain('Job Tracker');
    expect(first.querySelector('[data-tile-status]')?.textContent).toBe(`3 new jobs · last checked ${localTime(Date.parse('2026-09-30T08:00:00Z'), { date: true })}`);
    expect(first.querySelector('[data-tile-state="idle"]')?.textContent).toContain('Idle');

    const second = tile('news');
    expect(second.querySelector('[data-tile-team]')?.textContent).toContain('News desk');
    expect(second.querySelector('[data-tile-state="blocked"]')?.textContent).toContain('Blocked: Upload a file to Resume/');
  });

  it.each([
    ['running', null, 'Running'],
    ['paused', null, 'Paused'],
    ['capped', 'Scan for postings', 'Capped today: Scan for postings'],
  ] as const)('badges a %s solution in words', async (kind, reason, words) => {
    await launcher([launcherRow({ state: { kind, reason } })]);
    expect(tile('job-tracker').querySelector(`[data-tile-state="${kind}"]`)?.textContent).toContain(words);
  });

  it('opens the primary site in a new tab, and has no Open without one', async () => {
    await launcher([launcherRow(), launcherRow({ team: 'bare', teamName: 'Bare', primarySite: null })]);

    const open = tile('job-tracker').querySelector<HTMLAnchorElement>('a[data-tile-open]');
    expect(open?.getAttribute('href')).toBe('/sites/job-tracker/tracker/');
    expect(open?.getAttribute('target')).toBe('_blank');
    expect(open?.getAttribute('rel')).toContain('noopener');
    expect(tile('bare').querySelector('[data-tile-open]')).toBeNull();
    expect(tile('bare').querySelector('[data-tile-manage]')).not.toBeNull();
  });

  it('disables Open for an unpublished site', async () => {
    await launcher([launcherRow({ primarySite: { name: 'tracker', url: '/sites/job-tracker/tracker/', published: false } })]);

    const open = tile('job-tracker').querySelector('[data-tile-open]')!;
    expect(open.getAttribute('href')).toBeNull();
    expect(open.classList.contains('disabled') || open.hasAttribute('disabled') || open.getAttribute('aria-disabled') === 'true').toBe(true);
  });

  it('emits manage with the team when Manage is pressed', async () => {
    const wrapper = await launcher([launcherRow()]);

    tile('job-tracker').querySelector<HTMLElement>('[data-tile-manage]')!.click();
    await settle();

    expect(wrapper.emitted('manage')).toEqual([['job-tracker']]);
  });

  it('renders a status and a name that look like HTML as their characters', async () => {
    await launcher([launcherRow({ name: '<i>Tracker</i>', status: HtmlLooking, state: { kind: 'blocked', reason: '<b>upload</b>' } })]);

    const first = tile('job-tracker');
    expect(first.querySelector('[data-tile-status]')?.textContent).toBe(HtmlLooking);
    expect(first.querySelector('[data-tile-name]')?.textContent).toBe('<i>Tracker</i>');
    expect(first.querySelector('img')).toBeNull();
    expect(first.querySelector('b')).toBeNull();
    expect(first.querySelector('[data-tile-name] i')).toBeNull();
    expect(first.querySelector('[data-tile-state]')?.textContent).toContain('<b>upload</b>');
  });

  it('cuts a long name inside its tile, keeping Open and Manage in the tile', async () => {
    const long = 'Customer Onboarding and Support Knowledge Base Assistant Pro';
    expect(long).toHaveLength(60);
    await launcher([launcherRow({ name: long }), launcherRow({ team: 'news', teamName: 'News desk' })]);

    const first = tile('job-tracker');
    const name = first.querySelector('[data-tile-name]')!;
    expect(name.textContent).toBe(long);
    expect(name.getAttribute('title')).toBe(long);
    expect([...name.classList]).toEqual(expect.arrayContaining(['ellipsis', 'solution-tile-name']));
    expect(first.classList).toContain('solution-tile');
    // Quasar's `row` and `column` WRAP: in a wrapping flex box each line is as wide as its content,
    // so the name would never shrink and Open and Manage would sit outside the tile. The name's own
    // flex line is the title row, which carries `no-wrap`; no wrapping box sits between them. The
    // shared head above it may wrap: the title row is one item on it and shrinks as one.
    const title = name.closest('.solution-tile-title')!;
    expect(title).not.toBeNull();
    expect(first.contains(title)).toBe(true);
    expect(title.classList).toContain('no-wrap');
    const wrapping: string[] = [];
    for (let el: Element | null = name; el; el = el.parentElement) {
      const flex = el.classList.contains('row') || el.classList.contains('column');
      if (flex && !el.classList.contains('no-wrap')) wrapping.push(el.className);
      if (el === title) break;
    }
    expect(wrapping).toEqual([]);
    // The tile is the shared one, and the shared tile is a column that never wraps.
    expect([...first.classList]).toEqual(expect.arrayContaining(['os-tile', 'solution-tile']));
    const tiles = readFileSync(join(import.meta.dirname, '../../css/tiles.scss'), 'utf8');
    const shared = (selector: string) => new RegExp(`\\${selector}\\s*\\{[^}]*\\}`).exec(tiles)?.[0] ?? '';
    expect(shared('.os-tile')).toMatch(/flex-direction:\s*column/);
    expect(shared('.os-tile')).not.toMatch(/flex-wrap:\s*wrap/);
    // The actions are the tile's own children, not pushed into a neighbour.
    expect(first.querySelector('[data-tile-open]')).not.toBeNull();
    expect(first.querySelector('[data-tile-manage]')).not.toBeNull();

    // Scoped styles do not reach the test DOM, so the rules that make the name shrink are read
    // from the component: without min-width 0 a grid item and a flex item grow to their content.
    const source = readFileSync(join(import.meta.dirname, '../SolutionsLauncher.vue'), 'utf8');
    const rule = (selector: string) => new RegExp(`\\${selector}\\s*\\{[^}]*\\}`).exec(source)?.[0] ?? '';
    expect(rule('.solution-tile')).toMatch(/min-width:\s*0/);
    expect(rule('.solution-tile')).toMatch(/overflow:\s*hidden/);
    expect(rule('.solution-tile-title')).toMatch(/min-width:\s*0/);
    expect(rule('.solution-tile-name')).toMatch(/min-width:\s*0/);
    // A phone narrower than one column still gets a tile that fits it: the column's least width
    // is capped at the grid's own, and the shared grid builds its columns from that width.
    expect(rule('.solutions-tiles')).toMatch(/--os-tile-min:\s*min\(\d+rem, 100%\)/);
    expect(shared('.os-tiles')).toMatch(/minmax\(var\(--os-tile-min/);
  });

  it('explains how solutions arrive when none is installed', async () => {
    await launcher([]);

    expect(bodyFind('[data-solutions-empty]')).not.toBeNull();
    expect(bodyText()).toContain('Ask the Concierge');
    expect(bodyText()).toContain('Install from a folder');
    expect(bodyFind('[data-install-from-folder]')).not.toBeNull();
  });

  it('lays the tiles out on the shared grid: head, lines, then the actions', async () => {
    await launcher([launcherRow(), launcherRow({ team: 'news', teamName: 'News desk' })]);

    const grid = bodyFind('.os-tiles.solutions-tiles')!;
    expect(grid).not.toBeNull();
    const tiles = [...grid.children];
    expect(tiles.map((el) => el.getAttribute('data-solution-tile'))).toEqual(['job-tracker', 'news']);
    for (const el of tiles) expect(el.classList).toContain('os-tile');

    const first = tile('job-tracker');
    const head = first.querySelector('.os-tile-head')!;
    expect(head.querySelector('[data-tile-name]')).not.toBeNull();
    expect(head.querySelector('[data-tile-version]')).not.toBeNull();
    expect(first.querySelector('.os-tile-line[data-tile-team]')).not.toBeNull();
    expect(first.querySelector('.os-tile-line[data-tile-status]')).not.toBeNull();

    // In reading order: the head, the team, the badge, the status, then Open, Details and Manage.
    const order = ['[data-tile-name]', '[data-tile-team]', '[data-tile-state]', '[data-tile-status]', '[data-tile-open]', '[data-tile-details]', '[data-tile-manage]']
      .map((selector) => first.querySelector(selector)!);
    for (let i = 1; i < order.length; i++) {
      expect(order[i - 1]!.compareDocumentPosition(order[i]!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    }
  });

  describe('the filter', () => {
    const rows = () => [
      launcherRow(),
      launcherRow({ team: 'news', teamName: 'News desk', id: 'news', name: 'News', version: '0.3.0', status: 'Two stories drafted', state: { kind: 'paused', reason: null }, primarySite: { name: 'paper', url: '/sites/news/paper/', published: false } }),
      launcherRow({ team: 'support', teamName: 'Front Desk', id: 'helpdesk', name: 'Helpdesk', status: 'Five tickets open', state: { kind: 'blocked', reason: 'Upload a file to Resume/' } }),
    ];

    const shown = () => [...document.body.querySelectorAll('[data-solution-tile]')].map((el) => el.getAttribute('data-solution-tile'));

    async function type(text: string) {
      const input = document.body.querySelector<HTMLInputElement>('input[data-solutions-filter], [data-solutions-filter] input')!;
      input.value = text;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      await settle();
    }

    // Quasar puts a select's own attributes on its focus target, inside the component.
    const select = (wrapper: Awaited<ReturnType<typeof launcher>>, marker: string) =>
      wrapper.findAllComponents({ name: 'QSelect' }).find((picker) => picker.find(`[${marker}]`).exists())!;

    async function pick(wrapper: Awaited<ReturnType<typeof launcher>>, marker: string, value: string | null) {
      select(wrapper, marker).vm.$emit('update:modelValue', value);
      await settle();
    }

    it('narrows by a word of the name, the team or the status line', async () => {
      await launcher(rows());
      expect(shown()).toEqual(['job-tracker', 'news', 'support']);

      await type('HELPdesk');
      expect(shown()).toEqual(['support']);
      await type('news desk');
      expect(shown()).toEqual(['news']);
      await type('tickets');
      expect(shown()).toEqual(['support']);
    });

    it('narrows by a team, by a state, and by all three together', async () => {
      const wrapper = await launcher(rows());

      await pick(wrapper, 'data-solutions-filter-team', 'news');
      expect(shown()).toEqual(['news']);
      await pick(wrapper, 'data-solutions-filter-team', null);

      await pick(wrapper, 'data-solutions-filter-state', 'blocked');
      expect(shown()).toEqual(['support']);

      await pick(wrapper, 'data-solutions-filter-team', 'support');
      await type('five');
      expect(shown()).toEqual(['support']);
      await type('drafted');
      expect(shown()).toEqual([]);
    });

    it('offers only the teams and states the tiles have', async () => {
      const wrapper = await launcher(rows());

      expect(select(wrapper, 'data-solutions-filter-team').props('options')).toEqual([
        { label: 'Front Desk', value: 'support' },
        { label: 'Job Tracker', value: 'job-tracker' },
        { label: 'News desk', value: 'news' },
      ]);
      const states = (select(wrapper, 'data-solutions-filter-state').props('options') as { value: string }[]).map((option) => option.value);
      expect(states).toEqual(['idle', 'blocked', 'paused']);
      expect(states).not.toContain('running');
    });

    it('narrows what is shown only: nothing is fetched or sent, and Manage still names its team', async () => {
      const wrapper = await launcher(rows());

      await type('news');
      await pick(wrapper, 'data-solutions-filter-team', 'news');
      await pick(wrapper, 'data-solutions-filter-state', 'paused');
      tile('news').querySelector<HTMLElement>('[data-tile-manage]')!.click();
      await settle();
      await type('');
      await pick(wrapper, 'data-solutions-filter-team', null);
      await pick(wrapper, 'data-solutions-filter-state', null);

      expect(calls.map((call) => `${call.method} ${call.url}`)).toEqual(['GET /api/solutions/installed']);
      expect(wrapper.emitted('manage')).toEqual([['news']]);
    });

    it('brings hidden tiles back unchanged when cleared', async () => {
      const wrapper = await launcher(rows());
      const before = shown();

      await type('helpdesk');
      await pick(wrapper, 'data-solutions-filter-state', 'blocked');
      expect(shown()).toEqual(['support']);
      document.body.querySelector<HTMLElement>('.solutions-filter-text .q-field__focusable-action')!.click();
      await settle();
      await pick(wrapper, 'data-solutions-filter-state', null);

      expect(shown()).toEqual(before);
      expect(tile('job-tracker').querySelector('a[data-tile-open]')?.getAttribute('href')).toBe('/sites/job-tracker/tracker/');
      const unpublished = tile('news').querySelector('[data-tile-open]')!;
      expect(unpublished.getAttribute('href')).toBeNull();
      expect(unpublished.classList.contains('disabled') || unpublished.hasAttribute('disabled') || unpublished.getAttribute('aria-disabled') === 'true').toBe(true);
    });

    it('says when no tile matches, and never says nothing is installed', async () => {
      await launcher(rows());

      await type('nothing-is-called-this');
      expect(shown()).toEqual([]);
      expect(bodyFind('[data-solutions-none-match]')?.textContent).toContain('No solution matches the filter.');
      expect(bodyFind('[data-solutions-empty]')).toBeNull();
    });

    it('is not offered with nothing installed: the empty state shows, and no none-match line', async () => {
      await launcher([]);

      expect(bodyFind('[data-solutions-empty]')).not.toBeNull();
      expect(bodyFind('[data-solutions-none-match]')).toBeNull();
      expect(bodyFind('[data-solutions-filter]')).toBeNull();
    });
  });

  it('opens Details with the package, version, team, folder, who installed it, plugins and status, as text', async () => {
    await launcher([launcherRow({ status: HtmlLooking, plugins: ['job-board', 'mailer'] })]);

    tile('job-tracker').querySelector<HTMLElement>('[data-tile-details]')!.click();
    await settle();

    const details = bodyFind('[data-solution-details]')!;
    expect(details).not.toBeNull();
    expect(details.classList).toContain('os-dialog-md');
    const detail = (name: string) => details.querySelector(`[data-detail="${name}"]`)?.textContent?.trim();
    expect(detail('package')).toBe('job-tracker');
    expect(detail('version')).toBe('1.1.0');
    expect(detail('team')).toBe('Job Tracker');
    expect(detail('folder')).toBe('/data/documents/packages/job-tracker');
    expect(detail('installed-by')).toBe('dana@example.com');
    expect(detail('plugins')).toBe('job-board, mailer');
    expect(detail('site')).toContain('tracker');
    expect(detail('state')).toBe('Idle');
    expect(details.querySelector('[data-detail="status"]')?.textContent).toBe(HtmlLooking);
    expect(details.querySelector('img')).toBeNull();
    expect(details.querySelector('b')).toBeNull();
  });

  it('names the Review and install link a built package brings, and where it appears', async () => {
    await launcher([]);

    const empty = bodyFind('[data-solutions-empty]')!;
    const text = empty.textContent?.replace(/\s+/g, ' ') ?? '';
    expect(text).toContain('A solution is a package a team built');
    expect(text).toContain('Review and install');
    expect(text).toContain('Activity feed');
    expect(text).toContain('backlog item');
    // No link of its own: with nothing installed there is no package to link to.
    expect(empty.querySelector('a')).toBeNull();

    bodyFind('[data-install-from-folder]')!.click();
    await settle();
    expect(bodyFind('[data-install-dialog]')?.textContent).toContain('a folder holding its solution.json');
  });

  it('says why when the list cannot be read', async () => {
    calls = [];
    vi.stubGlobal('fetch', vi.fn(fakeHost([() => reply(500, { error: 'The Host is starting.' })], calls)));
    await mountDialog(SolutionsLauncher);
    await settle();

    expect(bodyFind('[data-launcher-problem]')?.textContent).toContain('The Host is starting.');
  });
});
