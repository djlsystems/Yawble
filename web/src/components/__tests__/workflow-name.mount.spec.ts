// @vitest-environment happy-dom
//
// A WORKFLOW IS NAMED BY ITS WORK, everywhere its number shows: the item's chip, then the words
// (the item's title, else the instruction's subject), then the number. With neither the number
// stands alone, as it always did - nothing is estimated and the number is never dropped. The chip
// opens the Backlog on the item; the number opens the team's Workflows dialog.
//
// Citations are built with `itemLabel` over a number, never written out (`CodeCitationTests`).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getWip, listLocalRepos, listRemovals } = vi.hoisted(() => ({
  getWip: vi.fn(),
  listLocalRepos: vi.fn(),
  listRemovals: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
  listContainerTriggers: vi.fn().mockResolvedValue([]),
  publishSteering: vi.fn(),
  getWip,
  listLocalRepos,
  listRemovals,
}));

import WorkflowName from '../WorkflowName.vue';
import ContainerCard from '../ContainerCard.vue';
import ActivityFeed from '../ActivityFeed.vue';
import TeamKpiStrip from '../TeamKpiStrip.vue';
import KanbanCardPanel from '../KanbanCardPanel.vue';
import TeamsView from '../TeamsView.vue';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import { itemLabel } from '../../lib/backlog';
import { workflowNumberTitle } from '../../lib/workflowNumber';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type Message,
  type Team,
  type TeamWorkflows,
  type TeamWorkflowTiming,
} from '../../api/types';
import type { KanbanCardDetail } from '../../api/kanban';

const team = asTeamId('alpha');
const ITEM = 197;
const TITLE = 'A workflow is named by its work';
const WORKFLOW = 18195;

function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: WORKFLOW,
    state: 'Running',
    startedAt: '2026-10-19T09:00:00Z',
    endedAt: null,
    serverNow: '2026-10-19T10:00:00Z',
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: '2026-10-19T09:30:00Z',
    awaitingFrom: null,
    subject: null,
    item: { id: ITEM, title: TITLE },
    pausedAt: null,
    pausedReason: null,
    ...over,
  } as TeamWorkflowTiming;
}

function payload(workflows: TeamWorkflowTiming[]): TeamWorkflows {
  return {
    available: true,
    openCount: workflows.filter((entry) => entry.endedAt === null).length,
    totalCount: workflows.length,
    earliestStartedAt: null,
    serverNow: '2026-10-19T10:00:00Z',
    workflows,
    missing: null,
  } as unknown as TeamWorkflows;
}

let wrapper: VueWrapper | null = null;

beforeEach(() => {
  setActivePinia(createPinia());
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
  getWip.mockResolvedValue({ max: 4, running: [], waiting: [] });
  listRemovals.mockResolvedValue([]);
  listLocalRepos.mockResolvedValue([]);
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  vi.unstubAllGlobals();
  resetBody();
});

const text = (root: ParentNode, selector: string) =>
  root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;

describe('WorkflowName', () => {
  it('renders the chip, the words and the number, in that order', () => {
    wrapper = mount(WorkflowName, { props: { number: WORKFLOW, item: { id: ITEM, title: TITLE }, subject: 'Build it', team } });

    const chip = wrapper.find('[data-workflow-item]');
    expect(chip.text()).toBe(itemLabel(ITEM));
    expect(chip.attributes('title')).toBe(TITLE);
    // The item's title wins over the subject, and the whole of it is the tooltip.
    expect(wrapper.find('[data-workflow-words]').text()).toBe(TITLE);
    expect(wrapper.find('[data-workflow-words]').attributes('title')).toBe(TITLE);
    const number = wrapper.find('[data-workflow-number]');
    expect(number.text()).toBe(`#${WORKFLOW}`);
    expect(number.attributes('title')).toBe(workflowNumberTitle(WORKFLOW));
    expect(wrapper.text().replace(/\s+/g, ' ')).toBe(`${itemLabel(ITEM)} ${TITLE} #${WORKFLOW}`);
  });

  it('falls back to the subject, with no chip, when there is no item', () => {
    wrapper = mount(WorkflowName, { props: { number: WORKFLOW, item: null, subject: 'Build the component' } });

    expect(wrapper.find('[data-workflow-item]').exists()).toBe(false);
    expect(wrapper.find('[data-workflow-words]').text()).toBe('Build the component');
    expect(wrapper.find('[data-workflow-number]').text()).toBe(`#${WORKFLOW}`);
  });

  it('renders the number alone when nothing else is known', () => {
    wrapper = mount(WorkflowName, { props: { number: WORKFLOW, item: null, subject: null, noun: 'workflow' } });

    expect(wrapper.find('[data-workflow-item]').exists()).toBe(false);
    expect(wrapper.find('[data-workflow-words]').exists()).toBe(false);
    expect(wrapper.text()).toBe(`workflow #${WORKFLOW}`);
  });

  it('renders a title as text, never markup', () => {
    wrapper = mount(WorkflowName, { props: { number: WORKFLOW, item: { id: ITEM, title: '<b>bold</b>' } } });

    expect(wrapper.find('b').exists()).toBe(false);
    expect(wrapper.find('[data-workflow-words]').text()).toBe('<b>bold</b>');
  });

  it('opens the Backlog on the item from the chip', async () => {
    wrapper = mount(WorkflowName, { props: { number: WORKFLOW, item: { id: ITEM, title: TITLE }, team } });
    const nav = useWorkflowNavStore();

    await wrapper.find('[data-workflow-item]').trigger('click');

    expect(nav.backlogOpen).toBe(true);
    expect(nav.backlogItem).toBe(ITEM);
  });

  it('opens the team\'s board and asks for its Workflows dialog from the number', async () => {
    const board = useConsoleStore();
    board.$patch({ teams: [{ id: team, name: 'Alpha', containers: [] } as unknown as Team], view: 'kanban' });
    vi.spyOn(board, 'setActiveTeam').mockImplementation((id) => { board.activeTeamId = id; });
    wrapper = mount(WorkflowName, { props: { number: WORKFLOW, item: { id: ITEM, title: TITLE }, team } });
    const nav = useWorkflowNavStore();

    await wrapper.find('[data-workflow-number]').trigger('click');

    expect(board.view).toBe('board');
    expect(board.activeTeamId).toBe(team);
    expect(nav.workflowsTeam).toBe(team);
  });
});

