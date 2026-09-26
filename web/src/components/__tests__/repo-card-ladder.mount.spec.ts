// @vitest-environment happy-dom
//
// THE LADDER IN THE CARD, MOUNTED. What the ladder MEANS lives in `lib/repoLadder.ts` and is tested
// there over the whole state space; this file asserts that the card renders the step `nextStep`
// chose as its one control, that the gate disables it, that the one destructive action asks first,
// and that each action reaches its own client call. It replaces a spec that read `RepoCard.vue` as
// text, and the source pins `console-git-reread.spec.ts` kept on the card's emits.
//
// That the dispatch table covers every `LadderAction` is `vue-tsc`'s to enforce: `ACTIONS` is a
// `Record<LadderAction, ...>`, so a missing entry is a type error, not a test failure.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const client = vi.hoisted(() => ({
  fetchRepoAsync: vi.fn(),
  bringRepoCurrentAsync: vi.fn(),
  rebaseRepoAsync: vi.fn(),
  pushRepoAsync: vi.fn(),
  mergeRepoToMainAsync: vi.fn(),
  deleteRepoRemoteBranchAsync: vi.fn(),
  cleanupRepoWorktreesAsync: vi.fn(),
  getRepoStatus: vi.fn(),
  gateVerdictAsync: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  ...client,
}));

import RepoCard from '../RepoCard.vue';
import { formatMainDelta } from '../../lib/repoStatus';
import type { RepoActionResult, RepoStatus } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

function status(over: Partial<RepoStatus> = {}): RepoStatus {
  return {
    defaultBranch: 'main',
    name: 'Harness',
    clonePath: '/repo',
    mainSha: 'a'.repeat(40),
    mainAhead: 0,
    mainBehind: 0,
    dirty: false,
    headCheckout: 'main',
    teamBranch: 'team/alpha',
    teamSha: 'b'.repeat(40),
    teamPushed: true,
    teamPushedFrom: null,
    teamMergedToMain: true,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: null,
    worktrees: [],
    originCheckedAt: null,
    ...over,
  } as RepoStatus;
}

const answer = (repo: RepoStatus, success = true, message = 'Done.'): RepoActionResult =>
  ({ repo: 'Harness', status: repo, message, success });

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  for (const fn of Object.values(client)) fn.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
  client.gateVerdictAsync.mockResolvedValue({ outcome: 'passed' });
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

/**
 * Mounted over `repo`, with the fetch on open answering `fetched` (the same state by default). The
 * card's parent feeds `updated` back in as the new `repo`; this does the same.
 */
async function mountCard(
  repo: RepoStatus,
  { fetched = answer(repo, true, 'Fetched origin.'), gitResolves = true, anyMemberRunning = false } = {},
) {
  client.fetchRepoAsync.mockResolvedValue(fetched);
  wrapper = mount(RepoCard, {
    attachTo: document.body,
    props: {
      team: 'alpha' as never,
      repo,
      gitResolves,
      anyMemberRunning,
      'onUpdated': (next: RepoStatus) => void wrapper?.setProps({ repo: next }),
    },
  });
  await flushPromises();
  return wrapper;
}

const actions = (card: VueWrapper) => card.findAll('.repo-action');
const action = (card: VueWrapper) => {
  const found = actions(card);
  expect(found, 'the card should offer exactly one action').toHaveLength(1);
  return found[0]!;
};

describe('the repo card climbs the ladder', () => {
  /** Never derived from a stale view of origin: the card reads origin the moment it opens. */
  it('fetches on open', async () => {
    await mountCard(status());

    expect(client.fetchRepoAsync).toHaveBeenCalledWith('alpha', 'Harness');
  });

  /**
   * THE SELF-LOCKING DEFECT. `behind 0` on the local ref disabled the one control that fetched, so a
   * clone whose origin had moved could never be told so. The fetch on open is what measures it, and
   * the step it measures is offered and live.
   */
  it('offers Bring current once the fetch finds the clone behind, however it read before', async () => {
    const card = await mountCard(status({ mainBehind: 0 }), {
      fetched: answer(status({ mainBehind: 3 }), true, 'Fetched origin.'),
    });

    expect(action(card).text()).toContain('Bring current');
    expect(action(card).attributes('disabled')).toBeUndefined();
    expect(card.text()).not.toContain('already level with origin/main');
  });

  /** ONE enabled action, the ladder's, rather than a row of buttons a reader can press out of order. */
  it('renders the one action nextStep chose', async () => {
    const card = await mountCard(status({ mainAhead: 2, mainBehind: 1 }));

    expect(action(card).text()).toContain('Rebase onto origin/main');
  });

  /**
   * A FAILED FETCH DOES NOT EMPTY THE CARD. The fetch route answers an unreachable origin with a 200
   * carrying `success: false`; the figures stay, the reason is said beside them, and Fetch origin
   * stays the step because nothing was read.
   */
  it('keeps the figures and says why when the fetch did not reach origin', async () => {
    const repo = status({ mainAhead: 1, mainBehind: 0 });
    const card = await mountCard(repo, { fetched: answer(repo, false, 'could not reach origin') });

    expect(card.find('.repo-stale').text()).toContain('could not reach origin');
    expect(card.text()).toContain(formatMainDelta(repo));
    expect(action(card).text()).toContain('Fetch origin');
  });
});

