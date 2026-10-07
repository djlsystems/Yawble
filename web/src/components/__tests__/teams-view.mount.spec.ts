// @vitest-environment happy-dom
//
// THE TEAMS TABLE, MOUNTED. Its rules (sort, status, the clone name) are `lib/teamsTable.ts`'s and
// tested there; what is left for this file is what a person reads in the two confirmations and
// what the row buttons actually call.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { cloneTeam, deleteTeam, getWip, listLocalRepos, listRemovals, pauseTeam, resumeTeam, retryRemoval, notify } = vi.hoisted(() => ({
  cloneTeam: vi.fn(),
  listLocalRepos: vi.fn(),
  listRemovals: vi.fn(),
  retryRemoval: vi.fn(),
  getWip: vi.fn(),
  deleteTeam: vi.fn(),
  pauseTeam: vi.fn(),
  resumeTeam: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  cloneTeam,
  deleteTeam,
  getWip,
  listLocalRepos,
  listRemovals,
  pauseTeam,
  resumeTeam,
  retryRemoval,
}));

import TeamsView from '../TeamsView.vue';
import { TeamDeletionConfirmationRequired } from '../../api/client';
import { useConsoleStore } from '../../stores/console';
import type { Team, TeamId } from '../../api/types';
import { bodyText, resetBody } from '../../test/mountQuasar';

const aTeam = (id: string, paused = false) => ({
  id: id as TeamId,
  name: id[0]!.toUpperCase() + id.slice(1),
  paused,
  containers: [],
}) as unknown as Team;

let board: ReturnType<typeof useConsoleStore>;

async function mountView(teams: Team[]) {
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

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')].find(
    // A Quasar label sits in `.block`, apart from the icon's ligature text ("pause" + "Pause").
    (candidate) => candidate.getAttribute('aria-label') === label
      || candidate.querySelector('.block')?.textContent?.trim() === label,
  );
  if (!found) throw new Error(`no "${label}" button rendered`);
  return found as HTMLButtonElement;
}

async function click(label: string) {
  button(label).click();
  await flushPromises();
}

/** The dialog's own card, so a phrase is asserted where a person reads it and not in a comment. */
function cardText(selector: string): string {
  return document.body.querySelector(selector)?.textContent?.replace(/\s+/g, ' ') ?? '';
}

beforeEach(() => {
  vi.clearAllMocks();
  pauseTeam.mockResolvedValue(undefined);
  resumeTeam.mockResolvedValue(undefined);
  getWip.mockResolvedValue({ max: 4, running: [], waiting: [] });
  listRemovals.mockResolvedValue([]);
  listLocalRepos.mockResolvedValue([]);
});

afterEach(resetBody);

describe('the team clone dialog', () => {
  it('lists the source settings the clone carries', async () => {
    await mountView([aTeam('alpha')]);
    await click('Clone this team');

    const text = cardText('.teams-clone-card');
    expect(text).toContain('Clone Alpha?');
    expect(text).toContain('its Manager Agent and member-agent allowlist');
    expect(text).toContain('its repositories');
    expect(text).toContain('its environment variables, except the admin credentials');
    expect(text).toContain('its roster — the same Manager and members, with the same hired_for tags');
  });

  it('says paused and admin credentials are not carried', async () => {
    await mountView([aTeam('alpha')]);
    await click('Clone this team');

    expect(cardText('.teams-clone-card'))
      .toContain('Not carried: paused, and the admin credentials — every clone mints those fresh for itself.');
  });
});

/**
 * WHAT THE DELETE DIALOG PROMISES MUST BE SOMETHING DELETION CAN DO.
 *
 * It must not list "the team's Concierge, and any open terminal session": both halves would be
 * false. There is no team's Concierge: one is chosen for the INSTANCE at Admin -> Concierge, and a team
 * row only RECORDS the choice. And a session is keyed on `ConciergeSessionKey(user)` - the person
 * alone - so no team-scoped operation can address one.
 */
