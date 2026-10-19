// @vitest-environment happy-dom
//
// NEEDS YOU ON THE APP BAR: the count is the route's answer (both parts, nothing folded) and is
// drawn in the needs-you state token above 0 and muted at 0; the panel groups the rows by kind, each
// row names its team, member or item, the platform's words and when it began (`localStamp`), and its
// one action lands where it says; a decision row offers Discuss with Concierge. The live part is
// re-read on demand, the stored part on a 30 s timer and when the panel opens.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';

const { getNeedsYou, follow } = vi.hoisted(() => ({ getNeedsYou: vi.fn(), follow: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getNeedsYou,
}));

vi.mock('../../lib/consoleLinks', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  follow,
}));

import NeedsYouControl from '../NeedsYouControl.vue';
import { StoredEveryMs, useNeedsYouStore } from '../../stores/needsYou';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import { useConciergeAskStore } from '../../stores/conciergeAsk';
import { cardHref, memberHref } from '../../lib/consoleLinks';
import { itemLabel } from '../../lib/backlog';
import { localStamp, wireInstant } from '../../lib/localTime';
import { NeedsYouKindWords } from '../../lib/needsYou';
import type { NeedsYouItem } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const Now = '2026-10-19T15:00:00Z';

function row(over: Partial<NeedsYouItem> & Pick<NeedsYouItem, 'kind' | 'where'>): NeedsYouItem {
  return {
    team: { id: 'alpha', name: 'Alpha team' },
    member: null,
    card: null,
    workflow: null,
    item: null,
    since: '2026-10-19T14:00:00Z',
    words: 'Something waits.',
    ...over,
  };
}

const decision = row({
  kind: 'decision',
  member: 'DeveloperInes',
  workflow: 4100,
  needsDecisionSeq: 4187,
  since: '2026-10-19T14:50:00Z',
  words: 'Which of the two packages?',
  where: { view: 'workflow', team: 'alpha', workflow: 4100 },
});
const blocked = row({
  kind: 'blocked',
  member: 'Mailer',
  team: { id: 'beta', name: 'Beta team' },
  workflow: 4200,
  since: '2026-10-19T14:40:00Z',
  words: 'CANNOT REACH the mail server.',
  where: { view: 'workflow', team: 'beta', workflow: 4200 },
});
const failed = row({
  kind: 'failed',
  member: 'Tester',
  card: '4300_Tester',
  workflow: 4300,
  failureClass: 'rate',
  since: '2026-10-18T09:15:00Z',
  words: 'The provider limited the rate.',
  where: { view: 'card', team: 'alpha', card: '4300_Tester' },
});
const waiting = row({
  kind: 'waiting-input',
  team: { id: 'resumes', name: 'Resumes' },
  words: 'waiting for a file in Resume/',
  where: { view: 'solution', team: 'resumes' },
});
const mergeReady = row({
  kind: 'merge-ready',
  item: { id: 201, title: 'One list of what waits' },
  since: '2026-10-19T13:00:00Z',
  words: 'The team branch is on origin and not merged.',
  where: { view: 'merge', team: 'alpha', item: 201 },
});
const stranded = row({
  kind: 'stranded',
  item: { id: 77, title: 'An older job' },
  since: '2026-10-19T12:00:00Z',
  words: 'The work continued in workflow 4400.',
  where: { view: 'backlog', team: 'alpha', item: 77 },
});
const removal = row({
  kind: 'removal',
  team: { id: 'gone', name: 'Gone team' },
  since: '2026-10-19T11:00:00Z',
  words: 'The removal of team Gone team did not finish.',
  where: { view: 'removals', team: 'gone' },
});

const Live = [decision, blocked, waiting, failed];
const Stored = [mergeReady, stranded, removal];

function answer(live: NeedsYouItem[], stored: NeedsYouItem[]) {
  getNeedsYou.mockImplementation(async (part: string) => ({ items: part === 'live' ? live : stored }));
}

let wrapper: VueWrapper | undefined;
let router: Router;

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
  vi.setSystemTime(Date.parse(Now));
  getNeedsYou.mockReset();
  follow.mockReset();
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.useRealTimers();
  resetBody();
});

