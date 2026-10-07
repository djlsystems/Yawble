// @vitest-environment happy-dom
//
// A WORKFLOW NUMBER SAYS WHY IT SKIPS. The number is the workflow's first row in the instance's
// log, so a fresh install reads 1, 14, 39, 45. Each surface below hovers the one shared wording,
// so the gaps read as what they are rather than as lost work.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  publishSteering: vi.fn(),
  listContainerTriggers: vi.fn().mockResolvedValue([]),
}));

import KanbanCard from '../KanbanCard.vue';
import ActivityFeed from '../ActivityFeed.vue';
import ContainerCard from '../ContainerCard.vue';
import { resetBody } from '../../test/mountQuasar';
import { WORKFLOW_NUMBER_HINT } from '../../lib/workflowNumber';
import type { KanbanCard as Card } from '../../api/kanban';
import { asMemberId, asTeamId, type ContainerSnapshot, type Message } from '../../api/types';

let wrapper: VueWrapper | null = null;

beforeEach(() => {
  setActivePinia(createPinia());
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  vi.unstubAllGlobals();
  resetBody();
});

describe('a shown workflow number hovers what it means', () => {
  it('on a board card', () => {
    const card: Card = {
      id: '2316', team: 'alpha', member: 'DeveloperInes', item: 5, workflowSeq: 39,
      title: 'Read spec', body: '', status: 'queued', laneId: 'todo', progress: [],
      createdAt: '2026-09-18T00:00:00Z', updatedAt: '2026-09-18T00:00:00Z',
      awaitingManager: false, paused: false,
    };
    wrapper = mount(KanbanCard, { props: { card } });

    const number = wrapper.find('.k-card-workflow');
    expect(number.text()).toBe('#39');
    expect(number.attributes('title')).toBe(`Workflow #39. ${WORKFLOW_NUMBER_HINT}`);
  });

  it('on the workflow button of a feed row', async () => {
    const message: Message = {
      seq: 40, type: 'agentContainer.progress', payload: JSON.stringify({ status: 'reading' }),
      source: 'Alpha/Worker', correlationId: 14, causationSeq: null, depth: 1,
      occurredAt: '2026-09-29T10:00:00Z',
    };
    wrapper = mount(ActivityFeed, { props: { feed: [message] }, attachTo: document.body });

    const tooltip = wrapper.find('.feed-workflow').findComponent({ name: 'QTooltip' });
    expect(tooltip.exists(), 'no tooltip on the workflow button').toBe(true);
    (tooltip.vm as unknown as { show: () => void }).show();
    await flushPromises();

    const text = document.body.querySelector('.q-tooltip')?.textContent?.replace(/\s+/g, ' ') ?? '';
    expect(text).toContain('Follow this workflow across every card');
    expect(text).toContain(WORKFLOW_NUMBER_HINT);
  });

  it('on a member card working a workflow', async () => {
    const snapshot: ContainerSnapshot = {
      team: asTeamId('alpha'), id: asMemberId('Worker'), name: 'Worker', agent: 'echo',
      state: 'Running', queueDepth: 0, ceiling: 16, subscribes: [], currentCorrelation: 45, sinceSeq: 0,
    };
    wrapper = mount(ContainerCard, {
      props: { snapshot, feed: [] },
      global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true } },
    });
    await flushPromises();

    const number = wrapper.find('[data-workflow-number]');
    expect(number.text()).toBe('workflow #45');
    expect(number.attributes('title')).toBe(`Workflow #45. ${WORKFLOW_NUMBER_HINT}`);
  });
});