describe('the gate', () => {
  it.each([
    [{ anyMemberRunning: true }, 'a member is Running'],
    [{ gitResolves: false }, "git was not found on this machine's PATH"],
  ])('disables the action and says why: %o', async (gate, reason) => {
    const card = await mountCard(status({ mainBehind: 3 }), { ...gate, fetched: answer(status({ mainBehind: 3 })) });

    expect(action(card).attributes('disabled')).toBeDefined();
    expect(card.find('.repo-next-reason').text()).toBe(reason);
  });

  /** The gate must not explain a button that is not there: an actionless rung gives the ladder's reason. */
  it('captions a rung with no action with the ladder reason, not the gate', async () => {
    const card = await mountCard(status({ dirty: true }));
    await card.setProps({ anyMemberRunning: true });

    expect(actions(card)).toHaveLength(0);
    expect(card.find('.repo-next-reason').text()).toContain('uncommitted changes');
    expect(card.text()).not.toContain('a member is Running');
  });
});

describe('taking a step', () => {
  /** A success in its own state and class, never red under the primary control. */
  it('says what went right in the success state, not the error one', async () => {
    const card = await mountCard(status({ mainBehind: 3 }), { fetched: answer(status({ mainBehind: 3 })) });
    client.bringRepoCurrentAsync.mockResolvedValue(answer(status(), true, 'Brought current.'));

    await action(card).trigger('click');
    await flushPromises();

    expect(card.find('.repo-success').text()).toBe('Brought current.');
    expect(card.find('.repo-error').exists()).toBe(false);
  });

  it('reports a failure that did not throw in the error state', async () => {
    const card = await mountCard(status({ mainBehind: 3 }), { fetched: answer(status({ mainBehind: 3 })) });
    client.bringRepoCurrentAsync.mockResolvedValue(answer(status({ mainBehind: 3 }), false, 'not a fast-forward'));

    await action(card).trigger('click');
    await flushPromises();

    expect(card.find('.repo-error').text()).toBe('not a fast-forward');
    expect(card.find('.repo-success').exists()).toBe(false);
  });

  it.each([
    ['bring-current', { mainBehind: 3 }, 'bringRepoCurrentAsync'],
    ['rebase', { mainAhead: 2, mainBehind: 1 }, 'rebaseRepoAsync'],
    ['push', { teamPushed: false, teamMergedToMain: false }, 'pushRepoAsync'],
  ] as const)('runs %s in one click, on its own client call', async (_, over, fn) => {
    const repo = status(over);
    const card = await mountCard(repo, { fetched: answer(repo) });
    client[fn].mockResolvedValue(answer(repo));

    await action(card).trigger('click');
    await flushPromises();

    expect(client[fn]).toHaveBeenCalledWith('alpha', 'Harness');
  });

  it('runs Fetch origin again from the button when the first fetch reached nothing', async () => {
    const repo = status();
    const card = await mountCard(repo, { fetched: answer(repo, false, 'unreachable') });

    await action(card).trigger('click');
    await flushPromises();

    expect(client.fetchRepoAsync).toHaveBeenCalledTimes(2);
  });

  /** The re-read after any action is the dialog's; the card says which way it went. */
  it('emits action-succeeded after a step and action-failed after one that threw', async () => {
    const card = await mountCard(status({ mainBehind: 3 }), { fetched: answer(status({ mainBehind: 3 })) });
    client.bringRepoCurrentAsync.mockResolvedValueOnce(answer(status({ mainBehind: 3 })));
    client.bringRepoCurrentAsync.mockRejectedValueOnce(new Error('network'));

    await action(card).trigger('click');
    await flushPromises();
    await action(card).trigger('click');
    await flushPromises();

    expect(card.emitted('action-succeeded')).toHaveLength(1);
    expect(card.emitted('action-failed')).toHaveLength(1);
    expect(card.find('.repo-error').text()).toBe('network');
  });
});

describe('the one destructive action', () => {
  const confirmCard = () =>
    [...document.body.querySelectorAll('.repo-confirm-card')].find((c) => c.textContent?.includes('origin/team/alpha'));

  it('asks first, naming the ref it deletes and why that is safe', async () => {
    const card = await mountCard(status());
    expect(action(card).text()).toContain('Delete the branch on origin');

    await action(card).trigger('click');
    await flushPromises();

    expect(client.deleteRepoRemoteBranchAsync).not.toHaveBeenCalled();
    expect(confirmCard()?.textContent).toContain('already on');
  });

  it('deletes only once the confirmation is pressed', async () => {
    const card = await mountCard(status());
    client.deleteRepoRemoteBranchAsync.mockResolvedValue(answer(status({ teamPushed: false })));

    await action(card).trigger('click');
    await flushPromises();
    const confirm = [...confirmCard()!.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Delete the branch')) as HTMLButtonElement;
    confirm.click();
    await flushPromises();

    expect(client.deleteRepoRemoteBranchAsync).toHaveBeenCalledWith('alpha', 'Harness');
  });
});
