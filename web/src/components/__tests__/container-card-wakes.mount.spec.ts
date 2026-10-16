// @vitest-environment happy-dom
//
// A MEMBER THAT ASKED TO BE WOKEN SAYS SO ON ITS CARD: "wakes at 9:41 PM: <note>", the time on the
// reader's clock (the Host sends UTC), with a person's Cancel. Cancel calls the person's route under
// the member's own team, and the line goes; a refusal (it fired a moment before) is said under it.
//
// The browser is in New York, on summer time (UTC-4) on these dates.
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { listContainerTriggers, cancelWake } = vi.hoisted(() => ({
  listContainerTriggers: vi.fn(),
  cancelWake: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
  cancelWake,
}));

import ContainerCard from '../ContainerCard.vue';
import { asMemberId, asTeamId, type ContainerSnapshot, type PendingWake } from '../../api/types';
import { localMinute } from '../../lib/localTime';
import { resetBody } from '../../test/mountQuasar';

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

const snapshot = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('DeveloperIda'),
  name: 'Developer Ida',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: 4100,
  sinceSeq: 0,
  ...over,
});

let zone: string | undefined;

beforeAll(() => {
  zone = process.env.TZ;
  process.env.TZ = 'America/New_York';
});

afterAll(() => {
  if (zone === undefined) delete process.env.TZ;
  else process.env.TZ = zone;
});

beforeEach(() => {
  setActivePinia(createPinia());
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(Now));
  listContainerTriggers.mockResolvedValue([]);
  cancelWake.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

async function mountCard(snap: ContainerSnapshot) {
  const wrapper = mount(ContainerCard, {
    props: { snapshot: snap, feed: [] },
    global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true, LiveViewDialog: true } },
  });
  await flushPromises();
  return wrapper;
}

describe('the member card with a pending wake', () => {
  it('says when it wakes on the reader\'s clock, with the note', async () => {
    const wrapper = await mountCard(snapshot({ wakes: [wake()] }));
    const line = wrapper.get('[data-test="member-wakes"]').text();

    expect(line).toContain(`wakes at ${localMinute(Date.parse(Due))}: check whether the certificate was issued`);
    // The reader's clock, not the wire's: 9:41 PM in New York, never the UTC 1:41.
    expect(line).toContain('9:41');
    expect(line).not.toContain('1:41 AM');
  });

  it('puts the date in front when the wake is not today', async () => {
    const wrapper = await mountCard(snapshot({ wakes: [wake({ dueAt: '2026-10-16T14:00:00Z' })] }));

    expect(wrapper.get('[data-test="member-wakes"]').text()).toContain(
      `wakes at ${localMinute(Date.parse('2026-10-16T14:00:00Z'), { date: true })}`,
    );
  });

  it('offers a person Cancel, which cancels that wake on the member\'s team and takes the line down', async () => {
    cancelWake.mockResolvedValue({ wake: wake(), cancelled: true });
    const wrapper = await mountCard(snapshot({ wakes: [wake()] }));

    const cancel = wrapper.get('[data-action="cancel-wake"]');
    expect(cancel.text()).toContain('Cancel');
    await cancel.trigger('click');
    await flushPromises();

    expect(cancelWake).toHaveBeenCalledWith('alpha', 7);
    expect(wrapper.find('[data-test="member-wakes"]').exists()).toBe(false);
  });

  it('says the Host\'s refusal and keeps the line when the cancel is refused', async () => {
    cancelWake.mockRejectedValue(new Error('No pending wake 7 on this team. It has fired or been cancelled.'));
    const wrapper = await mountCard(snapshot({ wakes: [wake()] }));

    await wrapper.get('[data-action="cancel-wake"]').trigger('click');
    await flushPromises();

    expect(wrapper.get('[data-test="wake-error"]').text()).toContain('It has fired or been cancelled.');
    expect(wrapper.find('[data-test="member-wakes"]').exists()).toBe(true);
  });

  it('shows one line per wake, earliest first, as the snapshot orders them', async () => {
    const wrapper = await mountCard(
      snapshot({ wakes: [wake(), wake({ id: 8, workflow: 4200, dueAt: '2026-10-16T01:55:00Z', note: 'look at the deploy' })] }),
    );

    const lines = wrapper.findAll('.wake-line').map((found) => found.text());
    expect(lines).toHaveLength(2);
    expect(lines[0]).toContain('certificate');
    expect(lines[1]).toContain('look at the deploy');
  });

  it('shows nothing for a member with no wake, as an older Host sends it', async () => {
    const wrapper = await mountCard(snapshot());

    expect(wrapper.find('[data-test="member-wakes"]').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('wakes at');
  });
});
