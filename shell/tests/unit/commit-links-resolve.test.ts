// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import {
  answerMatches, cachedMatches, commitChoice, closeChoice, configureCommitResolve, installCommitResolve, pickMatches, resetCommitResolve,
} from '../../src/renderer/src/commitResolve';
import { closeCommit, commitTarget, commitTeams, installCommitLinks, teamsFor } from '../../src/renderer/src/commitView';
import { renderMarkdown } from '../../src/renderer/src/markdown';
import { LOCAL_MARK } from '../../src/renderer/src/localMark';
import CommitChooser from '../../src/renderer/src/components/CommitChooser.vue';
import CommitText from '../../src/renderer/src/components/CommitText.vue';
import ChatMessage from '../../src/renderer/src/components/ChatMessage.vue';
import MemberActivity from '../../src/renderer/src/components/MemberActivity.vue';
import CardPanel from '../../src/renderer/src/board/CardPanel.vue';
import { mountPane, paneRow } from './deliverable-pane-fake';
import { GIT_COMMITS_RESOLVE, isUiMethod, type BoardCard, type CommitMatch, type TeamSummary, type YawbleApi } from '../../src/shared/api';

// A commit-shaped ID is a link only once git.commitsResolve confirms it, against a fake core with the
// core's contract ({team? | teamId?, ids} -> {max, results: [{id, valid,
// matches: [{team, teamId, repo (null: main), fullId, shortId, title, pushed}]}]}, at most 200 IDs a call).

const ADA = '3f9c2a1b7d4e5f60718293a4b5c6d7e8f9012345';
const LOCAL = 'c62216a90b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e';
const TWIN_WEB = '7c63337aaaa1111bbbb2222cccc3333dddd4444e';
const TWIN_CLOUD = '7c63337ffff9999eeee8888dddd7777cccc6666b';
const SHARED = 'e5d4c3b2a1f0e9d8c7b6a5f4e3d2c1b0a9f8e7d6';

/** The fake core's repositories: what each prefix names. */
const COMMITS: CommitMatch[] = [
  { team: 'payments', repo: null, fullId: ADA, title: 'Partial refunds: refund part of a payment', pushed: true },
  { team: 'payments', repo: null, fullId: LOCAL, title: 'Receipts: a new layout', pushed: false },
  { team: 'payments', repo: null, fullId: TWIN_WEB, title: 'Web: fix the refund button', pushed: true },
  { team: 'payments', repo: 'payments-cloud', fullId: TWIN_CLOUD, title: 'Cloud: retry the webhook', pushed: true },
  // The same commit in two teams (a merged base both teams carry).
  { team: 'web', repo: null, fullId: SHARED, title: 'Shared: the base both teams have', pushed: true },
  { team: 'cloud', repo: null, fullId: SHARED, title: 'Shared: the base both teams have', pushed: true },
];
const resolve = (ids: string[]) => ({ max: 200, results: ids.map((id) => ({ id: id.toLowerCase(), valid: true,
  matches: COMMITS.filter((c) => c.fullId.startsWith(id.toLowerCase())).map((c) => ({ ...c, teamId: 1, shortId: c.fullId.slice(0, 12) })) })) });

function fakeCore(extra: Record<string, (p: any) => unknown> = {}) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === GIT_COMMITS_RESOLVE) return resolve(params.ids);
      if (extra[method]) return extra[method](params);
      return null;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, calls, resolves: () => calls.filter((c) => c.method === GIT_COMMITS_RESOLVE) };
}

/** Lets the observer see the render, the batch timer fire and the answer land. */
async function settle() {
  for (let i = 0; i < 3; i++) { await new Promise((r) => setTimeout(r, 5)); await flushPromises(); }
}

const cleanup: (() => void)[] = [];
let core: ReturnType<typeof fakeCore>;
beforeEach(() => {
  resetCommitResolve();
  core = fakeCore();
  configureCommitResolve({ api: core.api, delayMs: 1 });
  commitTeams.value = { current: 'payments', all: ['payments', 'web'] };
  cleanup.push(installCommitResolve(teamsFor), installCommitLinks(document));
});
afterEach(() => {
  while (cleanup.length) cleanup.pop()!();
  closeCommit(); closeChoice();
  configureCommitResolve({ api: null, delayMs: 25 });
  commitTeams.value = { current: null, all: [] };
  document.body.innerHTML = '';
});