describe('the team delete dialog', () => {
  it('does not promise to remove a Concierge or end a terminal session', async () => {
    await mountView([aTeam('alpha')]);
    await click('Delete this team');

    const text = cardText('.teams-confirm-card');
    expect(text).toContain('Delete Alpha?');
    expect(text).not.toContain('Concierge');
    expect(text).not.toContain('terminal session');
  });

  /**
   * THE POSITIVE HALF. Deleting the bullet by deleting the whole list would satisfy the case above
   * and leave a destructive confirmation that warns about nothing.
   */
  it('still names what deletion really does destroy', async () => {
    await mountView([aTeam('alpha')]);
    await click('Delete this team');

    const text = cardText('.teams-confirm-card');
    expect(text).toContain('every Agent Container on the team, and any running agent');
    expect(text).toContain("its documents, its members' working folders, and its transcripts");
    expect(text).toContain("every account's access to it");
  });

  /** B001F: a team's local repository is not deleted with it, and the dialog says where it goes. */
  it('says the team\'s local repository is kept and can be deleted from Admin → Repositories', async () => {
    await mountView([{ ...aTeam('alpha'), repos: ['https://github.com/o/app.git', 'local:alpha'] } as unknown as Team]);
    await click('Delete this team');

    const kept = document.body.querySelector('[data-local-repos-kept]')?.textContent?.replace(/\s+/g, ' ').trim();
    expect(kept).toBe('Its local repository local:alpha is kept, and can be deleted from Admin → Repositories.');
  });

  /** Deleting a team never removes a plugin; a package's team says where to remove them. */
  it('says a package\'s plugins stay installed and are removed in Admin → Plugins', async () => {
    await mountView([{
      ...aTeam('alpha'),
      solution: { id: 'job-tracker', name: 'Job Tracker', version: '1.0.0', plugins: ['job-board', 'mailer'] },
    } as unknown as Team]);
    await click('Delete this team');

    const kept = document.body.querySelector('[data-solution-plugins-kept]')?.textContent?.replace(/\s+/g, ' ').trim();
    expect(kept).toBe(
      'Job Tracker 1.0.0 installed the plugins job-board, mailer. They stay installed after the team is deleted; '
      + 'remove them in Admin → Plugins.');
  });

  it('names the one plugin a package installed', async () => {
    await mountView([{
      ...aTeam('alpha'),
      solution: { id: 'job-tracker', name: 'Job Tracker', version: '1.0.0', plugins: ['job-board'] },
    } as unknown as Team]);
    await click('Delete this team');

    expect(document.body.querySelector('[data-solution-plugins-kept]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Job Tracker 1.0.0 installed the plugin job-board. It stays installed after the team is deleted; remove it in Admin → Plugins.');
  });

  it('says nothing about plugins for a team made by hand', async () => {
    await mountView([aTeam('alpha')]);
    await click('Delete this team');

    expect(document.body.querySelector('[data-solution-plugins-kept]')).toBeNull();
    expect(cardText('.teams-confirm-card')).not.toContain('Admin → Plugins');
  });

  it('says nothing about a kept repository when the team has no local one', async () => {
    await mountView([{ ...aTeam('alpha'), repos: ['https://github.com/o/app.git'] } as unknown as Team]);
    await click('Delete this team');

    expect(document.body.querySelector('[data-local-repos-kept]')).toBeNull();
    expect(cardText('.teams-confirm-card')).not.toContain('Admin → Repositories');
  });

  it('deletes without typing when the server has nothing to warn about', async () => {
    deleteTeam.mockResolvedValue({ containers: 0, failures: [] });
    await mountView([aTeam('alpha')]);
    await click('Delete this team');
    await click('Delete team');

    expect(deleteTeam).toHaveBeenCalledWith('alpha', undefined);
    expect(board.refreshForTeamDeleted).toHaveBeenCalledWith('alpha');
  });

  it('asks for the server\'s confirmation when deletion would discard commits on no remote', async () => {
    deleteTeam.mockRejectedValueOnce(
      new TeamDeletionConfirmationRequired('refused', 'discard alpha', ['Harness: 2 commits on no remote']),
    );
    await mountView([aTeam('alpha')]);
    await click('Delete this team');
    await click('Delete team');

    const text = cardText('.teams-confirm-card');
    expect(text).toContain('commits that are on no remote');
    expect(text).toContain('Harness: 2 commits on no remote');
    expect(text).toContain('Type discard alpha to confirm losing those commits');
    expect(button('Delete team').disabled).toBe(true);
  });
});

/**
 * "ALSO DELETE ITS LOCAL REPOSITORY", off by default, offered for a `local:<name>` the team
 * uses that no other team does, saying what is lost. One another team uses is named with that team
 * and has no box; a URL never gets one.
 */
describe('the team delete dialog\'s local repository checkbox', () => {
  const localRepo = (teams: string[]) => ({
    name: 'alpha',
    reference: 'local:alpha',
    sizeBytes: 2048,
    defaultBranch: 'main',
    lastCommit: { sha: 'abc123', subject: 'last', committedAt: '2026-09-28T10:00:00+00:00' },
    teams,
    unused: teams.length === 0,
    branches: ['main', 'team/alpha'],
    commitCount: 12,
  });

  const withRepos = (repos: string[]) => ({ ...aTeam('alpha'), repos } as unknown as Team);

  function checkbox(reference: string): HTMLElement | null {
    return document.body.querySelector(`[data-delete-local-repo="${reference}"] .q-checkbox`);
  }

  it('offers an unticked box for a local repository no other team uses, with what it loses', async () => {
    listLocalRepos.mockResolvedValue([localRepo(['alpha'])]);
    await mountView([withRepos(['local:alpha'])]);
    await click('Delete this team');

    const box = checkbox('local:alpha');
    expect(box).not.toBeNull();
    expect(box!.getAttribute('aria-checked')).toBe('false');
    expect(cardText('[data-delete-local-repo="local:alpha"]').trim())
      .toBe('Also delete its local repository local:alpha Loses 12 commits on branches main, team/alpha, last commit 2026-09-28.');
    // Unticked, it is still said to be kept.
    expect(document.body.querySelector('[data-local-repos-kept]')).not.toBeNull();
  });

  it('sends the ticked repository with the delete and no longer says it is kept', async () => {
    listLocalRepos.mockResolvedValue([localRepo(['alpha'])]);
    deleteTeam.mockResolvedValue({ containers: 1, failures: [], localRepositoriesDeleted: ['local:alpha'] });
    await mountView([withRepos(['local:alpha'])]);
    await click('Delete this team');

    checkbox('local:alpha')!.click();
    await flushPromises();
    expect(checkbox('local:alpha')!.getAttribute('aria-checked')).toBe('true');
    expect(document.body.querySelector('[data-local-repos-kept]')).toBeNull();

    await click('Delete team');

    expect(deleteTeam).toHaveBeenCalledWith('alpha', undefined, ['local:alpha']);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({
      type: 'positive',
      message: expect.stringContaining('Deleted: local:alpha.'),
    }));
  });

  it('sends no repository when the box is left unticked', async () => {
    listLocalRepos.mockResolvedValue([localRepo(['alpha'])]);
    deleteTeam.mockResolvedValue({ containers: 1, failures: [], localRepositoriesKept: ['local:alpha'] });
    await mountView([withRepos(['local:alpha'])]);
    await click('Delete this team');
    await click('Delete team');

    expect(deleteTeam).toHaveBeenCalledWith('alpha', undefined);
  });

  it('offers no box for a local repository another team uses, and names that team', async () => {
    listLocalRepos.mockResolvedValue([localRepo(['alpha', 'beta'])]);
    await mountView([withRepos(['local:alpha']), aTeam('beta')]);
    await click('Delete this team');

    expect(checkbox('local:alpha')).toBeNull();
    expect(document.body.querySelector('[data-delete-local-repo]')).toBeNull();
    expect(cardText('[data-local-repo-shared="local:alpha"]').trim()).toBe('local:alpha is kept: Beta uses it.');
    expect(document.body.querySelector('[data-local-repos-kept]')).toBeNull();
  });

  it('never offers a box for a URL repository', async () => {
    listLocalRepos.mockResolvedValue([localRepo([])]);
    await mountView([withRepos(['https://github.com/o/app.git'])]);
    await click('Delete this team');

    expect(document.body.querySelector('[data-delete-local-repo]')).toBeNull();
    expect(document.body.querySelector('.teams-confirm-card .q-checkbox')).toBeNull();
    expect(listLocalRepos).not.toHaveBeenCalled();
  });

  it('names a ticked repository that could not be deleted, and why, once the team is gone', async () => {
    listLocalRepos.mockResolvedValue([localRepo(['alpha'])]);
    deleteTeam.mockResolvedValue({
      containers: 1,
      failures: [],
      localRepositoriesKept: ['local:alpha'],
      localRepositoryFailures: [{ reference: 'local:alpha', reason: "'alpha' could not be deleted: busy. Delete it from Admin → Repositories." }],
    });
    await mountView([withRepos(['local:alpha'])]);
    await click('Delete this team');
    checkbox('local:alpha')!.click();
    await flushPromises();
    await click('Delete team');

    expect(notify).toHaveBeenCalledWith(expect.objectContaining({
      type: 'warning',
      message: expect.stringContaining(
        "local:alpha was not deleted: 'alpha' could not be deleted: busy. Delete it from Admin → Repositories."),
    }));
    expect(board.refreshForTeamDeleted).toHaveBeenCalledWith('alpha');
  });
});

