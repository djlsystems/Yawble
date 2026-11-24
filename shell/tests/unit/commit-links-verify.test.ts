// @vitest-environment happy-dom
// Commit links fed the REAL core's git.commitsResolve answers.
// tests/unit/fixtures/commit-links/*.json were written by the core's commitsResolve verify test with COMMIT_LINKS_FIXTURE_OUT set
// (two teams "web" and "cloud" on real repositories; a 7-digit prefix shared by two commits of one repository; the same
// commit in a team's main repository and its writable extra). The fake API answers each asked ID with the core's own
// result row for it (an ID the core was not asked is answered as no commit, as the core does for an unknown hex word).
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import { configureCommitResolve, commitChoice, closeChoice, installCommitResolve, resetCommitResolve } from '../../src/renderer/src/commitResolve';
import { closeCommit, commitTarget, commitTeams, installCommitLinks, teamsFor } from '../../src/renderer/src/commitView';
import { renderMarkdown } from '../../src/renderer/src/markdown';
import { LOCAL_MARK } from '../../src/renderer/src/localMark';
import CommitChooser from '../../src/renderer/src/components/CommitChooser.vue';
import CommitText from '../../src/renderer/src/components/CommitText.vue';
import CommitDialog from '../../src/renderer/src/components/CommitDialog.vue';
import ChatMessage from '../../src/renderer/src/components/ChatMessage.vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import TranscriptView from '../../src/renderer/src/components/TranscriptView.vue';
import CardPanel from '../../src/renderer/src/board/CardPanel.vue';
import WorkflowsDialog from '../../src/renderer/src/components/WorkflowsDialog.vue';
import MemberActivity from '../../src/renderer/src/components/MemberActivity.vue';
import { GIT_COMMITS_RESOLVE, type AgentCard, type BoardCard, type TeamSummary, type YawbleApi } from '../../src/shared/api';

const real = <T = any>(name: string): T => JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'commit-links', `${name}.json`), 'utf8'));
const TWO = real('resolve-two-teams');
const COLL = real('resolve-one-repo-collision');
const EXTRA = real('resolve-main-and-extra');
const S = TWO.shas as Record<string, string>;
const rows = [...TWO.answer.results, ...COLL.answer.results, ...EXTRA.answer.results] as { id: string; valid: boolean; matches: any[] }[];

/** The real core's row for each asked ID (one fixture per answer); unknown: no commit. */
function answer(ids: string[]) {
  return { max: 200, results: ids.map((id) => rows.find((r) => r.id === id.toLowerCase()) ?? { id: id.toLowerCase(), valid: true, matches: [] }) };
}

function fakeCore(extra: Record<string, (p: any) => unknown> = {}) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === GIT_COMMITS_RESOLVE) return answer(params.ids);
      if (extra[method]) return extra[method](params);
      return null;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, calls, resolves: () => calls.filter((c) => c.method === GIT_COMMITS_RESOLVE) };
}

async function settle() { for (let i = 0; i < 4; i++) { await new Promise((r) => setTimeout(r, 5)); await flushPromises(); } }

const cleanup: (() => void)[] = [];
let core: ReturnType<typeof fakeCore>;
function use(c: ReturnType<typeof fakeCore>) { core = c; configureCommitResolve({ api: c.api }); }
beforeEach(() => {
  resetCommitResolve();
  use(fakeCore());
  configureCommitResolve({ delayMs: 1 });
  commitTeams.value = { current: 'web', all: ['web', 'cloud'] };
  cleanup.push(installCommitResolve(teamsFor), installCommitLinks(document));
});
afterEach(() => {
  while (cleanup.length) cleanup.pop()!();
  closeCommit(); closeChoice();
  configureCommitResolve({ api: null, delayMs: 25 });
  commitTeams.value = { current: null, all: [] };
  document.body.innerHTML = '';
});
function keep<T extends { unmount(): void }>(w: T): T { cleanup.push(() => w.unmount()); return w; }
function place(markdown: string, team: string | null = 'web'): HTMLElement {
  const box = document.createElement('div');
  if (team) box.dataset.commitTeam = team;
  box.innerHTML = renderMarkdown(markdown);
  document.body.appendChild(box);
  return box;
}
const links = (root: ParentNode) => [...root.querySelectorAll<HTMLElement>('[data-test=commit-link]')];
const confirmed = (root: ParentNode) => links(root).map((a) => a.dataset.commit);