function place(markdown: string, team = 'payments'): HTMLElement {
  const box = document.createElement('div');
  box.dataset.commitTeam = team;
  box.innerHTML = renderMarkdown(markdown);
  document.body.appendChild(box);
  return box;
}
const links = (root: ParentNode) => [...root.querySelectorAll<HTMLElement>('[data-test=commit-link]')];

describe('commit links only for confirmed commits', () => {
  it('the new core method is one the window may call (git.*)', () => {
    expect(isUiMethod(GIT_COMMITS_RESOLVE)).toBe(true);
  });

  it('short and full IDs are text before the answer and links after it, opening the full commit', async () => {
    const box = place(`Committed ${ADA.slice(0, 7)} and ${ADA}.`);
    // Before the answer: candidates only, nothing clickable, the text reads as written.
    expect(links(box)).toHaveLength(0);
    expect(box.querySelector('[data-commit]')).toBeNull();
    expect(box.textContent).toBe(`Committed ${ADA.slice(0, 7)} and ${ADA}.\n`);
    await settle();
    const got = links(box);
    expect(got.map((a) => a.dataset.commit)).toEqual([ADA, ADA]);
    expect(got.map((a) => a.textContent)).toEqual([ADA.slice(0, 7), ADA]);
    got[0].click();
    expect(commitTarget.value).toEqual({ teams: ['payments', 'web'], sha: ADA });
  });

  it('a hex word that is not a commit stays text, and is remembered as not one', async () => {
    const box = place('The colour is abc1234 and ab12cd3 is a code.');
    await settle();
    expect(links(box)).toHaveLength(0);
    expect(box.querySelector('[data-commit]')).toBeNull();
    expect(box.querySelectorAll('[data-commit-state=text]')).toHaveLength(2);
    expect(cachedMatches('abc1234')).toEqual([]);
    expect(box.textContent).toBe('The colour is abc1234 and ab12cd3 is a code.\n');
  });

  it('an ID naming two commits opens the chooser with repository and title; picking one opens it', async () => {
    const box = place('Both have 7c63337 now.');
    await settle();
    const [a] = links(box);
    expect(a.dataset.commitState).toBe('choose');
    const chooser = mount(CommitChooser, { attachTo: document.body });
    cleanup.push(() => chooser.unmount());
    a.click();
    await flushPromises();
    expect(commitTarget.value).toBeNull();
    expect(commitChoice.value?.matches).toHaveLength(2);
    const rows = [...document.querySelectorAll('[data-test=commit-choice]')];
    expect(rows.map((r) => r.querySelector('[data-test=commit-choice-repo]')!.textContent)).toEqual(['main', 'payments-cloud']);
    expect(rows.map((r) => r.querySelector('[data-test=commit-choice-title]')!.textContent)).toEqual(['Web: fix the refund button', 'Cloud: retry the webhook']);
    (rows[1] as HTMLElement).click();
    await flushPromises();
    expect(commitChoice.value).toBeNull();
    expect(commitTarget.value).toMatchObject({ sha: TWIN_CLOUD, repo: 'payments-cloud' });
    expect(commitTarget.value!.teams[0]).toBe('payments');
  });

  it('the tooltip is the commit title, and the Commit view\'s "not pushed" words when origin lacks it', async () => {
    const box = place(`Pushed ${ADA.slice(0, 8)}; local ${LOCAL.slice(0, 8)}.`);
    await settle();
    const [pushed, local] = links(box);
    expect(pushed.title).toBe('Partial refunds: refund part of a payment');
    expect(local.title).toBe(`Receipts: a new layout\n${LOCAL_MARK.long}`);
    expect(local.title).toContain('only on this computer');
  });

  it('one batched call per screen, not one per ID; a cached answer makes no second call', async () => {
    place(`Hand-back: ${ADA.slice(0, 7)}, ${LOCAL.slice(0, 7)} and abc1234.`);
    place(`Report: ${ADA.slice(0, 10)} again.`, 'payments');
    const text = mount(CommitText, { props: { text: `Trail: ${LOCAL.slice(0, 9)}.`, team: 'payments' }, attachTo: document.body });
    cleanup.push(() => text.unmount());
    await settle();
    expect(core.resolves()).toHaveLength(1);
    expect([...core.resolves()[0].params.ids].sort()).toEqual([ADA.slice(0, 10), ADA.slice(0, 7), LOCAL.slice(0, 7), LOCAL.slice(0, 9), 'abc1234'].sort());
    expect(core.resolves()[0].params.team).toBeUndefined();
    // The same IDs on the next screen (a re-render): links at once from the cache, no call.
    document.body.innerHTML = '';
    const again = place(`Again ${ADA.slice(0, 7)} and abc1234.`);
    await settle();
    expect(core.resolves()).toHaveLength(1);
    expect(links(again).map((a) => a.dataset.commit)).toEqual([ADA]);
  });

  it('a failed call leaves the IDs as text and does not ask again in a loop', async () => {
    let n = 0;
    configureCommitResolve({ api: { ...core.api, call: async () => { n++; throw new Error('unknown method'); } } as YawbleApi });
    const box = place(`See ${ADA.slice(0, 7)}.`);
    await settle(); await settle();
    expect(n).toBe(1);
    expect(links(box)).toHaveLength(0);
    expect(box.textContent).toBe(`See ${ADA.slice(0, 7)}.\n`);
  });

  it('the answer read as the contract gives it; an ID left out or a malformed match is no commit; repo null is main', () => {
    const m = { team: 'payments', teamId: 1, repo: null, fullId: ADA.toUpperCase(), shortId: ADA.slice(0, 12), title: 't', pushed: false };
    const answer = { max: 200, results: [{ id: 'abc1234', valid: true, matches: [m] }, { id: 'ff00aa1', valid: true, matches: [{ title: 'no id' }] }] } as any;
    expect(answerMatches(answer, 'ABC1234')).toEqual([{ ...m, fullId: ADA }]);
    expect(answerMatches(answer, 'ff00aa1')).toEqual([]);
    expect(answerMatches(answer, 'bbb2222')).toEqual([]);
    expect(answerMatches(null, 'abc1234')).toEqual([]);
  });

  it('above the core\'s 200 IDs a call, the screen is split into calls of 200', async () => {
    const ids = Array.from({ length: 450 }, (_, i) => `a${i.toString(16).padStart(6, '0')}`);
    place(ids.join(' '));
    await settle();
    expect(core.resolves().map((c) => c.params.ids.length)).toEqual([200, 200, 50]);
    expect(new Set(core.resolves().flatMap((c) => c.params.ids)).size).toBe(450);
  });
  it('team order: the place\'s team first; it alone when it has the commit, else every team, nearest first', () => {
    const a = { team: 'web', repo: null, fullId: ADA, title: 'a', pushed: true };
    const b = { team: 'payments', repo: null, fullId: LOCAL, title: 'b', pushed: true };
    expect(pickMatches([a, b], ['payments', 'web'], 'payments')).toEqual([b]);
    // Only the place's own team narrows; the screen's team (or the first of every team) only orders.
    expect(pickMatches([a, b], ['payments', 'web'])).toEqual([b, a]);
    expect(pickMatches([a, b], ['payments', 'web'], null)).toEqual([b, a]);
    expect(pickMatches([a, b], ['ops', 'payments', 'web'], 'ops')).toEqual([b, a]);
    expect(pickMatches([a, b], ['ops', 'payments', 'web'])).toEqual([b, a]);
    expect(pickMatches([a, a], ['web'])).toEqual([a]);
  });
  it('an ID in two teams, in a place with no team of its own, opens the chooser whatever the team order', async () => {
    for (const all of [['web', 'cloud'], ['cloud', 'web']]) {
      resetCommitResolve();
      document.body.innerHTML = '';
      commitTeams.value = { current: null, all };
      const box = document.createElement('div');
      box.innerHTML = renderMarkdown(`Both teams have ${SHARED.slice(0, 8)}.`);
      document.body.appendChild(box);
      await settle();
      const [a] = links(box);
      expect(a.dataset.commitState).toBe('choose');
      expect(a.dataset.commitIn).toBeUndefined();
      a.click();
      expect(commitChoice.value?.matches.map((m) => m.team)).toEqual(all);
      closeChoice();
    }
  });
  it('the screen\'s team alone does not narrow (it only comes first); the place\'s own team does', async () => {
    commitTeams.value = { current: 'cloud', all: ['web', 'cloud'] };
    const open = document.createElement('div');
    open.innerHTML = renderMarkdown(`Both teams have ${SHARED.slice(0, 8)}.`);
    document.body.appendChild(open);
    const own = place(`Both teams have ${SHARED.slice(0, 8)}.`, 'web');
    await settle();
    expect(links(open)[0].dataset.commitState).toBe('choose');
    links(open)[0].click();
    expect(commitChoice.value?.matches.map((m) => m.team)).toEqual(['cloud', 'web']);
    closeChoice();
    expect(links(own)[0].dataset.commitState).toBe('link');
    expect(links(own)[0].dataset.commitIn).toBe('web');
  });
  it('CommitText narrows only to its own team prop', async () => {
    commitTeams.value = { current: 'cloud', all: ['web', 'cloud'] };
    const bare = mount(CommitText, { props: { text: `Merged ${SHARED.slice(0, 8)}` }, attachTo: document.body });
    const mine = mount(CommitText, { props: { text: `Merged ${SHARED.slice(0, 8)}`, team: 'web' }, attachTo: document.body });
    await settle();
    expect(bare.find('[data-test=commit-link]').attributes('data-commit-state')).toBe('choose');
    expect(mine.find('[data-test=commit-link]').attributes('data-commit-state')).toBe('link');
    expect(mine.find('[data-test=commit-link]').attributes('data-commit-in')).toBe('web');
    bare.unmount(); mine.unmount();
  });
});

