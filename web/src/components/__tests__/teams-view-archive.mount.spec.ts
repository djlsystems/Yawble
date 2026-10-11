// @vitest-environment happy-dom
//
// ARCHIVED TEAMS ON THE TEAMS LIST, MOUNTED: the two checkboxes and what they remember, an archived
// row's status and actions, the Archive confirmation and its refusal, and Delete's refusal when a
// team is not quiet. Every server is a mock.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const api = vi.hoisted(() => ({
  archiveCheck: vi.fn(),
  archiveTeam: vi.fn(),
  unarchiveTeam: vi.fn(),
  cloneTeam: vi.fn(),
  deleteTeam: vi.fn(),
  getWip: vi.fn(),
  listLocalRepos: vi.fn(),
  listRemovals: vi.fn(),
  pauseTeam: vi.fn(),
  resumeTeam: vi.fn(),
  retryRemoval: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: api.notify }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  archiveCheck: api.archiveCheck,
  archiveTeam: api.archiveTeam,
  unarchiveTeam: api.unarchiveTeam,
  cloneTeam: api.cloneTeam,
  deleteTeam: api.deleteTeam,
  getWip: api.getWip,
  listLocalRepos: api.listLocalRepos,
  listRemovals: api.listRemovals,
  pauseTeam: api.pauseTeam,
  resumeTeam: api.resumeTeam,
  retryRemoval: api.retryRemoval,
}));

import TeamsView from '../TeamsView.vue';
import { useConsoleStore } from '../../stores/console';
import type { Team, TeamId } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const NotQuiet = 'Developer is running; Manager has 2 instructions queued';

const aTeam = (id: string, extra: Partial<Team> = {}) => ({
  id: id as TeamId,
  name: id[0]!.toUpperCase() + id.slice(1),
  paused: false,
  containers: [],
  ...extra,
}) as unknown as Team;

const active = aTeam('alpha');
const archived = aTeam('beta', { archived: true, paused: true, archivedAt: '2026-10-01T09:00:00Z', archivedBy: 'dana@example.com' });

let board: ReturnType<typeof useConsoleStore>;

async function mountView(teams: Team[] = [active, archived]) {
  setActivePinia(createPinia());
  board = useConsoleStore();
  board.$patch({ teams });
  vi.spyOn(board, 'refresh').mockResolvedValue();
  vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});
  vi.spyOn(board, 'refreshForTeamDeleted').mockResolvedValue();

  const wrapper = mount(TeamsView, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

/** The rendered rows' team ids, in table order. */
const rowIds = () => [...document.body.querySelectorAll('tr[data-team]')].map((row) => row.getAttribute('data-team'));

const row = (id: string) => document.body.querySelector(`tr[data-team="${id}"]`) as HTMLElement;

/** The labels of a row's action buttons, by aria-label or visible label. */
function actionsOf(id: string): string[] {
  return [...row(id).querySelectorAll('[data-col-actions] button')].map((button) =>
    button.getAttribute('aria-label') ?? button.querySelector('.block')?.textContent?.trim() ?? '');
}

function buttonIn(scope: ParentNode, label: string): HTMLButtonElement {
  const found = [...scope.querySelectorAll('button')].find(
    (candidate) => candidate.getAttribute('aria-label') === label
      || candidate.querySelector('.block')?.textContent?.trim() === label,
  );
  if (!found) throw new Error(`no "${label}" button rendered`);
  return found as HTMLButtonElement;
}

async function tick(which: 'active' | 'archived') {
  (document.body.querySelector(`[data-teams-shown="${which}"]`) as HTMLElement).click();
  await flushPromises();
}

const cardText = (selector: string) => document.body.querySelector(selector)?.textContent?.replace(/\s+/g, ' ') ?? '';

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // No network: anything not mocked above (the Activity tile's reads) is refused here.
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  api.getWip.mockResolvedValue({ max: 4, running: [], waiting: [] });
  api.listRemovals.mockResolvedValue([]);
  api.listLocalRepos.mockResolvedValue([]);
  api.archiveCheck.mockResolvedValue({ quiet: true, reason: null, openWorkflows: [] });
  api.archiveTeam.mockResolvedValue(undefined);
  api.unarchiveTeam.mockResolvedValue(undefined);
});

afterEach(() => {
  resetBody();
  vi.unstubAllGlobals();
});

describe('the Show active and Show archived checkboxes', () => {
  it('list active teams only at first: Show active ticked, Show archived not', async () => {
    await mountView();

    expect(rowIds()).toEqual(['alpha']);
    expect(document.body.querySelector('[data-teams-shown="active"]')?.getAttribute('aria-checked')).toBe('true');
    expect(document.body.querySelector('[data-teams-shown="archived"]')?.getAttribute('aria-checked')).toBe('false');
  });

  it('add archived teams to the list, sorted with the rest, when Show archived is ticked', async () => {
    await mountView([aTeam('gamma'), archived, active]);
    await tick('archived');

    expect(rowIds()).toEqual(['alpha', 'beta', 'gamma']);
  });

  it('say to tick one when both are unticked', async () => {
    await mountView();
    await tick('active');

    expect(rowIds()).toEqual([]);
    expect(document.body.querySelector('[data-teams-shown-none]')?.textContent?.trim()).toBe('Tick Show active or Show archived');
  });

  it('are remembered by this browser for the next mount', async () => {
    const first = await mountView();
    await tick('archived');
    await tick('active');
    first.unmount();
    resetBody();

    await mountView();

    expect(rowIds()).toEqual(['beta']);
    expect(document.body.querySelector('[data-teams-shown="archived"]')?.getAttribute('aria-checked')).toBe('true');
    expect(document.body.querySelector('[data-teams-shown="active"]')?.getAttribute('aria-checked')).toBe('false');
  });
});

