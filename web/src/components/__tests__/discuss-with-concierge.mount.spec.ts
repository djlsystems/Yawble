// @vitest-environment happy-dom
//
// "DISCUSS WITH CONCIERGE", WHERE A DECISION WAITS ON A PERSON: on the member card carrying the
// needs-decision mark, on a Needs You card in `needs-decision` and on that card opened - and only
// while the decision is open. Never on a blocked or failed card, nor on one with no decision. A
// click leaves exactly that decision for the Concierge panel (`concierge-panel-discuss.mount.spec.ts`
// is what the panel then does with it), and a click on the board's card does not open the card.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { listContainerTriggers } = vi.hoisted(() => ({ listContainerTriggers: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
}));

import ContainerCard from '../ContainerCard.vue';
import KanbanCard from '../KanbanCard.vue';
import KanbanCardPanel from '../KanbanCardPanel.vue';
import { useConciergeAskStore } from '../../stores/conciergeAsk';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { asMemberId, asTeamId, type ContainerSnapshot, type Team } from '../../api/types';
import type { KanbanCard as Card, KanbanCardDetail } from '../../api/kanban';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { decisionRequest } from '../../lib/conciergeDecision';

const Button = '[data-test="discuss-with-concierge"]';

const snapshot = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('Manager'),
  name: 'Manager',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...over,
});

const deciding = { needsDecision: 'Which of the two packages?', needsDecisionSeq: 4187, needsDecisionCorrelation: 4100 };

const card = (over: Partial<Card> = {}): Card => ({
  id: '4100_Manager',
  workflowSeq: 4100,
  team: 'alpha',
  member: 'Manager',
  title: 'Choose a package',
  body: '',
  status: 'needs-decision',
  laneId: 'blocked',
  progress: [],
  createdAt: '2026-10-16T01:00:00Z',
  updatedAt: '2026-10-16T01:10:00Z',
  awaitingManager: false,
  paused: false,
  needsDecisionSeq: 4187,
  needsDecisionCorrelation: 4100,
  ...over,
});

let wrapper: VueWrapper | null = null;

beforeEach(() => {
  setActivePinia(createPinia());
  listContainerTriggers.mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
  useConsoleStore().teams = [{ id: asTeamId('alpha'), name: 'Alpha Team' } as Team];
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  vi.unstubAllGlobals();
  resetBody();
});

async function memberCard(snap: ContainerSnapshot) {
  wrapper = mount(ContainerCard, {
    props: { snapshot: snap, feed: [] },
    global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true } },
  });
  await flushPromises();
  return wrapper;
}

function boardCard(props: Card) {
  wrapper = mount(KanbanCard, { props: { card: props } });
  return wrapper;
}

async function openedCard(props: Card) {
  const kanban = useKanbanStore();
  kanban.load = vi.fn(async () => {});
  kanban.select = vi.fn(async () => {});
  wrapper = mount(KanbanCardPanel);
  const detail: KanbanCardDetail = { ...props, trail: [] };
  kanban.selectedId = detail.id;
  kanban.detail = detail;
  await flushPromises();
}

const asked = () => useConciergeAskStore().asked;

describe('on the member card', () => {
  it('shows beside "needs you", and a click asks about that decision', async () => {
    const card = await memberCard(snapshot(deciding));

    expect(card.text()).toContain('needs you');
    const button = card.find(Button);
    expect(button.exists()).toBe(true);
    expect(button.text()).toContain('Discuss with Concierge');

    await button.trigger('click');

    expect(asked()).toEqual({ team: 'alpha', teamName: 'Alpha Team', member: 'Manager', workflow: 4100, seq: 4187 });
    expect(useConciergeAskStore().clicks).toBe(1);
  });

  it('asks with the row, never the question\'s own words', async () => {
    const card = await memberCard(snapshot(deciding));
    await card.find(Button).trigger('click');

    const request = decisionRequest(asked()!);
    expect(request).toContain('seq 4187');
    expect(request).not.toContain(deciding.needsDecision);
    expect(Object.values(asked()!)).not.toContain(deciding.needsDecision);
  });

  it('does not show on a blocked member, a failed one, or one with no decision', async () => {
    expect((await memberCard(snapshot({ blocked: 'gave up' }))).find(Button).exists()).toBe(false);
    wrapper!.unmount();
    expect((await memberCard(snapshot({ ...deciding, failed: 'exit 1' }))).find(Button).exists()).toBe(false);
    wrapper!.unmount();
    expect((await memberCard(snapshot({ ...deciding, blocked: 'gave up' }))).find(Button).exists()).toBe(false);
    wrapper!.unmount();
    expect((await memberCard(snapshot())).find(Button).exists()).toBe(false);
  });

  it('does not show once the mark has cleared, even with the row numbers left behind', async () => {
    const card = await memberCard(snapshot({ needsDecisionSeq: 4187, needsDecisionCorrelation: 4100 }));

    expect(card.find(Button).exists()).toBe(false);
  });
});

describe('on a Needs You card on the board', () => {
  it('shows on a needs-decision card, and a click asks about it without opening the card', async () => {
    const shown = boardCard(card());

    const button = shown.find(Button);
    expect(button.exists()).toBe(true);

    await button.trigger('click');

    expect(asked()).toEqual({ team: 'alpha', teamName: 'Alpha Team', member: 'Manager', workflow: 4100, seq: 4187 });
    expect(shown.emitted('open')).toBeUndefined();
  });

  it('does not show on a blocked or failed card, nor on a card with no decision', () => {
    for (const status of ['blocked', 'failed', 'running', 'queued'] as const) {
      expect(boardCard(card({ status })).find(Button).exists(), status).toBe(false);
      wrapper!.unmount();
    }
    expect(boardCard(card({ needsDecisionSeq: null, needsDecisionCorrelation: null })).find(Button).exists()).toBe(false);
  });
});

describe('on the opened card', () => {
  it('shows on a needs-decision card, and a click asks about it', async () => {
    await openedCard(card());

    const button = bodyFind(Button) as HTMLElement | null;
    expect(button).not.toBeNull();

    button!.click();

    expect(asked()).toEqual({ team: 'alpha', teamName: 'Alpha Team', member: 'Manager', workflow: 4100, seq: 4187 });
  });

  it('does not show on a blocked or failed card', async () => {
    await openedCard(card({ status: 'blocked', needsDecisionSeq: null, needsDecisionCorrelation: null }));
    expect(bodyFind(Button)).toBeNull();
    wrapper!.unmount();
    resetBody();

    await openedCard(card({ status: 'failed' }));
    expect(bodyFind(Button)).toBeNull();
  });
});