describe('commit links from the real core\'s answers', () => {
  it('1. short and full IDs: plain text before the answer, links to the full commit after it', async () => {
    const box = place(`Merged ${S.pushed.slice(0, 7)}, full ${S.pushed}, local ${S.local.slice(0, 10)}.`);
    expect(links(box)).toHaveLength(0);
    expect(box.querySelector('[data-commit]')).toBeNull();
    expect(box.querySelector('a')).toBeNull();
    expect(getComputedStyle(box.querySelector('.commit-id')!).cursor).not.toBe('pointer');
    await settle();
    expect(confirmed(box)).toEqual([S.pushed, S.pushed, S.local]);
    expect(links(box).every((a) => a.dataset.commitIn === 'web' && a.dataset.commitRepo === undefined)).toBe(true);
    links(box)[0].click();
    expect(commitTarget.value).toEqual({ teams: ['web', 'cloud'], sha: S.pushed });
  });

  it('2. hex words that are not commits stay text: hex-named branch/tag, blob, tree, annotated tag object, a made-up word', async () => {
    const words = ['cafe1234', 'deadbee1', 'beefcafe', S.blob.slice(0, 9), S.tree.slice(0, 9), S.tagObject.slice(0, 9), 'abc1234'];
    const box = place(`Not commits: ${words.join(', ')}. A branch called cafe stays a word.`);
    await settle();
    expect(links(box)).toHaveLength(0);
    expect(box.querySelector('[data-commit]')).toBeNull();
    // beefcafe has no digit and "cafe" is 4 letters: the shell never asks; the rest are asked and answered "none".
    const asked = core.resolves().flatMap((c) => c.params.ids);
    expect(asked).not.toContain('beefcafe');
    expect(asked).not.toContain('cafe');
    expect([...box.querySelectorAll('[data-commit-state=text]')].map((e) => e.textContent)).toEqual(words.filter((w) => w !== 'beefcafe'));
    expect(box.textContent).toBe(`Not commits: ${words.join(', ')}. A branch called cafe stays a word.\n`);
  });

  // An ID in two teams opens the chooser, not a link to the first team.
  const choiceRepos = () => [...document.querySelectorAll('[data-test=commit-choice-repo]')].map((e) => e.textContent);
  for (const all of [['web', 'cloud'], ['cloud', 'web']]) {
    it(`3a / F1 fixed: the same short ID in two teams, no team on the place and none on screen (the Concierge), all ${all.join(',')}: the chooser, both teams in that order`, async () => {
      const id = S.shared.slice(0, 8);
      commitTeams.value = { current: null, all };
      const box = place(`Both teams have ${id}.`, null);
      keep(mount(CommitChooser, { attachTo: document.body }));
      await settle();
      const [a] = links(box);
      expect(a.dataset.commitState).toBe('choose');
      expect(a.dataset.commitIn).toBeUndefined();
      expect(a.title).toBe(`2 commits start with ${id}: click to choose one`);
      a.click();
      await flushPromises();
      expect(commitTarget.value).toBeNull();
      expect(choiceRepos()).toEqual(all.map((t) => `${t} · main`));
    });
  }

  it('3a / observation 4: the screen\'s team (commitTeams.current) only orders the chooser; it does not pick the team', async () => {
    const id = S.shared.slice(0, 8);
    commitTeams.value = { current: 'cloud', all: ['web', 'cloud'] };
    const box = place(`Both teams have ${id}.`, null);
    keep(mount(CommitChooser, { attachTo: document.body }));
    await settle();
    const [a] = links(box);
    expect(a.dataset.commitState).toBe('choose');
    a.click();
    await flushPromises();
    expect(choiceRepos()).toEqual(['cloud · main', 'web · main']);
    // The same, plain text with no team (CommitText without team prop): the chooser too.
    const t = keep(mount(CommitText, { props: { text: `Trail ${id}` }, attachTo: document.body }));
    await settle();
    expect(t.find('[data-test=commit-link]').attributes('data-commit-state')).toBe('choose');
  });

  it('3b / own team in plain text (CommitText team prop): a link straight to that team\'s commit, no chooser', async () => {
    const id = S.shared.slice(0, 8);
    commitTeams.value = { current: 'web', all: ['web', 'cloud'] };
    const t = keep(mount(CommitText, { props: { text: `Trail ${id}`, team: 'cloud' }, attachTo: document.body }));
    keep(mount(CommitChooser, { attachTo: document.body }));
    await settle();
    const a = t.find('[data-test=commit-link]');
    expect(a.attributes('data-commit-state')).toBe('link');
    expect(a.attributes('data-commit-in')).toBe('cloud');
    (a.element as HTMLElement).click();
    await flushPromises();
    expect(document.querySelectorAll('[data-test=commit-choice]')).toHaveLength(0);
    expect(commitTarget.value).toMatchObject({ sha: S.shared });
    expect(commitTarget.value!.teams[0]).toBe('cloud');
  });

  it('3a. the chooser for an ID in two teams (as when F1 is fixed): team, repository and title; picking one opens it', async () => {
    const id = S.shared.slice(0, 8);
    // A place whose own team has neither copy (team "ops"): pickMatches then keeps both teams' matches.
    commitTeams.value = { current: null, all: ['web', 'cloud'] };
    const box = place(`Both teams have ${id}.`, 'ops');
    const chooser = keep(mount(CommitChooser, { attachTo: document.body }));
    await settle();
    const [a] = links(box);
    expect(a.dataset.commitState).toBe('choose');
    expect(a.title).toBe(`2 commits start with ${id}: click to choose one`);
    a.click();
    await flushPromises();
    const rowsShown = [...document.querySelectorAll('[data-test=commit-choice]')];
    expect(rowsShown.map((r) => r.querySelector('[data-test=commit-choice-repo]')!.textContent)).toEqual(['web · main', 'cloud · main']);
    expect(rowsShown.map((r) => r.querySelector('[data-test=commit-choice-title]')!.textContent)).toEqual(['Shared: the common README', 'Shared: the common README']);
    (rowsShown[1] as HTMLElement).click();
    await flushPromises();
    expect(commitTarget.value).toMatchObject({ sha: S.shared });
    expect(commitTarget.value!.teams[0]).toBe('cloud');
    expect(commitTarget.value!.repo).toBeUndefined();
    chooser.unmount();
  });

  it('3b. in a team\'s own place, that team\'s commit is a link (not the chooser)', async () => {
    const box = place(`Shared ${S.shared.slice(0, 8)}.`, 'cloud');
    await settle();
    const [a] = links(box);
    expect(a.dataset.commitState).toBe('link');
    expect(a.dataset.commitIn).toBe('cloud');
  });

  it('3c. two commits of one repository sharing 7 digits: the chooser lists both titles; 8 digits are links', async () => {
    const box = place(`Ambiguous ${COLL.a.slice(0, 7)}; exact ${COLL.a.slice(0, 8)} and ${COLL.b.slice(0, 8)}.`);
    keep(mount(CommitChooser, { attachTo: document.body }));
    await settle();
    const [amb, a, b] = links(box);
    expect(amb.dataset.commitState).toBe('choose');
    expect([a.dataset.commit, b.dataset.commit]).toEqual([COLL.a, COLL.b]);
    amb.click();
    await flushPromises();
    const shown = [...document.querySelectorAll('[data-test=commit-choice]')].map((r) => (r as HTMLElement).dataset.commitFull);
    expect(shown.sort()).toEqual([COLL.a, COLL.b].sort());
    expect([...document.querySelectorAll('[data-test=commit-choice-repo]')].map((e) => e.textContent)).toEqual(['main', 'main']);
  });

  it('3d. main and a writable extra repository: chooser by repository; picking the extra opens the Commit view on it', async () => {
    const id = EXTRA.sha.slice(0, 9);
    const box = place(`In both: ${id}.`);
    keep(mount(CommitChooser, { attachTo: document.body }));
    const shows: any[] = [];
    use(fakeCore({ 'git.commitShow': (p) => { shows.push(p); return { found: false, sha: p.sha, goneReason: 'x' }; } }));
    keep(mount(CommitDialog, { props: { api: core.api }, attachTo: document.body }));
    await settle();
    links(box)[0].click();
    await flushPromises();
    expect([...document.querySelectorAll('[data-test=commit-choice-repo]')].map((e) => e.textContent)).toEqual(['main', 'web-cloud']);
    (document.querySelectorAll('[data-test=commit-choice]')[1] as HTMLElement).click();
    await settle();
    expect(commitTarget.value).toEqual({ teams: ['web', 'cloud'], sha: EXTRA.sha, repo: 'web-cloud' });
    expect(shows[0]).toEqual({ team: 'web', sha: EXTRA.sha, repo: 'web-cloud' });
  });

  it('4. the tooltip: the title; plus the Commit view\'s "not pushed" words for an unpushed commit', async () => {
    const box = place(`${S.pushed.slice(0, 7)} and ${S.local.slice(0, 10)}`);
    await settle();
    const [p, l] = links(box);
    expect(p.title).toBe('Partial refunds: refund part of a payment');
    expect(l.title).toBe(`Receipts: a new layout\n${LOCAL_MARK.long}`);
    // The Commit view shows LOCAL_MARK.long for where.pushed=false (commitView.ts whereLines): the same words.
  });

  it('5. one batched call for a whole screen (markdown + plain text places); the second render uses the cache', async () => {
    place(`Hand-back ${S.pushed.slice(0, 7)} and abc1234`);
    place(`Report ${S.local.slice(0, 10)}`);
    keep(mount(CommitText, { props: { text: `Trail ${S.cloudOnly.slice(0, 7)}`, team: 'cloud' }, attachTo: document.body }));
    await settle();
    expect(core.resolves()).toHaveLength(1);
    expect(core.resolves()[0].params.ids).toHaveLength(4);
    document.body.innerHTML = '';
    const again = place(`Again ${S.pushed.slice(0, 7)} and abc1234 and ${S.local.slice(0, 10)}`);
    await settle();
    expect(core.resolves()).toHaveLength(1);
    expect(confirmed(again)).toEqual([S.pushed, S.local]);
  });
});