describe('commit links through the remote view\'s client, as a Visitor', () => {
  class FakeSocket {
    static OPEN = 1;
    static last: FakeSocket | null = null;
    readyState = 0;
    sent: any[] = [];
    onopen: (() => void) | null = null;
    onmessage: ((m: { data: string }) => void) | null = null;
    onclose: ((e: { reason: string }) => void) | null = null;
    constructor(public url: string) { FakeSocket.last = this; }
    send(text: string) { this.sent.push(JSON.parse(text)); }
    close() {}
    open() { this.readyState = 1; this.onopen?.(); }
    answer(message: unknown) { this.onmessage?.({ data: JSON.stringify(message) }); }
  }

  it('the batch goes over the socket like any read, and a Visitor gets links', async () => {
    vi.stubGlobal('WebSocket', FakeSocket);
    try {
      const { createRemoteApi } = await import('../../src/renderer/src/remoteApi');
      const { loadRemoteAccess, readOnly } = await import('../../src/renderer/src/remoteAccess');
      const api = createRemoteApi('ws://example.invalid/rpc');
      configureCommitResolve({ api });
      const ws = FakeSocket.last!;
      ws.open();
      const access = loadRemoteAccess(api.getInfo);
      const info = ws.sent.find((m) => m.method === 'core.getInfo');
      ws.answer({ id: info.id, result: { version: 'x', remoteAccess: 'read' } });
      await access;
      expect(readOnly.value).toBe(true);

      const box = place(`Visitor reads ${ADA.slice(0, 7)} and abc1234.`);
      await settle();
      const asks = ws.sent.filter((m) => m.method === GIT_COMMITS_RESOLVE);
      expect(asks).toHaveLength(1);
      expect(asks[0].params.ids.sort()).toEqual([ADA.slice(0, 7), 'abc1234']);
      ws.answer({ id: asks[0].id, result: resolve(asks[0].params.ids) });
      await settle();
      expect(links(box).map((a) => a.dataset.commit)).toEqual([ADA]);
    } finally {
      vi.unstubAllGlobals();
      const { applyRemoteAccess } = await import('../../src/renderer/src/remoteAccess');
      applyRemoteAccess('full');
    }
  });
});

