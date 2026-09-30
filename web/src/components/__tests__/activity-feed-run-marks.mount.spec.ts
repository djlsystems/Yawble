// @vitest-environment happy-dom
//
// WHY A FINISHED RUN WOKE NOBODY, ON THE FEED. A `completed` row marked `quiet`, or carrying
// `workflowDeclared` (the platform declared the run's workflow as it ended, so the Manager was not
// woken), says so on its row. A failure, and a completion without either key, carries no mark.
import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import type { Message } from '../../api/types';

import ActivityFeed from '../ActivityFeed.vue';
import '../../test/mountQuasar';

const row = (seq: number, type: string, payload: Record<string, unknown>): Message => ({
  seq,
  type,
  payload: JSON.stringify({ exitCode: 0, output: 'GO', ...payload }),
  source: 'Mail/Echo',
  correlationId: 40,
  causationSeq: 39,
  depth: 1,
  occurredAt: '2026-09-30T10:00:00Z',
});

const mountFeed = (feed: Message[]) =>
  mount(ActivityFeed, { props: { feed, team: 'Mail', name: 'Echo', sinceSeq: 0 } });

const marks = (wrapper: ReturnType<typeof mountFeed>) =>
  wrapper.findAll('.q-item').map((item) => item.find('.feed-run-mark').exists() ? item.find('.feed-run-mark').text() : null);

beforeEach(() => setActivePinia(createPinia()));

describe('ActivityFeed run marks', () => {
  it('marks a completion whose workflow the platform declared, and a quiet one, and nothing else', () => {
    const wrapper = mountFeed([
      row(44, 'agentContainer.completed', { workflowDeclared: true }),
      row(43, 'agentContainer.completed', { quiet: true }),
      row(42, 'agentContainer.completed', {}),
      row(41, 'agentContainer.failed', { workflowDeclared: true, exitCode: 1 }),
    ]);

    expect(marks(wrapper)).toEqual(['workflow declared', 'quiet', null, null]);
  });
});