describe('a confirmed commit link renders in each place', () => {
  const said = (team = 'web') => `Done: ${team === 'cloud' ? S.cloudOnly.slice(0, 7) : S.pushed.slice(0, 7)}; not a commit: abc1234.`;

  it('hand-backs and Manager reports (MemberPane\'s Report)', async () => {
    const member = { id: 7, teamId: 1, team: 'web', name: 'Manager', role: 'manager', cli: 'claude', instructions: '', model: '', credential: 'default' };
    const card = { member, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: `handed back: ${said()}`, runId: null,
      runStartedAt: null, lastRunState: 'completed', lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [] } as unknown as AgentCard;
    const Screen = defineComponent({ setup: (_, { expose }) => { expose({ size: () => ({ cols: 80, rows: 24 }) }); return () => h('div'); } });
    const w = keep(mount(MemberPane, { props: { card, api: core.api } as any, attachTo: document.body, global: { stubs: { TerminalScreen: Screen } } }));
    await settle();
    expect(confirmed(w.find('[data-test=agent-output]').element)).toEqual([S.pushed]);
  });

  it('card body, comments and trail (CardPanel)', async () => {
    const card = { id: 5, team: 'cloud', teamId: 2, member: 'Ada', item: 'B074', title: 'Links', body: `Body ${S.cloudOnly.slice(0, 7)}.`, status: 'done', lane: 'done',
      placed: true, awaitingManager: false, workflow: 3, createdAt: '2026-11-18T19:00:00Z', updatedAt: '2026-11-18T19:00:00Z', trailCount: 2 } as unknown as BoardCard;
    const detail = { ...card, trail: [
      { id: 1, at: '2026-11-18T19:00:00Z', source: 'Ada', kind: 'handback', text: `Handed back: ${said('cloud')}` },
      { id: 2, at: '2026-11-18T19:01:00Z', source: 'Manager', kind: 'comment', text: `Merged ${S.shared.slice(0, 8)}.` },
    ] };
    use(fakeCore({ 'board.card': () => detail }));
    const w = keep(mount(CardPanel, { props: { id: 5, lanes: [{ id: 'done', label: 'Done' }], api: core.api }, attachTo: document.body }));
    await settle();
    const lines = [...w.element.querySelectorAll('[data-test=card-trail-line]')];
    expect(confirmed(lines[0])).toEqual([S.cloudOnly]);
    // The shared commit in the card's own team: a link to cloud's, no chooser.
    expect(links(lines[1]).map((a) => [a.dataset.commitState, a.dataset.commitIn])).toEqual([['link', 'cloud']]);
    expect(confirmed(w.element.querySelector('.body')!)).toEqual([S.cloudOnly]);
    expect(core.resolves()).toHaveLength(1);
  });

  it('workflow threads: the ending (WorkflowsDialog) and a thread\'s messages (TranscriptView)', async () => {
    const wf = { id: 10, title: 'Card 5', state: 'delivered', owner: null, messages: 4, startedAt: '2026-11-18T18:00:00Z', lastAt: '2026-11-18T19:00:00Z',
      endedAt: '2026-11-18T19:00:00Z', ending: said(), busy: [], time: null, expiresAt: null, expiredAt: null };
    use(fakeCore({ 'bus.workflows': () => [wf] }));
    keep(mount(WorkflowsDialog, { props: { team: { id: 1, name: 'web' } as TeamSummary, api: core.api, pollMs: 0 }, attachTo: document.body }));
    const line = JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: said() }] } });
    const t = keep(mount(TranscriptView, { props: { text: line }, attachTo: document.body }));
    await settle();
    expect(confirmed(document.querySelector('.ending')!)).toEqual([S.pushed]);
    expect(confirmed(t.element)).toEqual([S.pushed]);
  });

  it('Concierge answers (ChatMessage), no team on the place', async () => {
    commitTeams.value = { current: null, all: ['web', 'cloud'] };
    const w = keep(mount(ChatMessage, { props: { role: 'assistant', text: `${said()} Cloud: ${S.cloudOnly.slice(0, 7)}.` }, attachTo: document.body }));
    await settle();
    expect(confirmed(w.element)).toEqual([S.pushed, S.cloudOnly]);
    links(w.element)[1].click();
    expect(commitTarget.value).toEqual({ teams: ['cloud', 'web'], sha: S.cloudOnly });
  });

  it('notifications: Needs you questions (markdown, as TeamView renders them) and activity lines (MemberActivity)', async () => {
    const q = place(`Merge ${S.pushed.slice(0, 7)} to main?`);
    const activity = [{ key: 'a', seq: 1, tone: 'ok', team: 'web', member: 'Ada', verb: 'handed back', who: 'Ada', text: said(), at: '2026-11-18T19:00:00Z' }];
    const w = keep(mount(MemberActivity, { props: { rows: activity as any, loaded: true, now: Date.parse('2026-11-18T19:05:00Z') }, attachTo: document.body }));
    await settle();
    expect(confirmed(q)).toEqual([S.pushed]);
    expect(confirmed(w.element)).toEqual([S.pushed]);
  });
});