async function mountControl() {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/console', component: { template: '<div />' } },
      { path: '/solutions/:team', component: { template: '<div />' } },
      { path: '/:any(.*)', component: { template: '<div />' } },
    ],
  });
  await router.push('/console');
  wrapper = mount(NeedsYouControl, { global: { plugins: [router] }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}

const control = () => wrapper!.get('[data-test="needs-you"]');

async function openPanel() {
  await control().trigger('click');
  await flushPromises();
}

const rows = () => [...document.body.querySelectorAll<HTMLElement>('[data-test="needs-you-row"]')];
const rowOf = (kind: string) => rows().find((node) => node.dataset.kind === kind)!;

describe('the header count', () => {
  it("is the route's answer, both parts, in the needs-you token above 0", async () => {
    answer(Live, Stored);
    await mountControl();

    expect(getNeedsYou.mock.calls.map(([part]) => part).sort()).toEqual(['live', 'stored']);
    expect(wrapper!.get('[data-test="needs-you-count"]').text()).toBe(String(Live.length + Stored.length));
    expect(control().attributes('data-state')).toBe('needs-you');
    expect(control().classes()).toContain('needs-you--waiting');
    expect(control().attributes('aria-label')).toBe('Needs you: 7');
    expect(control().text()).toContain('Needs you');
  });

  it('reads 0 muted when the route answers nothing', async () => {
    answer([], []);
    await mountControl();

    expect(wrapper!.get('[data-test="needs-you-count"]').text()).toBe('0');
    expect(control().attributes('data-state')).toBe('muted');
    expect(control().classes()).toContain('needs-you--muted');
  });

  it('draws the waiting state through the needs-you token and the muted one through the muted ink', () => {
    const source = readFileSync(join(import.meta.dirname, '..', 'NeedsYouControl.vue'), 'utf8');

    expect(source).toMatch(/\.needs-you--waiting\s*\{\s*color:\s*var\(--os-state-needs-you\);/);
    expect(source).toMatch(/\.needs-you--muted\s*\{\s*color:\s*var\(--os-ink-muted\);/);
  });

  it('says it has not read the route rather than reading 0, when the read fails', async () => {
    getNeedsYou.mockRejectedValue(new Error('HTTP 503'));
    await mountControl();

    expect(wrapper!.get('[data-test="needs-you-count"]').text()).toBe('–');
    expect(control().attributes('data-state')).toBe('muted');
    expect(control().attributes('aria-label')).toBe('Needs you: not read (HTTP 503)');
  });

  it('follows the live part when it is read again (a push), with no other figure folded in', async () => {
    answer(Live, Stored);
    await mountControl();
    answer([decision], Stored);

    await useNeedsYouStore().loadLive();
    await flushPromises();

    expect(wrapper!.get('[data-test="needs-you-count"]').text()).toBe(String(1 + Stored.length));
  });
});

describe('freshness', () => {
  it('reads the stored part every 30 s and not the live one', async () => {
    answer(Live, Stored);
    await mountControl();
    getNeedsYou.mockClear();

    vi.advanceTimersByTime(StoredEveryMs);
    await flushPromises();

    expect(getNeedsYou.mock.calls).toEqual([['stored']]);
  });

  it('reads the stored part when the panel opens', async () => {
    answer(Live, Stored);
    await mountControl();
    getNeedsYou.mockClear();

    await openPanel();

    expect(getNeedsYou.mock.calls.map(([part]) => part)).toContain('stored');
  });

  it('coalesces a burst of pushes into one read in flight and one after it', async () => {
    let release: () => void = () => {};
    answer(Live, Stored);
    await mountControl();
    getNeedsYou.mockClear();
    getNeedsYou.mockImplementation(
      (part: string) => new Promise((resolve) => (release = () => resolve({ items: part === 'live' ? Live : Stored }))),
    );

    const store = useNeedsYouStore();
    const first = store.loadLive();
    void store.loadLive();
    void store.loadLive();
    release();
    await flushPromises();
    release();
    await first;

    expect(getNeedsYou.mock.calls).toEqual([['live'], ['live']]);
  });
});

describe('the panel', () => {
  it('groups the rows by kind, deciding first and tidying up last', async () => {
    answer(Live, Stored);
    await mountControl();
    await openPanel();

    const groups = [...document.body.querySelectorAll<HTMLElement>('[data-needs-you-group]')];
    expect(groups.map((group) => group.dataset.needsYouGroup)).toEqual([
      'decision',
      'blocked',
      'failed',
      'waiting-input',
      'merge-ready',
      'stranded',
      'removal',
    ]);
    expect(groups[0]!.textContent).toContain(`${NeedsYouKindWords.decision} (1)`);
    expect(groups.map((group) => group.querySelectorAll('[data-test="needs-you-row"]').length)).toEqual([1, 1, 1, 1, 1, 1, 1]);
  });

  it("names the row's team, its member or item, the platform's words and when it began", async () => {
    answer(Live, Stored);
    await mountControl();
    await openPanel();

    const blockedRow = rowOf('blocked');
    expect(blockedRow.querySelector('[data-test="needs-you-team"]')!.textContent).toBe('Beta team');
    expect(blockedRow.querySelector('[data-test="needs-you-subject"]')!.textContent).toContain('Mailer');
    expect(blockedRow.querySelector('[data-test="needs-you-words"]')!.textContent).toBe(blocked.words);
    expect(blockedRow.querySelector('[data-test="needs-you-since"]')!.textContent).toBe(localStamp(wireInstant(blocked.since)));

    const mergeRow = rowOf('merge-ready');
    expect(mergeRow.querySelector('[data-test="needs-you-subject"]')!.textContent).toContain(`${itemLabel(201)} One list of what waits`);

    // Another day carries its date: the same formatter, under whatever zone the run is in.
    expect(rowOf('failed').querySelector('[data-test="needs-you-since"]')!.textContent).toBe(
      localStamp(wireInstant(failed.since)),
    );
  });

  it('says nothing is waiting when the route answers nothing', async () => {
    answer([], []);
    await mountControl();
    await openPanel();

    expect(document.body.querySelector('[data-test="needs-you-empty"]')?.textContent).toContain('Nothing is waiting on you.');
    expect(rows()).toHaveLength(0);
  });

  it('offers Discuss with Concierge on a decision row, and a click hands the Concierge that decision', async () => {
    answer(Live, Stored);
    await mountControl();
    await openPanel();

    const decisionRow = rowOf('decision');
    expect(decisionRow.querySelector('[data-test="needs-you-open"]')).toBeNull();
    const discuss = decisionRow.querySelector<HTMLElement>('[data-test="discuss-with-concierge"]')!;
    expect(discuss.textContent).toContain('Discuss with Concierge');

    discuss.click();
    await flushPromises();

    expect(useConciergeAskStore().asked).toEqual({
      team: 'alpha',
      teamName: 'Alpha team',
      member: 'DeveloperInes',
      workflow: 4100,
      seq: 4187,
    });
  });

  it('offers Open, not Discuss, on every other row', async () => {
    answer(Live, Stored);
    await mountControl();
    await openPanel();

    for (const kind of ['blocked', 'failed', 'waiting-input', 'merge-ready', 'stranded', 'removal']) {
      const node = rowOf(kind);
      expect(node.querySelector('[data-test="discuss-with-concierge"]'), kind).toBeNull();
      expect(node.querySelector('[data-test="needs-you-open"]')?.textContent, kind).toContain('Open');
    }
  });
});

describe("each row's Open lands where it says", () => {
  async function openRow(kind: string) {
    await openPanel();
    rowOf(kind).querySelector<HTMLElement>('[data-test="needs-you-open"]')!.click();
    await flushPromises();
  }

  beforeEach(() => answer(Live, Stored));

  it("a member's workflow: that member's card on its board (the UX 4 deep link)", async () => {
    await mountControl();
    await openRow('blocked');

    expect(follow).toHaveBeenCalledWith(memberHref('beta', 'Mailer'));
  });

  it("a workflow naming no member: the team's Workflows dialog", async () => {
    answer([row({ kind: 'blocked', where: { view: 'workflow', team: 'beta', workflow: 4200 } })], []);
    await mountControl();
    await openRow('blocked');

    expect(useConsoleStore().activeTeamId).toBe('beta');
    expect(useWorkflowNavStore().workflowsTeam).toBe('beta');
  });

  it('a card: the card on the Kanban, with its panel asked for', async () => {
    await mountControl();
    await openRow('failed');

    expect(follow).toHaveBeenCalledWith(cardHref('4300_Tester'));
    expect(useKanbanStore().panelAsk).toBe('4300_Tester');
  });

  it("a stranded item: the Backlog on that item", async () => {
    await mountControl();
    await openRow('stranded');

    const nav = useWorkflowNavStore();
    expect(nav.backlogOpen).toBe(true);
    expect(nav.backlogItem).toBe(77);
  });

  it("merge-ready: the team made active and its Git dialog asked for", async () => {
    await mountControl();
    const asks = useWorkflowNavStore().gitAsks;
    await openRow('merge-ready');

    expect(useConsoleStore().activeTeamId).toBe('alpha');
    expect(useWorkflowNavStore().gitAsks).toBe(asks + 1);
  });

  it("a solution's missing input: its solution panel", async () => {
    await mountControl();
    await openRow('waiting-input');
    await flushPromises();

    expect(router.currentRoute.value.path).toBe('/solutions/resumes');
  });

  it('an unfinished removal: the Teams view, where removals are listed', async () => {
    await mountControl();
    await openRow('removal');

    expect(useConsoleStore().view).toBe('teams');
  });
});
