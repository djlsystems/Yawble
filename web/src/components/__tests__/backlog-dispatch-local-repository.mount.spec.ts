// @vitest-environment happy-dom
//
// BACKLOG DISPATCH TO A NEW TEAM, B001F: the same "Create a local repository for this team" box as
// New Team (shown only with no repository listed, ticked by default, unticked sends
// `localRepository: false`), and a refused repository check shown with the same component - the
// Host's sentence, ONLY the choices it listed, and each choice resent in `repoChoices` until the
// team is made and the item dispatched.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const { backlogItems, backlogItem, listCatalog, fileSystemRoots, dispatchBacklogItemToNewTeam } = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  dispatchBacklogItemToNewTeam: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getInstanceId: async () => 'instance-a',
  backlogItems,
  backlogItem,
  listCatalog,
  fileSystemRoots,
  dispatchBacklogItemToNewTeam,
}));

import BacklogDialog from '../BacklogDialog.vue';
import { ActionRefused } from '../../api/client';
import type { RepoCheckFailure } from '../../api/types';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { addChips, chipsIn } from '../../test/chipList';


const MISSING = 'https://github.com/acme/job-tracker';
const OTHER = 'https://git.example.com/acme/other.git';

const dispatched = {
  team: 'b0001-first-thing',
  teamName: 'b0001-first-thing',
  correlation: 9,
  dispatch: 1,
  localRepository: null,
  createdOnGitHub: null,
};

beforeEach(() => {
  localStorage.clear();
  backlogItems.mockReset();
  backlogItems.mockResolvedValue([{
    id: 1, team: null, teamName: null, teamGone: false, title: 'first thing', body: '', state: 'ready',
    archivedAt: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com', inFlight: null,
  }]);
  backlogItem.mockReset();
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  fileSystemRoots.mockReset();
  fileSystemRoots.mockResolvedValue({ roots: [] });
  dispatchBacklogItemToNewTeam.mockReset();
  dispatchBacklogItemToNewTeam.mockResolvedValue(dispatched);
});

afterEach(resetBody);

/** Opens the backlog with this browser remembering `repos`, opens Dispatch on the item, picks "new team". */
async function openDispatchToNew(repos: string[]) {
  remember({ managerAgent: 'claude-headless', memberAgents: ['claude-headless'], root: null, repos }, 'instance-a');
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;
  vi.spyOn(board, 'refreshForTeamCreated').mockResolvedValue(undefined as never);
  vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});
  useSessionStore().user = { email: 'admin@example.com' } as never;

  await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  ([...document.querySelectorAll('button')].find((b) => b.innerHTML.includes('send')) as HTMLElement).click();
  await flushPromises();

  [...document.querySelectorAll('.q-radio')]
    .find((r) => r.textContent?.includes('new team'))!
    .dispatchEvent(new Event('click', { bubbles: true }));
  await flushPromises();
}

