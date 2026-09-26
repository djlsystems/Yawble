// @vitest-environment happy-dom
//
// A CARD'S "LOAD OLDER". History is read through `?before=` into the store's list for this
// card, past rows that belong to other members, down to the container's first message.
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { computed, defineComponent, h } from 'vue';
import type { Message } from '../../api/types';

const { getMessagesBefore } = vi.hoisted(() => ({ getMessagesBefore: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMessagesBefore,
}));

import ActivityFeed from '../ActivityFeed.vue';
import { useConsoleStore } from '../../stores/console';
import '../../test/mountQuasar';

const message = (seq: number, source = 'T/Writer'): Message => ({
  seq,
  type: 'agentContainer.progress',
  payload: JSON.stringify({ container: source, status: `line ${seq}` }),
  source,
  correlationId: 1,
  causationSeq: null,
  depth: 0,
  occurredAt: '2026-09-20T10:00:00Z',
});

/** The card's own wiring: the feed is the STORE's list, so history read into it shows up here. */
function mountFeed(sinceSeq: number) {
  const board = useConsoleStore();

  return mount(
    defineComponent(() => {
      const feed = computed(() => board.activityFor('T', 'Writer'));
      return () => h(ActivityFeed, { feed: feed.value, team: 'T', name: 'Writer', sinceSeq });
    }),
  );
}

async function clickLoadOlder(wrapper: ReturnType<typeof mountFeed>) {
  await wrapper.find('.cursor-sentinel button').trigger('click');
  await flushPromises();
}

beforeEach(() => {
  setActivePinia(createPinia());
  getMessagesBefore.mockReset();
});

describe('ActivityFeed load older', () => {
  it('reads below the oldest row it holds and keeps only this member', async () => {
    const board = useConsoleStore();
    board.record([message(500), message(501)]);

    getMessagesBefore.mockResolvedValueOnce([message(499, 'T/Other'), message(420), message(410, 'U/Writer')]);

    const wrapper = mountFeed(0);
    await clickLoadOlder(wrapper);

    expect(getMessagesBefore).toHaveBeenCalledWith('T', 'Writer', 500, 200);
    expect(board.activityFor('T', 'Writer').map((m) => m.seq)).toEqual([501, 500, 420]);
    expect(wrapper.text()).toContain('line 420');
  });

  it('moves the cursor past pages of other members and stops at the first message', async () => {
    const board = useConsoleStore();
    board.record([message(500)]);

    getMessagesBefore
      .mockResolvedValueOnce([message(450, 'T/Other'), message(300, 'T/Other')])
      .mockResolvedValueOnce([message(250), message(100, 'T/Other')]);

    const wrapper = mountFeed(99);
    await clickLoadOlder(wrapper);
    await clickLoadOlder(wrapper);

    expect(getMessagesBefore).toHaveBeenNthCalledWith(2, 'T', 'Writer', 300, 200);
    expect(board.activityFor('T', 'Writer').map((m) => m.seq)).toEqual([500, 250]);

    // seq 100 is sinceSeq + 1: nothing older belongs to this container, so it stops asking.
    expect(wrapper.text()).toContain('The first message.');
    expect(getMessagesBefore).toHaveBeenCalledTimes(2);
  });
});