/**
 * A DELETION THAT COULD NOT REMOVE ITS FOLDER WHOLE names every path still on disk where the delete
 * was asked for, and retries it from there.
 */
describe('an unfinished removal', () => {
  const root = '/data/teams/alpha';
  const stuck = `${root}/workspaces/Dev/tmp/a.bin`;

  it('names every remaining path and retries from the dialog until it finishes', async () => {
    deleteTeam.mockResolvedValue({
      containers: 0, failures: [`${root}: removal unfinished`], remaining: [stuck], removalUnfinished: root,
    });
    retryRemoval
      .mockResolvedValueOnce({ retried: [{ path: root, finished: false, remaining: [stuck], note: null }] })
      .mockResolvedValueOnce({ retried: [{ path: root, finished: true, remaining: [], note: null }] });
    await mountView([aTeam('alpha')]);
    await click('Delete this team');
    await click('Delete team');

    expect(cardText('.teams-unfinished-card')).toContain(stuck);

    await click('Retry removal');
    expect(retryRemoval).toHaveBeenCalledWith(root);
    expect(cardText('.teams-unfinished-card')).toContain(stuck);

    await click('Retry removal');
    await flushPromises();
    expect(retryRemoval).toHaveBeenCalledTimes(2);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive' }));
  });
});

