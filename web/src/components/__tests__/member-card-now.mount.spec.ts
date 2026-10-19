// @vitest-environment happy-dom
//
// THE MEMBER CARD SAYS WHAT IT IS DOING without reading the feed: a "now" line under the header -
// while running, the member's newest progress text; while idle, the first line of its last outcome -
// read from the rows the card already holds, never a second stream. And the member's name opens
// what the eye opens: the live view, or earlier runs.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { listContainerTriggers } = vi.hoisted(() => ({ listContainerTriggers: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
}));

import ContainerCard from '../ContainerCard.vue';
import LiveViewDialog from '../LiveViewDialog.vue';
import { asMemberId, asTeamId, type ContainerSnapshot, type Message } from '../../api/types';
import { memberNow } from '../../lib/memberNow';
import { resetBody } from '../../test/mountQuasar';

const snapshot = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('DeveloperIda'),
  name: 'Developer Ida',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  watchable: true,
  ...over,
});

let seq = 100;
/** A row of this member's, newest first as the board holds them. */
function row(type: string, payload: Record<string, unknown>): Message {
  seq -= 1;
  return {
    seq, type, source: 'alpha/DeveloperIda', payload: JSON.stringify(payload),
    correlationId: 7, causationSeq: null, depth: 0, occurredAt: '2026-10-19T10:00:00Z',
  };
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  setActivePinia(createPinia());
  seq = 100;
  listContainerTriggers.mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

async function mountCard(over: Partial<ContainerSnapshot>, feed: Message[]) {
  wrapper = mount(ContainerCard, { props: { snapshot: snapshot(over), feed }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}

const nowLine = (card: VueWrapper) => card.find('[data-member-now]');

describe('the member card\'s now line', () => {
  it('shows the newest progress text while the member is running', async () => {
    const card = await mountCard({ state: 'Running' }, [
      row('agentContainer.progress', { status: 'Running the web suite' }),
      row('agentContainer.progress', { status: 'Reading the spec' }),
      row('agentContainer.started', {}),
      row('agentContainer.completed', { output: 'Earlier run done' }),
    ]);

    expect(nowLine(card).text()).toBe('Running the web suite');
    expect(nowLine(card).attributes('data-member-now')).toBe('progress');
  });

  it('shows nothing while running before the run in flight has reported, rather than an earlier run\'s words', async () => {
    const card = await mountCard({ state: 'Running' }, [
      row('agentContainer.started', {}),
      row('agentContainer.progress', { status: 'An earlier run\'s step' }),
    ]);

    expect(nowLine(card).exists()).toBe(false);
  });

  it('shows the first line of the last outcome while idle', async () => {
    const card = await mountCard({ state: 'Idle' }, [
      row('wake.asked', { note: 'later' }),
      row('agentContainer.completed', { output: '\nPushed the branch and handed back.\n\nDetails follow.' }),
      row('agentContainer.progress', { status: 'Pushing' }),
      row('agentContainer.failed', { output: 'An older failure' }),
    ]);

    expect(nowLine(card).text()).toBe('Pushed the branch and handed back.');
    expect(nowLine(card).attributes('data-member-now')).toBe('outcome');
    expect(nowLine(card).attributes('title')).toBe('Pushed the branch and handed back.');
  });

  it('reads a failure or a block as the last outcome too, and nothing when there is none', async () => {
    expect(memberNow([row('agentContainer.failed', { launchError: 'agent not found', output: '' })], false))
      .toEqual({ kind: 'outcome', text: 'agent not found' });
    expect(memberNow([row('agentContainer.blocked', { reason: 'Waiting on access' })], false))
      .toEqual({ kind: 'outcome', text: 'Waiting on access' });
    expect(memberNow([row('agentContainer.progress', { status: 'mid-way' })], false)).toBeNull();

    const card = await mountCard({ state: 'Idle' }, []);
    expect(nowLine(card).exists()).toBe(false);
  });

  it('is text, never markup', async () => {
    const card = await mountCard({ state: 'Running' }, [row('agentContainer.progress', { status: '<b>bold</b>' })]);

    expect(nowLine(card).text()).toBe('<b>bold</b>');
    expect(nowLine(card).find('b').exists()).toBe(false);
  });
});

describe('the member card\'s name', () => {
  it('opens the live view, as the eye does', async () => {
    const card = await mountCard({ state: 'Running' }, []);

    const name = card.find('[data-member-name]');
    expect(name.text()).toBe('Developer Ida');
    expect(name.attributes('title')).toBe('Watch this run and earlier ones');
    await name.trigger('click');

    const live = card.findComponent(LiveViewDialog);
    expect(live.props('modelValue')).toBe(true);
    expect(live.props('member')).toBe('DeveloperIda');
  });

  it('opens earlier runs for an idle member and for a plugin', async () => {
    const idle = await mountCard({ state: 'Idle' }, []);
    expect(idle.find('[data-member-name]').attributes('title')).toBe('Earlier runs');
    wrapper!.unmount();

    const plugin = await mountCard({ kind: 'plugin', agent: 'plugin:mailer', watchable: false }, []);
    await plugin.find('[data-member-name]').trigger('click');
    expect(plugin.findComponent(LiveViewDialog).props('modelValue')).toBe(true);
  });

  it('is plain text for a member with nothing to watch', async () => {
    const card = await mountCard({ watchable: false }, []);

    expect(card.find('[data-member-name]').exists()).toBe(false);
    expect(card.text()).toContain('Developer Ida');
  });
});
