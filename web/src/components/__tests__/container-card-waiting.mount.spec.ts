// @vitest-environment happy-dom
//
// A member whose claim on a WIP slot is waiting says so on its card - "waiting for
// a slot" - rather than reading idle. Either the snapshot's `held` or the ledger naming it is enough.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { listContainerTriggers } = vi.hoisted(() => ({ listContainerTriggers: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
}));

import ContainerCard from '../ContainerCard.vue';
import { useWipStore } from '../../stores/wip';
import { asMemberId, asTeamId, type ContainerSnapshot } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

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

beforeEach(() => {
  setActivePinia(createPinia());
  listContainerTriggers.mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function mountCard(snap: ContainerSnapshot) {
  const wrapper = mount(ContainerCard, {
    props: { snapshot: snap, feed: [] },
    global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true } },
  });
  await flushPromises();
  return wrapper;
}

describe('the member card while held', () => {
  it('reads waiting for a slot when the snapshot is held', async () => {
    const wrapper = await mountCard(snapshot({ held: true }));

    expect(wrapper.text()).toContain('waiting for a slot');
    expect(wrapper.text()).not.toMatch(/\bidle\b/);
  });

  it('reads waiting for a slot when the ledger names it waiting', async () => {
    const wip = useWipStore();
    wip.view = { max: 2, running: [], waiting: [{ team: 'alpha', member: 'Manager', since: '' }] };

    const wrapper = await mountCard(snapshot());

    expect(wrapper.text()).toContain('waiting for a slot');
  });

  it('reads idle when nothing holds it', async () => {
    const wrapper = await mountCard(snapshot());

    expect(wrapper.text()).toContain('idle');
    expect(wrapper.text()).not.toContain('waiting for a slot');
  });
});

describe('the Watch button', () => {
  const eye = '[aria-label="Watch this member"]';

  it('is present on an idle member whose snapshot is watchable', async () => {
    const wrapper = await mountCard(snapshot({ watchable: true }));

    expect(wrapper.find(eye).exists()).toBe(true);
  });

  it('is present on a running member whose snapshot is watchable', async () => {
    const wrapper = await mountCard(snapshot({ state: 'Running', watchable: true }));

    expect(wrapper.find(eye).exists()).toBe(true);
  });

  it('is absent on a member that is not watchable, running or idle', async () => {
    for (const over of [{ watchable: false }, { state: 'Running' as const, watchable: false }, {}]) {
      const wrapper = await mountCard(snapshot(over));

      expect(wrapper.find(eye).exists(), JSON.stringify(over)).toBe(false);
      wrapper.unmount();
    }
  });
});