describe('where the name shows', () => {
  it('names the member card\'s workflow from the team\'s workflows payload', async () => {
    useConsoleStore().$patch({ workflows: { [team]: payload([timing()]) } });
    const snapshot: ContainerSnapshot = {
      team, id: asMemberId('Worker'), name: 'Worker', agent: 'echo',
      state: 'Running', queueDepth: 0, ceiling: 16, subscribes: [], currentCorrelation: WORKFLOW, sinceSeq: 0,
    };
    wrapper = mount(ContainerCard, {
      props: { snapshot, feed: [] },
      global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true } },
    });
    await flushPromises();

    expect(text(wrapper.element, '[data-member-workflow]')).toBe(`${itemLabel(ITEM)} ${TITLE} #${WORKFLOW}`);
  });

  it('shows the member card\'s number alone for a workflow outside the payload\'s rows', async () => {
    useConsoleStore().$patch({ workflows: { [team]: payload([timing({ correlation: 17000 })]) } });
    const snapshot: ContainerSnapshot = {
      team, id: asMemberId('Worker'), name: 'Worker', agent: 'echo',
      state: 'Running', queueDepth: 0, ceiling: 16, subscribes: [], currentCorrelation: WORKFLOW, sinceSeq: 0,
    };
    wrapper = mount(ContainerCard, {
      props: { snapshot, feed: [] },
      global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true } },
    });
    await flushPromises();

    expect(text(wrapper.element, '[data-member-workflow]')).toBe(`workflow #${WORKFLOW}`);
  });

  it('names a feed row\'s workflow, its number still the Follow button', async () => {
    useConsoleStore().$patch({ workflows: { [team]: payload([timing({ item: null, subject: 'Build the component' })]) } });
    const message: Message = {
      seq: 18200, type: 'agentContainer.progress', payload: JSON.stringify({ status: 'reading' }),
      source: 'alpha/Worker', correlationId: WORKFLOW, causationSeq: null, depth: 1,
      occurredAt: '2026-10-19T10:00:00Z',
    };
    wrapper = mount(ActivityFeed, { props: { feed: [message], team }, attachTo: document.body });
    await flushPromises();

    const name = wrapper.find('.feed-workflow-name');
    expect(name.find('[data-workflow-words]').text()).toBe('Build the component');
    expect(name.find('.feed-workflow').text()).toBe(`#${WORKFLOW}`);
  });

  it('puts the item chip in front of the subject on a Workflows dialog row', async () => {
    const board = useConsoleStore();
    board.$patch({ teams: [{ id: team, name: 'Alpha', containers: [] } as unknown as Team], activeTeamId: team });
    wrapper = mount(TeamKpiStrip, {
      props: { teamId: team, containers: [], usage: null, timing: null, workflows: payload([timing()]), clockOffset: 0 },
    });
    await flushPromises();
    await wrapper.find('.team-kpi-members').trigger('click');
    await flushPromises();

    const row = bodyFind('[data-workflow-row-name]')!;
    const chip = row.querySelector('[data-workflow-item]')!;
    const words = row.querySelector('[data-workflow-words]')!;
    expect(chip.textContent).toBe(itemLabel(ITEM));
    expect(words.textContent).toBe(TITLE);
    // The chip comes first.
    expect(chip.compareDocumentPosition(words) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(text(row, '[data-workflow-number]')).toBe(`#${WORKFLOW}`);
  });

  it('names the tile\'s one workflow, and opens the Workflows dialog when its number asks', async () => {
    const board = useConsoleStore();
    board.$patch({ teams: [{ id: team, name: 'Alpha', containers: [] } as unknown as Team], activeTeamId: team });
    vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});
    wrapper = mount(TeamKpiStrip, {
      props: { teamId: team, containers: [], usage: null, timing: null, workflows: payload([timing()]), clockOffset: 0 },
    });
    await flushPromises();

    expect(text(wrapper.element, '[data-test="workflow-name"]')).toBe(`${itemLabel(ITEM)} ${TITLE} #${WORKFLOW}`);
    expect(bodyFind('[data-workflow-row-name]')).toBeNull();

    await wrapper.find('[data-test="workflow-name"] [data-workflow-number]').trigger('click');
    await flushPromises();

    expect(bodyFind('[data-workflow-row-name]')).not.toBeNull();
  });

  it('names the workflow in the Kanban card panel\'s header', async () => {
    useConsoleStore().$patch({ workflows: { [team]: payload([timing()]) } });
    const kanban = useKanbanStore();
    wrapper = mount(KanbanCardPanel);
    const detail: KanbanCardDetail = {
      id: 'c-1', workflowSeq: WORKFLOW, team, member: 'Manager', item: ITEM, title: 'Build it', body: '',
      status: 'running', laneId: 'in-progress', progress: [], createdAt: '2026-10-19T09:00:00Z',
      updatedAt: '2026-10-19T09:00:00Z', awaitingManager: false, paused: false, trail: [],
    };
    kanban.selectedId = detail.id;
    kanban.detail = detail;
    await flushPromises();

    const head = bodyFind('.k-panel-head')!;
    expect(text(head, '[data-workflow-name]')).toBe(`${itemLabel(ITEM)} ${TITLE} #${WORKFLOW}`);
    // The workflow's chip is the card's own item, so it shows once.
    expect(head.querySelectorAll('[data-card-item]').length).toBe(0);
  });

  it('keeps the card\'s own item chip and the number alone when the team\'s payload is not loaded', async () => {
    const kanban = useKanbanStore();
    wrapper = mount(KanbanCardPanel);
    const detail: KanbanCardDetail = {
      id: 'c-2', workflowSeq: WORKFLOW, team, member: 'Manager', item: ITEM, title: 'Build it', body: '',
      status: 'running', laneId: 'in-progress', progress: [], createdAt: '2026-10-19T09:00:00Z',
      updatedAt: '2026-10-19T09:00:00Z', awaitingManager: false, paused: false, trail: [],
    };
    kanban.selectedId = detail.id;
    kanban.detail = detail;
    await flushPromises();

    const head = bodyFind('.k-panel-head')!;
    expect(text(head, '[data-card-item]')).toBe(itemLabel(ITEM));
    expect(text(head, '[data-workflow-name]')).toBe(`workflow #${WORKFLOW}`);
  });
});

