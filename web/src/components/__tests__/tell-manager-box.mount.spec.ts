// @vitest-environment happy-dom
//
// THE TEAM PAGE'S OWN WAY TO GIVE A TEAM WORK: a box whose words go to the Manager as a person's
// instruction. It rides the same `tell` route a person's tell already takes, and continues a
// chosen workflow the way the Concierge's steering does - the workflow's correlation id as the
// instruction's causation. These assert the REQUEST, because a box that renders and sends the
// wrong body looks exactly like one that works.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

import TellManagerBox from '../TellManagerBox.vue';
import { asTeamId } from '../../api/types';
import type { TeamWorkflowTiming } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 1707,
    state: 'Running',
    startedAt: '2026-09-20T09:00:00Z',
    endedAt: null,
    serverNow: '2026-09-20T10:00:00Z',
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: null,
    awaitingFrom: null,
    subject: 'Build the login page',
    pausedAt: null,
    pausedReason: null,
    pausedLimit: null,
    ...over,
  };
}

let wrapper: VueWrapper | undefined;
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ seq: 2001, correlationId: 2001 }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  }));
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

async function mountBox(workflows: TeamWorkflowTiming[] = []) {
  wrapper = mount(TellManagerBox, { props: { team: asTeamId('alpha'), workflows } });
  await flushPromises();
  return wrapper;
}

async function type(box: VueWrapper, text: string) {
  await box.find('textarea[data-tell-manager-input]').setValue(text);
}

async function send(box: VueWrapper) {
  await box.find('[data-tell-manager-send]').trigger('click');
  await flushPromises();
}

/** The one request the box made: its path, its method and its parsed body. */
function sent() {
  expect(fetchMock).toHaveBeenCalledTimes(1);
  const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
  return { url, method: init.method, body: JSON.parse(String(init.body)) as Record<string, unknown> };
}

describe('Tell the Manager box', () => {
  it('has a place to type and says in plain words what happens next', async () => {
    const box = await mountBox();

    expect(box.find('textarea[data-tell-manager-input]').exists()).toBe(true);
    expect(box.find('[data-tell-manager-send]').text()).toBe('Tell the Manager');
    expect(box.find('[data-tell-manager-next]').text()).toBe(
      'The Manager plans the work and hands it to the team. The board shows it as it moves.');
  });

  it('sends the words to the Manager through the person\'s tell route, starting new work by default', async () => {
    const box = await mountBox([timing()]);

    await type(box, 'Add a sign-out button');
    await send(box);

    expect(sent()).toEqual({
      url: '/api/teams/alpha/containers/Manager/tell',
      method: 'POST',
      body: { instruction: 'Add a sign-out button' },
    });
  });

  it('continues a chosen workflow by passing its number as causation, as steering does', async () => {
    const box = await mountBox([timing({ correlation: 1707 })]);

    box.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 1707);
    await flushPromises();
    await type(box, 'Make the button red too');
    await send(box);

    expect(sent()).toEqual({
      url: '/api/teams/alpha/containers/Manager/tell',
      method: 'POST',
      body: { instruction: 'Make the button red too', causation: '1707' },
    });
  });

  it('offers only open workflows to continue, after new work', async () => {
    const box = await mountBox([
      timing({ correlation: 1707, subject: 'Build the login page' }),
      timing({ correlation: 1600, subject: 'Old job', state: 'Completed', endedAt: '2026-09-20T09:40:00Z' }),
    ]);

    const options = box.findComponent({ name: 'QSelect' }).props('options') as { label: string; value: number | null }[];
    expect(options).toEqual([
      { label: 'New work', value: null },
      { label: '#1707 Build the login page', value: 1707 },
    ]);
  });

  it('sends nothing for empty words', async () => {
    const box = await mountBox();

    await type(box, '   ');
    await send(box);

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('clears the box and says it was sent once the Manager has it', async () => {
    const box = await mountBox();

    await type(box, 'Add a sign-out button');
    await send(box);

    expect((box.find('textarea[data-tell-manager-input]').element as HTMLTextAreaElement).value).toBe('');
    expect(box.find('[data-tell-manager-sent]').text()).toBe('Sent. The Manager has it.');
  });

  it('shows the server\'s refusal and keeps the words', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ error: 'No member \'Manager\'.' }), {
      status: 404,
      headers: { 'content-type': 'application/json' },
    }));
    const box = await mountBox();

    await type(box, 'Add a sign-out button');
    await send(box);

    expect(box.find('[data-tell-manager-error]').text()).toContain('No member \'Manager\'.');
    expect((box.find('textarea[data-tell-manager-input]').element as HTMLTextAreaElement).value)
      .toBe('Add a sign-out button');
  });
});
