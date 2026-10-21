// @vitest-environment happy-dom
//
// AN OPENED CARD WHOSE WORKFLOW HAS A PENDING WAKE SAYS SO, as the card on the board does: "wakes at
// 9:41 PM: <note>" on the reader's clock (the Host sends UTC), with a person's Cancel. Cancel calls the
// person's route under the card's own team and the line goes; a refusal is said under it.
//
// The browser is in New York, on summer time (UTC-4) on these dates.
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { cancelWake } = vi.hoisted(() => ({ cancelWake: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  cancelWake,
}));

import KanbanCardPanel from '../KanbanCardPanel.vue';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCardDetail } from '../../api/kanban';
import type { PendingWake } from '../../api/types';
import { localMinute } from '../../lib/localTime';
import { avatarIndex, setAvatarSheet } from '../../lib/avatars';

/** 9:41 PM in New York on the 15th; 01:41 on the 16th in UTC. */
const Due = '2026-10-16T01:41:00Z';
const Now = '2026-10-16T01:30:00Z';

const wake = (over: Partial<PendingWake> = {}): PendingWake => ({
  id: 7,
  member: 'DeveloperIda',
  workflow: 4100,
  dueAt: Due,
  askedAt: '2026-10-16T01:31:00Z',
  note: 'check whether the certificate was issued',
  ...over,
});

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

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(Now));
  cancelWake.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  wrapper?.unmount();
  wrapper = null;
  resetBody();
});

async function open(wakes: PendingWake[] | undefined) {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  // The refetch after a write is the store's own business; nothing here is about it.
  kanban.load = vi.fn(async () => {});
  kanban.select = vi.fn(async () => {});
  wrapper = mount(KanbanCardPanel);
  const detail: KanbanCardDetail = {
    id: 'c-1',
    workflowSeq: 4100,
    team: 'alpha',
    member: 'DeveloperIda',
    title: 'Renew the certificate',
    body: '',
    status: 'running',
    laneId: 'in-progress',
    progress: [],
    createdAt: '2026-10-16T01:00:00Z',
    updatedAt: '2026-10-16T01:10:00Z',
    awaitingManager: false,
    paused: false,
    trail: [],
    ...(wakes ? { wakes } : {}),
  };
  kanban.selectedId = detail.id;
  kanban.detail = detail;
  await flushPromises();
}

const line = () => bodyFind('[data-test="panel-wakes"]');

describe('the opened card\'s title bar', () => {
  it('wears its member\'s avatar before the title', async () => {
    setAvatarSheet(null, 1024, 1024);
    try {
      await open(undefined);
      const avatar = bodyFind('.k-panel-head [data-test="member-avatar"]');
      expect(avatar?.getAttribute('data-avatar')).toBe(String(avatarIndex('alpha', 'DeveloperIda', 100)));
      expect(avatar?.nextElementSibling?.querySelector('.text-subtitle1')?.textContent).toBe('Renew the certificate');
    } finally {
      setAvatarSheet(null, 0, 0);
    }
  });
});

describe('an opened card with a pending wake', () => {
  it('says when it wakes on the reader\'s clock, with the note and Cancel', async () => {
    await open([wake()]);

    const text = line()?.textContent ?? '';
    expect(text).toContain(`wakes at ${localMinute(Date.parse(Due))}: check whether the certificate was issued`);
    expect(text).toContain('9:41');
    expect(text).not.toContain('1:41 AM');
    expect(bodyFind('[data-action="cancel-wake"]')?.textContent).toContain('Cancel');
  });

  it('shows nothing when the workflow has no pending wake', async () => {
    await open(undefined);

    expect(bodyFind('.k-panel')).not.toBeNull();
    expect(line()).toBeNull();
  });

  it('cancels that wake on the card\'s team, and the line goes', async () => {
    cancelWake.mockResolvedValue({ wake: wake(), cancelled: true });
    await open([wake()]);

    bodyFind('[data-action="cancel-wake"]')!.click();
    await flushPromises();

    expect(cancelWake).toHaveBeenCalledWith('alpha', 7);
    expect(line()).toBeNull();
  });

  it('says the Host\'s refusal and keeps the line when the cancel is refused', async () => {
    cancelWake.mockRejectedValue(new Error('No pending wake 7 on this team. It has fired or been cancelled.'));
    await open([wake()]);

    bodyFind('[data-action="cancel-wake"]')!.click();
    await flushPromises();

    expect(bodyFind('[data-test="panel-wake-error"]')?.textContent).toContain('It has fired or been cancelled.');
    expect(line()).not.toBeNull();
  });
});