describe('every place IDs appear renders a confirmed link', () => {
  const said = `Done: ${ADA.slice(0, 7)}; not a commit: abc1234.`;
  const confirmed = (root: ParentNode) => links(root).map((a) => a.dataset.commit);

  it('rendered markdown (hand-backs and reports in MemberPane, card bodies, decisions in TeamView, Logs, Documents)', async () => {
    // Every one of those is renderMarkdown's HTML in a v-html under a data-commit-team: the document observer finds it.
    const w = mount({ render: () => h('div', { 'data-commit-team': 'payments', innerHTML: renderMarkdown(said) }) }, { attachTo: document.body });
    cleanup.push(() => w.unmount());
    await settle();
    expect(confirmed(w.element)).toEqual([ADA]);
  });

  it('a Concierge answer (ChatMessage)', async () => {
    const w = mount(ChatMessage, { props: { role: 'assistant', text: said }, attachTo: document.body });
    cleanup.push(() => w.unmount());
    await settle();
    expect(confirmed(w.element)).toEqual([ADA]);
  });

  it('a card\'s trail (CardPanel), with the trail\'s hand-back and comment lines', async () => {
    const card: BoardCard = {
      id: 5, team: 'payments', teamId: 1, member: 'DeveloperAda', item: 'B074', title: 'Links', body: `Body names ${LOCAL.slice(0, 7)}.`, status: 'done', lane: 'done',
      placed: true, awaitingManager: false, deliverable: 3, createdAt: '2026-11-18T19:00:00Z', updatedAt: '2026-11-18T19:00:00Z', trailCount: 2,
    } as BoardCard;
    const detail = { ...card, trail: [
      { id: 1, at: '2026-11-18T19:00:00Z', source: 'DeveloperAda', kind: 'handback', text: `Handed back: ${said}` },
      { id: 2, at: '2026-11-18T19:01:00Z', source: 'Manager', kind: 'comment', text: `Merged ${ADA.slice(0, 9)}.` },
    ] };
    core = fakeCore({ 'board.card': () => detail });
    configureCommitResolve({ api: core.api });
    const w = mount(CardPanel, { props: { id: 5, lanes: [{ id: 'done', label: 'Done' }], api: core.api }, attachTo: document.body });
    cleanup.push(() => w.unmount());
    await settle();
    const lines = [...w.element.querySelectorAll('[data-test=card-trail-line]')];
    expect(confirmed(lines[0])).toEqual([ADA]);
    expect(lines[0].textContent).toContain('not a commit: abc1234.');
    expect(confirmed(lines[1])).toEqual([ADA]);
    expect(confirmed(w.element.querySelector('.body')!)).toEqual([LOCAL]);
    expect(core.resolves()).toHaveLength(1);
  });

  it('a member\'s activity line (MemberActivity; the Monitor\'s list uses the same CommitText)', async () => {
    const rows = [{ key: 'a', seq: 1, tone: 'ok', team: 'payments', member: 'DeveloperAda', verb: 'handed back', who: 'DeveloperAda', text: said, at: '2026-11-18T19:00:00Z' }];
    const w = mount(MemberActivity, { props: { rows: rows as any, loaded: true, now: Date.parse('2026-11-18T19:05:00Z') }, attachTo: document.body });
    cleanup.push(() => w.unmount());
    await settle();
    expect(confirmed(w.element)).toEqual([ADA]);
    expect(w.text()).toContain('abc1234');
  });

  it('a deliverable\'s handback (the Deliverables page\'s pane: what was delivered)', async () => {
    const row = paneRow({ id: 10, title: 'Partial refunds', status: 'completed' });
    const handback = { seq: 4, at: '2026-11-18T19:00:00Z', kind: 'handback', from: 'DeveloperAda', member: 'DeveloperAda', text: said };
    core = fakeCore({ 'deliverables.get': () => ({ deliverable: row, cards: [], messages: [handback], spec: null, backlog: true }) });
    configureCommitResolve({ api: core.api });
    const w = await mountPane(row, core.api);
    cleanup.push(() => w.unmount());
    await settle();
    expect(confirmed(w.get('[data-test=deliverable-pane-message]').element)).toEqual([ADA]);
  });

  it('plain text from anywhere (CommitText): text until confirmed, then a link; a click opens it in its team', async () => {
    const w = mount(CommitText, { props: { text: said, team: 'web' }, attachTo: document.body });
    cleanup.push(() => w.unmount());
    expect(links(w.element)).toHaveLength(0);
    await settle();
    const [a] = links(w.element);
    expect(a.title).toBe('Partial refunds: refund part of a payment');
    a.click();
    expect(commitTarget.value).toEqual({ teams: ['payments', 'web'], sha: ADA });
  });
});