/**
 * EVERY UNFINISHED REMOVAL, NOT ONLY THE ONE A DELETION ON THIS PAGE JUST LEFT: a deleted member's
 * workspace and a folder a Reset emptied are recorded and retried exactly as a team root is, and a
 * person who comes back later - or after a restart that could not finish them - finds them here,
 * listed from `GET /api/removals`, each with its remaining paths and its own Retry.
 */
describe('the unfinished removals list', () => {
  const teamRoot = {
    path: '/data/teams/gone', kind: 'team-root', team: 'gone', member: null,
    remaining: ['/data/teams/gone/repos/x/.git/lock'], recordedAt: '2026-09-28T09:00:00Z', attempts: 2,
  };
  const workspace = {
    path: '/data/teams/alpha/workspaces/Dev', kind: 'workspace', team: 'alpha', member: 'Dev',
    remaining: ['/data/teams/alpha/workspaces/Dev/node_modules/.bin/x'], recordedAt: '2026-09-28T09:05:00Z', attempts: 1,
  };
  const emptied = {
    path: '/data/teams/alpha/workspaces/Scout', kind: 'emptied', team: 'alpha', member: null,
    remaining: ['/data/teams/alpha/workspaces/Scout/cache/a.bin', '/data/teams/alpha/workspaces/Scout/cache/b.bin'],
    recordedAt: '2026-09-28T09:10:00Z', attempts: 1,
  };

  const rows = () => [...document.body.querySelectorAll<HTMLElement>('[data-removal]')];
  const rowFor = (path: string) => rows().find((row) => row.dataset.removal === path)!;

  it('lists a team root, a member workspace and a Reset folder, each with every remaining path', async () => {
    listRemovals.mockResolvedValue([teamRoot, workspace, emptied]);
    await mountView([aTeam('alpha')]);

    expect(listRemovals).toHaveBeenCalled();
    expect(rows().map((row) => row.dataset.removal)).toEqual([teamRoot.path, workspace.path, emptied.path]);

    expect(rowFor(teamRoot.path).textContent).toContain('gone');
    expect(rowFor(teamRoot.path).textContent).toContain(teamRoot.remaining[0]);
    expect(rowFor(workspace.path).textContent).toContain('Dev');
    expect(rowFor(workspace.path).textContent).toContain(workspace.remaining[0]);
    for (const path of emptied.remaining) expect(rowFor(emptied.path).textContent).toContain(path);

    // The kind in words, so a person can tell a deleted team's folder from a Reset's leftovers.
    expect(rowFor(teamRoot.path).querySelector('[data-removal-kind]')?.textContent).toMatch(/team/i);
    expect(rowFor(workspace.path).querySelector('[data-removal-kind]')?.textContent).toMatch(/workspace/i);
    expect(rowFor(emptied.path).querySelector('[data-removal-kind]')?.textContent).toMatch(/reset/i);
  });

  it('shows nothing when no removal is unfinished', async () => {
    await mountView([aTeam('alpha')]);

    expect(document.body.querySelector('[data-removals]')).toBeNull();
  });

  it('retries one removal by its path, keeps what still remains, and drops it once it finishes', async () => {
    listRemovals.mockResolvedValue([workspace, emptied]);
    const left = [emptied.remaining[1]!];
    retryRemoval
      .mockResolvedValueOnce({ retried: [{ ...emptied, finished: false, remaining: left, note: 'still held' }] })
      .mockResolvedValueOnce({ retried: [{ ...emptied, finished: true, remaining: [], note: null }] });
    await mountView([aTeam('alpha')]);

    await click(`Retry removal of ${emptied.path}`);
    expect(retryRemoval).toHaveBeenCalledWith(emptied.path);
    expect(rowFor(emptied.path).textContent).not.toContain(emptied.remaining[0]);
    expect(rowFor(emptied.path).textContent).toContain(left[0]);
    expect(rowFor(emptied.path).textContent).toContain('still held');

    await click(`Retry removal of ${emptied.path}`);
    expect(rows().map((row) => row.dataset.removal)).toEqual([workspace.path]);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive' }));
  });

  it('reads the list again after a team deletion leaves its folder behind', async () => {
    const root = '/data/teams/alpha';
    deleteTeam.mockResolvedValue({
      containers: 0, failures: [`${root}: removal unfinished`], remaining: [`${root}/x`], removalUnfinished: root,
    });
    await mountView([aTeam('alpha')]);
    const before = listRemovals.mock.calls.length;

    await click('Delete this team');
    await click('Delete team');

    expect(listRemovals.mock.calls.length).toBeGreaterThan(before);
  });
});

