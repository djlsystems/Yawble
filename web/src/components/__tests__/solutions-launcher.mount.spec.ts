// @vitest-environment happy-dom
//
// THE SOLUTIONS LAUNCHER: one tile per installed solution - name, version, team, status line
// and a state badge - with Open only when the package has a primary site (a real link, new tab) and
// Manage. Package text is text: a status that looks like HTML makes no element. Nothing installed
// explains how solutions arrive.
import { afterEach, describe, expect, it, vi } from 'vitest';

import SolutionsLauncher from '../SolutionsLauncher.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call } from '../../test/solutionFixtures';
import { HtmlLooking, launcherRow } from '../../test/solutionPanelFixtures';
import type { InstalledSolution } from '../../api/types';

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
    expect(first.querySelector('[data-tile-status]')?.textContent).toBe('3 new jobs · last checked 2026-09-30T08:00:00Z');
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

  it('explains how solutions arrive when none is installed', async () => {
    await launcher([]);

    expect(bodyFind('[data-solutions-empty]')).not.toBeNull();
    expect(bodyText()).toContain('Ask the Concierge');
    expect(bodyText()).toContain('Install from a folder');
    expect(bodyFind('[data-install-from-folder]')).not.toBeNull();
  });

  it('says why when the list cannot be read', async () => {
    calls = [];
    vi.stubGlobal('fetch', vi.fn(fakeHost([() => reply(500, { error: 'The Host is starting.' })], calls)));
    await mountDialog(SolutionsLauncher);
    await settle();

    expect(bodyFind('[data-launcher-problem]')?.textContent).toContain('The Host is starting.');
  });
});
