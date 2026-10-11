// @vitest-environment happy-dom
//
// WHERE A TEAM'S ACTIVITY VIEW BEGINS, set from the Activity dialog: "Start activity from now" asks
// first and sends `now`; with it set, the dialog says from when and by whom, and Show all history
// sends `null`. Each change reads the window again, in the dialog and on the tile it opened from.
// Run it in more than one zone: the time here is expected in the browser's own locale and zone.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

import TeamStatisticsTile from '../TeamStatisticsTile.vue';
import { asTeamId, type ActivityFrom, type TeamActivity } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');
const iso = (minutes: number) => new Date(Date.UTC(2026, 9, 7, 14, minutes)).toISOString();

const container = (id: string) => ({
  team: teamId, id, name: id, agent: 'claude-headless', state: 'Idle', queueDepth: 0, ceiling: 16,
  subscribes: [], currentCorrelation: null, sinceSeq: 0,
});

const members = [container('Manager'), container('Developer')];

/** Where the Host's setting stands, as the route keeps it. */
let stored: ActivityFrom | null;

/** What a `now` sets: the Host's clock, and the person who asked. */
const setNow: ActivityFrom = { at: iso(20), by: 'ines@example.com' };

/** The Host's answer: its window starts at the floor while one is set. */
function activity(): TeamActivity {
  const from = stored ? stored.at : iso(0);

  return {
    from,
    to: iso(30),
    serverNow: iso(30),
    window: 'workflows',
    members: [
      { member: 'Manager', kind: 'agent', isManager: true, current: true, spans: [{ state: 'running', from, to: iso(30), workflow: 7 }] },
    ],
    ...(stored ? { activityFrom: stored } : {}),
  } as TeamActivity;
}

let fetchMock: ReturnType<typeof vi.fn>;
let refuse: string | null;
let wrapper: VueWrapper | undefined;

const calls = () => fetchMock.mock.calls.map(([url, init]) => ({ url: String(url), init: init as RequestInit | undefined }));
const activityReads = () => calls().filter((c) => c.url === `/api/teams/${teamId}/activity`).length;
const puts = () => calls().filter((c) => c.init?.method === 'PUT');

beforeEach(() => {
  stored = null;
  refuse = null;
  fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    if (String(url) === `/api/teams/${teamId}/activity-from` && init?.method === 'PUT') {
      if (refuse) return new Response(JSON.stringify({ error: refuse }), { status: 500, headers: { 'Content-Type': 'application/json' } });

      const { from } = JSON.parse(String(init.body)) as { from: 'now' | null };
      stored = from === 'now' ? setNow : null;

      return new Response(JSON.stringify({ activityFrom: stored }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }

    if (String(url) === `/api/teams/${teamId}/activity`) {
      return new Response(JSON.stringify(activity()), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }

    return new Response('{}', { status: 404 });
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

async function settle() {
  await flushPromises();
  await flushPromises();
}

/** The compact tile, as on the Teams list, with its dialog opened by a click. */
async function openDialog() {
  setActivePinia(createPinia());
  wrapper = mount(TeamStatisticsTile, {
    props: { teamId, containers: members as never, workflows: null, compact: true },
    attachTo: document.body,
  });
  await settle();
  await wrapper.trigger('click');
  await settle();

  return wrapper;
}

const dialog = () => document.querySelector<HTMLElement>('.stats-dialog-card')!;
const find = (selector: string) => dialog().querySelector<HTMLElement>(selector);

async function click(selector: string) {
  const el = find(selector);
  if (!el) throw new Error(`nothing matches ${selector}`);
  el.click();
  await settle();
}

describe('Start activity from now', () => {
  it('asks first in one sentence, and sends nothing until confirmed', async () => {
    await openDialog();

    expect(find('[data-activity-from-confirm]')).toBeNull();

    await click('[data-activity-from-start]');

    expect(puts()).toHaveLength(0);
    expect(find('[data-activity-from-confirm]')!.querySelector('p')!.textContent!.replace(/\s+/g, ' ').trim()).toBe(
      'The chart and the token chart will begin now; nothing is deleted, spend, caps and outcomes are unchanged, '
      + 'and Show all history brings it back.');
  });

  it('sends now on confirm, then reads the window again in the dialog and on the tile', async () => {
    await openDialog();
    const before = activityReads();

    await click('[data-activity-from-start]');
    await click('[data-activity-from-confirm-yes]');

    expect(puts().map((c) => [c.url, JSON.parse(String(c.init!.body))])).toEqual([
      [`/api/teams/${teamId}/activity-from`, { from: 'now' }],
    ]);
    expect(activityReads() - before).toBe(2);
    expect(find('[data-activity-from-confirm]')).toBeNull();
    expect(wrapper!.emitted('own-window')!.at(-1)).toEqual([{ from: Date.parse(iso(20)), to: Date.parse(iso(30)) }]);
  });

  it('sends nothing on Cancel', async () => {
    await openDialog();

    await click('[data-activity-from-start]');
    [...find('[data-activity-from-confirm]')!.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Cancel')!.click();
    await settle();

    expect(puts()).toHaveLength(0);
    expect(find('[data-activity-from-confirm]')).toBeNull();
  });

  it('shows the Host\'s sentence when it refuses, and nothing changes', async () => {
    refuse = 'The activity start could not be saved.';
    await openDialog();

    await click('[data-activity-from-start]');
    await click('[data-activity-from-confirm-yes]');

    expect(find('[data-activity-from-error]')!.textContent).toBe('The activity start could not be saved.');
    expect(find('[data-activity-from]')).toBeNull();
  });
});

describe('with it set', () => {
  it('says from when, on the reader\'s clock, and by whom', async () => {
    stored = setNow;
    await openDialog();

    expect(find('[data-activity-from]')!.textContent).toBe(
      `Activity shown from ${new Date(Date.parse(iso(20))).toLocaleString()}, set by ines@example.com`);
  });

  it('is said once set from the dialog', async () => {
    await openDialog();

    expect(find('[data-activity-from]')).toBeNull();

    await click('[data-activity-from-start]');
    await click('[data-activity-from-confirm-yes]');

    expect(find('[data-activity-from]')!.textContent).toContain('set by ines@example.com');
  });

  it('Show all history sends null and reads the whole window again', async () => {
    stored = setNow;
    await openDialog();
    const before = activityReads();

    await click('[data-activity-from-clear]');

    expect(puts().map((c) => [c.url, JSON.parse(String(c.init!.body))])).toEqual([
      [`/api/teams/${teamId}/activity-from`, { from: null }],
    ]);
    expect(activityReads() - before).toBe(2);
    expect(find('[data-activity-from]')).toBeNull();
    expect(find('[data-activity-from-clear]')).toBeNull();
    expect(wrapper!.emitted('own-window')!.at(-1)).toEqual([{ from: Date.parse(iso(0)), to: Date.parse(iso(30)) }]);
  });
});
