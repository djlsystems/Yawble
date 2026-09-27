// @vitest-environment happy-dom
//
// A PLUGIN MEMBER'S CARD. A plugin runs no model: no tokens, no spend, no live view. Its card shows
// no eye, names the plugin rather than an Agent, says a failure without provider or spend words, and
// opens its earlier runs - each with what it reported - from a history button (E5).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { listContainerTriggers } = vi.hoisted(() => ({ listContainerTriggers: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
}));

import ContainerCard from '../ContainerCard.vue';
import { asMemberId, asTeamId, type ContainerSnapshot, type MemberRunsPage } from '../../api/types';
import { bodyText, resetBody } from '../../test/mountQuasar';

const plugin = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('Echo'),
  name: 'Echo',
  agent: 'plugin:sample-echo',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...over,
});

/** `GET .../runs` for a plugin member, as the route answers it: each run with its output. */
const runs: MemberRunsPage = {
  runs: [
    {
      seq: 42, workflow: 40, startedAt: '2026-09-27T10:00:00Z', endedAt: '2026-09-27T10:00:02Z',
      durationMs: 2000, outcome: 'completed', output: 'olleh',
    },
    {
      // Every item blocked: no completed row, so the run is its last `blocked` row, with its reason.
      seq: 36, workflow: 35, startedAt: '2026-09-27T09:30:00Z', endedAt: '2026-09-27T09:30:01Z',
      durationMs: 1000, outcome: 'blocked', output: null, reason: 'no creds',
    },
    {
      seq: 30, workflow: 29, startedAt: '2026-09-27T09:00:00Z', endedAt: '2026-09-27T09:00:01Z',
      durationMs: 1000, outcome: 'failed', output: null,
    },
  ],
  nextBefore: null,
};

let fetch: ReturnType<typeof vi.fn>;

beforeEach(() => {
  setActivePinia(createPinia());
  listContainerTriggers.mockResolvedValue([]);
  fetch = vi.fn((input: string) => {
    const url = new URL(input, 'http://host');
    if (url.pathname.endsWith('/runs')) {
      return Promise.resolve(new Response(JSON.stringify(runs), { status: 200, headers: { 'content-type': 'application/json' } }));
    }
    return new Promise<Response>(() => {});
  });
  vi.stubGlobal('fetch', fetch);
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function mountCard(snap: ContainerSnapshot) {
  const wrapper = mount(ContainerCard, {
    attachTo: document.body,
    props: { snapshot: snap, feed: [] },
    global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true } },
  });
  await flushPromises();
  return wrapper;
}

const eye = '[aria-label="Watch this member"]';
const history = '[aria-label="Earlier runs"]';

describe('the member card for a plugin', () => {
  it('shows no live view, even when the snapshot says watchable, running or idle', async () => {
    for (const over of [{}, { watchable: true }, { state: 'Running' as const, watchable: true }]) {
      const wrapper = await mountCard(plugin(over));

      expect(wrapper.find(eye).exists(), JSON.stringify(over)).toBe(false);
      wrapper.unmount();
    }
  });

  it('shows no tokens or spend, and names the plugin rather than an Agent', async () => {
    const wrapper = await mountCard(plugin());
    const text = wrapper.text();

    expect(text).not.toMatch(/token/i);
    expect(text).not.toMatch(/spend/i);
    expect(text).toMatch(/plugin\s+sample-echo/);
    expect(text).not.toMatch(/agent\s+plugin:/);
  });

  it('says a failure without provider or spend words', async () => {
    for (const failureClass of ['agent-fault', 'quota', 'rate', 'unknown']) {
      const wrapper = await mountCard(plugin({ failed: 'asked to fail: nope', failureClass }));
      const text = wrapper.text();

      expect(text, failureClass).toContain('run failed');
      expect(text, failureClass).toContain('asked to fail: nope');
      expect(text, failureClass).not.toMatch(/provider|spend|agent.fault/i);
      wrapper.unmount();
    }
  });

  it('keeps the provider and spend words for an agent member', async () => {
    const wrapper = await mountCard(plugin({ kind: 'agent', agent: 'claude-headless', failed: 'The agent exited 1.', failureClass: 'agent-fault' }));

    expect(wrapper.text()).toContain('would spend again');
  });

  it('opens its earlier runs, each with what it reported, and never asks for a live view', async () => {
    const wrapper = await mountCard(plugin());

    await wrapper.find(history).trigger('click');
    await flushPromises();

    expect(bodyText()).toContain('Runs of Echo');
    expect(bodyText()).not.toContain('Not running');
    expect(document.body.querySelectorAll('.earlier-run')).toHaveLength(3);

    (document.body.querySelector('.earlier-run[data-seq="42"]') as HTMLElement).click();
    await flushPromises();

    expect(document.body.querySelector('.run-output')?.textContent).toBe('olleh');

    const asked = fetch.mock.calls.map(([input]) => String(input));
    expect(asked).toContain('/api/teams/alpha/members/Echo/runs');
    expect(asked.some((path) => path.endsWith('/live') || path.includes('/transcript'))).toBe(false);
  });

  it('says so when a run reported nothing', async () => {
    const wrapper = await mountCard(plugin());

    await wrapper.find(history).trigger('click');
    await flushPromises();
    (document.body.querySelector('.earlier-run[data-seq="30"]') as HTMLElement).click();
    await flushPromises();

    expect(bodyText()).toContain('This run reported no output.');
  });

  it('lists a run that ended blocked, and opens it onto its reason', async () => {
    const wrapper = await mountCard(plugin());

    await wrapper.find(history).trigger('click');
    await flushPromises();

    const row = document.body.querySelector('.earlier-run[data-seq="36"]') as HTMLElement;
    expect(row.querySelector('.earlier-run-outcome')?.getAttribute('data-outcome')).toBe('blocked');

    row.click();
    await flushPromises();

    expect(document.body.querySelector('.run-reason')?.textContent).toBe('Blocked: no creds');
    expect(bodyText()).not.toContain('This run reported no output.');
  });

  it('offers no history button on an agent member', async () => {
    const wrapper = await mountCard(plugin({ kind: 'agent', agent: 'claude-headless', watchable: true }));

    expect(wrapper.find(history).exists()).toBe(false);
    expect(wrapper.find(eye).exists()).toBe(true);
  });
});
