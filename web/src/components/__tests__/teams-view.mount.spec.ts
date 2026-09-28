// @vitest-environment happy-dom
//
// THE TEAMS TABLE, MOUNTED. Its rules (sort, status, the clone name) are `lib/teamsTable.ts`'s and
// tested there; what is left for this file is what a person reads in the two confirmations and
// what the row buttons actually call.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { cloneTeam, deleteTeam, getWip, pauseTeam, resumeTeam, retryRemoval, notify } = vi.hoisted(() => ({
  cloneTeam: vi.fn(),
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