function dispatchButton(): HTMLElement {
  return [...document.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Dispatch') as HTMLElement;
}

function checkbox(): HTMLElement | null {
  return document.body.querySelector<HTMLElement>('[data-local-repository-checkbox]');
}

/** The settings (third argument) of dispatch call `index`. */
function sent(index: number): Record<string, unknown> {
  return dispatchBacklogItemToNewTeam.mock.calls[index]![2] as Record<string, unknown>;
}

function offered(url?: string): string[] {
  const scope = url ? `[data-repo-check-url="${url}"] ` : '';
  return [...document.body.querySelectorAll<HTMLElement>(`${scope}[data-repo-choice]`)].map((b) => b.dataset.repoChoice!);
}

function choice(code: string, url?: string): HTMLElement {
  const scope = url ? `[data-repo-check-url="${url}"] ` : '';
  const found = document.body.querySelector<HTMLElement>(`${scope}[data-repo-choice="${code}"]`);
  if (!found) throw new Error(`no ${code} choice in the rendered refusal`);
  return found;
}

/** The 422 exactly as `send` throws it for the contract's refusal body. */
function refused(error: string, repos: RepoCheckFailure[]) {
  return Object.assign(new ActionRefused(error, { error, code: 'repo-check-failed', repos }), { status: 422 });
}

const notFoundSentence = `${MISSING} could not be read: remote: Repository not found. Nothing was created. `
  + 'Choose: create it on GitHub (private), or use a local repository instead.';

describe('Backlog dispatch to a new team: Create a local repository for this team', () => {
  it('is shown ticked with no repository listed, and Dispatch sends localRepository: true', async () => {
    await openDispatchToNew([]);

    expect(checkbox()).not.toBeNull();
    expect(bodyText()).toContain('Create a local repository for this team');
    expect(checkbox()!.getAttribute('aria-checked')).toBe('true');

    dispatchButton().click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledTimes(1);
    expect(sent(0).repos).toEqual([]);
    expect(sent(0).localRepository).toBe(true);
  });

  it('unticked, sends localRepository: false', async () => {
    await openDispatchToNew([]);

    checkbox()!.click();
    await flushPromises();
    expect(checkbox()!.getAttribute('aria-checked')).toBe('false');

    dispatchButton().click();
    await flushPromises();

    expect(sent(0).localRepository).toBe(false);
  });

  it('is hidden with a repository listed, which sends no localRepository', async () => {
    await openDispatchToNew([MISSING]);

    expect(checkbox()).toBeNull();

    dispatchButton().click();
    await flushPromises();

    expect(sent(0).repos).toEqual([MISSING]);
    expect(sent(0).localRepository).toBeUndefined();
  });
});

describe('Backlog dispatch to a new team: a URL added in Team settings', () => {
  const TYPED = 'https://github.com/owner/typed.git';

  function button(label: string): HTMLElement {
    const found = [...document.querySelectorAll('button')].find((b) => b.textContent?.trim().endsWith(label));
    if (!found) throw new Error(`no ${label} button in the rendered dialog`);
    return found as HTMLElement;
  }

  const reposList = () => document.body.querySelector('[data-settings-repos]')!;

  /** Opens Team settings and adds `url` to the repositories through the list's Add dialog. */
  async function addRepository(url: string) {
    button('Team settings').click();
    await flushPromises();
    await addChips(reposList(), url);
  }

  it('Use these keeps it: the box hides, and Dispatch sends it in repos with no local repository requested', async () => {
    await openDispatchToNew([]);
    expect(checkbox()).not.toBeNull();

    await addRepository(`  ${TYPED}  `);
    button('Use these').click();
    await flushPromises();

    expect(checkbox()).toBeNull();

    dispatchButton().click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledTimes(1);
    expect(sent(0).repos).toEqual([TYPED]);
    expect(sent(0).localRepository).toBeUndefined();
  });

  it('adds it after the ones already there', async () => {
    await openDispatchToNew([OTHER]);

    await addRepository(TYPED);
    expect(chipsIn(reposList())).toEqual([OTHER, TYPED]);
    button('Use these').click();
    await flushPromises();

    dispatchButton().click();
    await flushPromises();

    expect(sent(0).repos).toEqual([OTHER, TYPED]);
  });
});

describe('Backlog dispatch to a new team: a refused repository check', () => {
  it('shows the sentence and only the offered choices; Create it on GitHub resends and dispatches', async () => {
    dispatchBacklogItemToNewTeam.mockRejectedValueOnce(refused(notFoundSentence, [
      { url: MISSING, failure: 'not-found', reason: 'remote: Repository not found.', choices: ['create-on-github', 'use-local'] },
    ]));

    await openDispatchToNew([MISSING]);
    dispatchButton().click();
    await flushPromises();

    expect(bodyText()).toContain(notFoundSentence);
    expect(offered()).toEqual(['create-on-github', 'use-local']);
    expect(bodyText()).toContain('Create it on GitHub (private)');
    expect(bodyText()).toContain('Use a local repository instead');
    expect(bodyText()).not.toContain('Attach anyway');
    expect(useConsoleStore().setActiveTeam).not.toHaveBeenCalled();

    choice('create-on-github').click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledTimes(2);
    expect(dispatchBacklogItemToNewTeam.mock.calls[1]![0]).toBe(1);
    expect(sent(1).repos).toEqual([MISSING]);
    expect(sent(1).repoChoices).toEqual({ [MISSING]: 'create-on-github' });
    expect(useConsoleStore().setActiveTeam).toHaveBeenCalledWith('b0001-first-thing');
    expect(offered()).toEqual([]);
  });

  it('Use a local repository instead resends use-local and dispatches', async () => {
    dispatchBacklogItemToNewTeam.mockRejectedValueOnce(refused(notFoundSentence, [
      { url: MISSING, failure: 'not-found', reason: 'remote: Repository not found.', choices: ['create-on-github', 'use-local'] },
    ]));

    await openDispatchToNew([MISSING]);
    dispatchButton().click();
    await flushPromises();

    choice('use-local').click();
    await flushPromises();

    expect(sent(1).repoChoices).toEqual({ [MISSING]: 'use-local' });
    expect(useConsoleStore().setActiveTeam).toHaveBeenCalledWith('b0001-first-thing');
  });

  it('a network failure offers only use-local and Attach anyway; Attach anyway resends attach-anyway', async () => {
    const sentence = `${OTHER} could not be read: Could not resolve host: git.example.com. Nothing was created.`;
    dispatchBacklogItemToNewTeam.mockRejectedValueOnce(refused(sentence, [
      { url: OTHER, failure: 'unreachable', reason: 'Could not resolve host: git.example.com', choices: ['use-local', 'attach-anyway'] },
    ]));

    await openDispatchToNew([OTHER]);
    dispatchButton().click();
    await flushPromises();

    expect(bodyText()).toContain(sentence);
    expect(offered()).toEqual(['use-local', 'attach-anyway']);
    expect(bodyText()).not.toContain('Create it on GitHub (private)');

    choice('attach-anyway').click();
    await flushPromises();

    expect(sent(1).repoChoices).toEqual({ [OTHER]: 'attach-anyway' });
    expect(useConsoleStore().setActiveTeam).toHaveBeenCalledWith('b0001-first-thing');
  });

  it('offers only a local repository when that is all the Host lists (the Concierge rule)', async () => {
    dispatchBacklogItemToNewTeam.mockRejectedValueOnce(refused(notFoundSentence, [
      { url: MISSING, failure: 'not-found', reason: 'not found', choices: ['use-local'] },
    ]));

    await openDispatchToNew([MISSING]);
    dispatchButton().click();
    await flushPromises();

    expect(offered()).toEqual(['use-local']);
  });

  it('with two refused URLs, resends once both have a choice, carrying both', async () => {
    dispatchBacklogItemToNewTeam.mockRejectedValueOnce(refused('Two could not be read.', [
      { url: MISSING, failure: 'not-found', reason: 'not found', choices: ['create-on-github', 'use-local'] },
      { url: OTHER, failure: 'unreachable', reason: 'timed out', choices: ['use-local', 'attach-anyway'] },
    ]));

    await openDispatchToNew([MISSING, OTHER]);
    dispatchButton().click();
    await flushPromises();

    choice('use-local', MISSING).click();
    await flushPromises();
    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledTimes(1);

    choice('attach-anyway', OTHER).click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledTimes(2);
    expect(sent(1).repoChoices).toEqual({ [MISSING]: 'use-local', [OTHER]: 'attach-anyway' });
    expect(useConsoleStore().setActiveTeam).toHaveBeenCalledWith('b0001-first-thing');
  });

  it('a refusal that is not a repository check stays the plain sentence, with no choices', async () => {
    dispatchBacklogItemToNewTeam.mockRejectedValueOnce(
      Object.assign(new ActionRefused('Something else.', { error: 'Something else.' }), { status: 422 }));

    await openDispatchToNew([MISSING]);
    dispatchButton().click();
    await flushPromises();

    expect(bodyText()).toContain('Something else.');
    expect(offered()).toEqual([]);
  });
});