describe('an archived row', () => {
  it('reads ARCHIVED in Status and offers Unarchive, Clone and Delete, with no Pause or Resume', async () => {
    await mountView();
    await tick('archived');

    expect(row('beta').querySelector('[data-col-status]')?.textContent?.trim()).toBe('ARCHIVED');
    expect(actionsOf('beta')).toEqual(['Unarchive', 'Clone this team', 'Delete this team']);
  });

  it('offers Archive beside Delete on an active row', async () => {
    await mountView();

    expect(actionsOf('alpha')).toEqual(['Pause', 'Clone this team', 'Archive this team', 'Delete this team']);
  });

  it('unarchives through the route and says the team comes back paused', async () => {
    await mountView();
    await tick('archived');
    buttonIn(row('beta'), 'Unarchive').click();
    await flushPromises();

    expect(api.unarchiveTeam).toHaveBeenCalledWith('beta');
    expect(board.refresh).toHaveBeenCalled();
    expect(api.notify).toHaveBeenCalledWith(expect.objectContaining({
      type: 'positive',
      message: 'Beta is back, paused. Nothing runs until someone resumes it.',
    }));
  });

  it('opens the Clone and Delete dialogs on the archived team', async () => {
    await mountView();
    await tick('archived');

    buttonIn(row('beta'), 'Clone this team').click();
    await flushPromises();
    expect(cardText('.teams-clone-card')).toContain('Clone Beta?');
    buttonIn(document.body.querySelector('.teams-clone-card')!, 'Cancel').click();
    await flushPromises();

    buttonIn(row('beta'), 'Delete this team').click();
    await flushPromises();
    expect(cardText('.teams-confirm-card')).toContain('Delete Beta?');
  });
});

describe('the Archive confirmation', () => {
  it('lists the open workflows, says what archiving does and that Unarchive brings it back paused', async () => {
    api.archiveCheck.mockResolvedValue({
      quiet: true,
      reason: null,
      openWorkflows: [{ workflow: 10557, title: 'Archive a team' }, { workflow: 10560, title: 'Tidy the board' }],
    });
    await mountView();
    buttonIn(row('alpha'), 'Archive this team').click();
    await flushPromises();

    expect(api.archiveCheck).toHaveBeenCalledWith('alpha');
    const text = cardText('.teams-archive-card');
    expect(text).toContain('Archive Alpha?');
    expect(text).toContain('it does no work, takes no instructions, and leaves the tabs and the Kanban');
    expect(text).toContain('Unarchive brings the team back paused: nothing runs until someone resumes it.');
    const listed = [...document.body.querySelectorAll('[data-archive-open-workflows] li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(listed).toEqual(['#10557 Archive a team', '#10560 Tidy the board']);

    buttonIn(document.body.querySelector('.teams-archive-card')!, 'Archive team').click();
    await flushPromises();
    expect(api.archiveTeam).toHaveBeenCalledWith('alpha');
    expect(board.refresh).toHaveBeenCalled();
  });

  it('shows a 409 refusal as its sentence, in the dialog', async () => {
    api.archiveTeam.mockRejectedValue(Object.assign(new Error(NotQuiet), { status: 409 }));
    await mountView();
    buttonIn(row('alpha'), 'Archive this team').click();
    await flushPromises();
    buttonIn(document.body.querySelector('.teams-archive-card')!, 'Archive team').click();
    await flushPromises();

    expect(document.body.querySelector('[data-archive-refusal]')?.textContent?.trim()).toBe(NotQuiet);
    expect(cardText('.teams-archive-card')).toContain('Archive Alpha?');
    expect(board.refresh).not.toHaveBeenCalled();
  });

  it('says what is still going when the check finds the team not quiet', async () => {
    api.archiveCheck.mockResolvedValue({ quiet: false, reason: NotQuiet, openWorkflows: [] });
    await mountView();
    buttonIn(row('alpha'), 'Archive this team').click();
    await flushPromises();

    expect(document.body.querySelector('[data-archive-refusal]')?.textContent?.trim()).toBe(NotQuiet);
  });
});

describe('the Delete dialog when the team is not quiet', () => {
  it('shows the 409 sentence in the dialog rather than a toast', async () => {
    api.deleteTeam.mockRejectedValue(Object.assign(new Error(NotQuiet), { status: 409 }));
    await mountView();
    await tick('archived');
    buttonIn(row('beta'), 'Delete this team').click();
    await flushPromises();
    buttonIn(document.body.querySelector('.teams-confirm-card')!, 'Delete team').click();
    await flushPromises();

    expect(api.deleteTeam).toHaveBeenCalledWith('beta', undefined);
    expect(document.body.querySelector('[data-delete-refusal]')?.textContent?.trim()).toBe(NotQuiet);
    expect(api.notify).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'negative' }));
  });
});
