// @vitest-environment happy-dom
//
// A FEED ROW'S TIME sits under its chip, as the time of day alone - on another day too - so the
// summary keeps the row's width. The full date and time is the time's tooltip. The workflow number
// is the row's last column.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import type { Message } from '../../api/types';
import { localDate, localTime } from '../../lib/localTime';
import { dateTimeOf, timeOf } from '../../lib/summarise';

import { useConsoleStore } from '../../stores/console';
import ActivityFeed from '../ActivityFeed.vue';
import '../../test/mountQuasar';

// The row is from 29 September; the board is read on 3 November, so the row is from another day.
const occurredAt = '2026-09-29T10:00:07Z';
const at = new Date(occurredAt).getTime();
const now = new Date('2026-11-03T15:00:00Z');

const row: Message = {
  seq: 7,
  type: 'agentContainer.completed',
  payload: JSON.stringify({ exitCode: 0, output: 'GO' }),
  source: 'Mail/Echo',
  correlationId: 40,
  causationSeq: 6,
  depth: 1,
  occurredAt,
};

let wrapper: VueWrapper | null = null;

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(now);
});
afterEach(() => {
  vi.useRealTimers();
  wrapper?.unmount();
  wrapper = null;
  document.body.innerHTML = '';
});

describe('ActivityFeed row time', () => {
  it('shows the time of day alone under the chip, even for a row from another day', () => {
    wrapper = mount(ActivityFeed, { props: { feed: [row], team: 'Mail', name: 'Echo', sinceSeq: 0 } });

    // Another day: the old column (timeOf, localStamp) would have shown the date here.
    expect(timeOf(occurredAt)).toContain(localDate(at));

    const lead = wrapper.find('.feed-lead');
    expect(lead.find('[data-feed-chip]').exists()).toBe(true);
    const time = lead.find('[data-feed-time]').text();
    expect(time).toBe(localTime(at));
    expect(time).toMatch(/07/);
    expect(time).not.toContain(localDate(at));
    // The row's aria-label still says the row's stamp, date and all.
    expect(wrapper.find('.q-item').attributes('aria-label')).toContain(timeOf(occurredAt));
  });

  it('gives the full date and time as the tooltip of the time', async () => {
    wrapper = mount(ActivityFeed, { props: { feed: [row] }, attachTo: document.body });

    const tooltip = wrapper.find('[data-feed-time]').findComponent({ name: 'QTooltip' });
    (tooltip.vm as unknown as { show: () => void }).show();
    await flushPromises();

    expect(document.body.querySelector('[data-feed-time-full]')?.textContent).toBe(dateTimeOf(occurredAt));
  });

  it('keeps the workflow number as the last column', () => {
    wrapper = mount(ActivityFeed, { props: { feed: [row] } });

    const sections = wrapper.find('.q-item').findAll('.q-item__section');
    expect(sections[sections.length - 1]!.find('[data-feed-workflow]').exists()).toBe(true);
    expect(sections[sections.length - 1]!.find('[data-feed-workflow]').text()).toBe('#40');
    expect(wrapper.find('[data-feed-time]').exists()).toBe(true);
    expect(sections[sections.length - 1]!.find('[data-feed-time]').exists()).toBe(false);
  });

  it('still follows the workflow from the last column, without opening the row', async () => {
    wrapper = mount(ActivityFeed, { props: { feed: [row] }, attachTo: document.body });
    const board = useConsoleStore();

    await wrapper.find('[data-feed-workflow]').trigger('click');
    await flushPromises();

    expect(board.highlightedWorkflow).toBe(40);
    expect(document.body.querySelector('[data-feed-card-time]')).toBeNull();
  });

  it('keeps the summary to two lines', () => {
    wrapper = mount(ActivityFeed, { props: { feed: [row] } });

    const summary = wrapper.findAllComponents({ name: 'QItemLabel' }).find((label) => label.classes('feed-line'));
    expect(summary?.props('lines')).toBe('2');
  });
});
