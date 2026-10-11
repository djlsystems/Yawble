// @vitest-environment happy-dom
//
// ONE CLOCK ON AN OPENED CARD: its Created and last-moved times read in the browser's zone, as its
// trail's rows do, so the same moment says the same time in both places. The Host sends every time as
// a UTC instant; one that came without its Z (as cards were sent before) is still read as UTC, never
// as the browser's local time.
//
// The browser is in New York, on summer time (UTC-4) on these dates.
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

import KanbanCardPanel from '../KanbanCardPanel.vue';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCardDetail } from '../../api/kanban';
import { localTime } from '../../lib/localTime';

const Told = '2026-10-08T14:44:11Z';
const Moved = '2026-10-08T15:02:30Z';
/** "10/8/2026, 10:44:11 AM" on a New York en-US browser. */
const local = (iso: string) => localTime(Date.parse(iso), { date: true });

let zone: string | undefined;
let wrapper: VueWrapper | null = null;

beforeAll(() => {
  zone = process.env.TZ;
  process.env.TZ = 'America/New_York';
});

afterAll(() => {
  if (zone === undefined) delete process.env.TZ;
  else process.env.TZ = zone;
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  resetBody();
});

async function open(createdAt: string, updatedAt: string) {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  wrapper = mount(KanbanCardPanel);
  const detail: KanbanCardDetail = {
    id: 'c-1',
    workflowSeq: 10670,
    team: 'alpha',
    member: 'Manager',
    title: 'Read the openings',
    body: '',
    status: 'running',
    laneId: 'in-progress',
    progress: [],
    createdAt,
    updatedAt,
    awaitingManager: false,
    paused: false,
    trail: [{ seq: 10670, type: 'agentContainer.tell', occurredAt: '2026-10-08T14:44:11+00:00', text: 'Read the openings' }],
  };
  kanban.selectedId = detail.id;
  kanban.detail = detail;
  await flushPromises();
}

const created = () => bodyFind('[data-card-times]')?.textContent?.replace(/\s+/g, ' ').trim();
const trailWhen = () => bodyFind('.k-trail-when')?.textContent;

describe('an opened card: its times', () => {
  it('says Created in the reader\'s clock, the same as its trail row for that moment', async () => {
    await open(Told, Moved);

    expect(trailWhen()).toBe(local(Told));
    expect(created()).toBe(`None reported. Created ${local(Told)}, last moved ${local(Moved)}.`);
  });

  it('reads a time sent with no offset as UTC, not as the browser\'s local time', async () => {
    await open('2026-10-08T14:44:11', '2026-10-08T15:02:30.1234567');

    expect(created()).toBe(`None reported. Created ${local(Told)}, last moved ${local('2026-10-08T15:02:30.123Z')}.`);
    expect(created()).toContain(trailWhen()!);
  });
});