describe('TeamsView pause controls', () => {
  it('pauses a running team from its row, to any signed-in person', async () => {
    await mountView([aTeam('alpha')]);
    await click('Pause');

    expect(pauseTeam).toHaveBeenCalledWith('alpha');
    expect(resumeTeam).not.toHaveBeenCalled();
    expect(board.refresh).toHaveBeenCalled();
  });

  it('resumes a paused team from its row', async () => {
    await mountView([aTeam('alpha', true)]);
    await click('Resume');

    expect(resumeTeam).toHaveBeenCalledWith('alpha');
    expect(pauseTeam).not.toHaveBeenCalled();
  });

  it('does not open the team on the way through', async () => {
    await mountView([aTeam('alpha')]);
    const setActiveTeam = vi.spyOn(board, 'setActiveTeam');
    await click('Pause');

    expect(setActiveTeam).not.toHaveBeenCalled();
    expect(bodyText()).toContain('Alpha');
  });
});

/**
 * The row reads the ledger from the WIP store - `2 running, 1 waiting` - and a team
 * whose only work is a held wake reads "waiting for a slot" naming who is held, never undeclared.
 */
describe('the WIP ledger on a row', () => {
  const since = '2026-09-23T10:00:00Z';

  it('reads the running and waiting counts for that team only', async () => {
    getWip.mockResolvedValue({
      max: 3,
      running: [
        { team: 'alpha', member: 'Dev1', since },
        { team: 'alpha', member: 'Dev2', since },
        { team: 'beta', member: 'Dev1', since },
      ],
      waiting: [{ team: 'alpha', member: 'Manager', since }],
    });

    await mountView([aTeam('alpha'), aTeam('beta')]);

    const rows = [...document.body.querySelectorAll('tr.teams-row')].map((tr) => tr.textContent ?? '');
    expect(rows[0]).toContain('2 running, 1 waiting');
    expect(rows[1]).toContain('1 running, 0 waiting');
  });

  it('says waiting for a slot and names the held member', async () => {
    const held = {
      ...aTeam('alpha'),
      containers: [
        { team: 'alpha', id: 'Manager', name: 'Manager', state: 'Idle', queueDepth: 0, held: true },
        { team: 'alpha', id: 'Dev1', name: 'Dev1', state: 'Idle', queueDepth: 0 },
      ],
    } as unknown as Team;

    await mountView([held]);

    const text = document.body.querySelector('tr.teams-row')?.textContent ?? '';
    expect(text).toContain('waiting for a slot: Manager');
    expect(text).not.toContain('undeclared');
  });
});

describe('the Teams view with no team yet', () => {
  it('says what a team is and names New Team and the Concierge as the next step', async () => {
    const wrapper = await mountView([]);
    const text = wrapper.text().replace(/\s+/g, ' ');

    expect(text).toContain(
      'A team is a Manager and the members it takes on to do your work; to start one, choose New Team on the ribbon above or ask the Concierge.',
    );
    expect(text).not.toContain('door into it');
  });
});