describe('the Teams table', () => {
  async function mountTeams(workflows: TeamWorkflows) {
    const board = useConsoleStore();
    board.$patch({
      teams: [{ id: team, name: 'Alpha', paused: false, containers: [] } as unknown as Team],
      workflows: { [team]: workflows },
    });
    vi.spyOn(board, 'refresh').mockResolvedValue();
    vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});
    wrapper = mount(TeamsView, { attachTo: document.body });
    await flushPromises();
  }

  it('shows the items of the row\'s open workflows under Status, newest first, one per item', async () => {
    await mountTeams(payload([
      timing({ correlation: 18300, item: { id: 201, title: 'Newer item' } }),
      timing({ correlation: 18250, item: { id: ITEM, title: TITLE } }),
      timing({ correlation: 18195, item: { id: ITEM, title: TITLE } }),
      timing({ correlation: 18100, item: { id: 150, title: 'Finished item' }, endedAt: '2026-10-19T09:40:00Z' }),
      timing({ correlation: 18050, item: null, subject: 'A tell' }),
    ]));

    const chips = [...document.body.querySelectorAll('[data-col-status] [data-team-item]')];
    expect(chips.map((chip) => chip.textContent)).toEqual([itemLabel(201), itemLabel(ITEM)]);
    expect(chips.map((chip) => chip.getAttribute('title'))).toEqual(['Newer item', TITLE]);
  });

  it('opens the Backlog on the item from a chip', async () => {
    await mountTeams(payload([timing()]));
    const nav = useWorkflowNavStore();

    (document.body.querySelector('[data-team-item]') as HTMLButtonElement).click();
    await flushPromises();

    expect(nav.backlogOpen).toBe(true);
    expect(nav.backlogItem).toBe(ITEM);
  });

  it('shows nothing, not a word, when no item is in flight', async () => {
    await mountTeams(payload([timing({ item: null, subject: 'A tell' })]));

    expect(document.body.querySelector('[data-team-items]')).toBeNull();
  });
});
