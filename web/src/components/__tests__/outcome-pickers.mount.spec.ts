// @vitest-environment happy-dom
//
// THE OUTCOME PICKERS WHERE WORK STARTS, MOUNTED: the backlog item editor and a trigger. Each offers
// None and every active and proposed outcome, and what is asserted is what reaches the route - the
// backlog PATCH, and the trigger's create request and update patch built from the dialog's draft.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { backlogItems, backlogItem, listCatalog, fileSystemRoots, updateBacklogItem, listLiveOutcomes } = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  updateBacklogItem: vi.fn(),
  listLiveOutcomes: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  backlogItems,
  backlogItem,
  listCatalog,
  fileSystemRoots,
  updateBacklogItem,
}));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listLiveOutcomes,
}));

import BacklogDialog from '../BacklogDialog.vue';
import TriggerEditDialog from '../TriggerEditDialog.vue';
import OutcomePicker from '../OutcomePicker.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { asTeamId, type TeamTrigger } from '../../api/types';
import { createTriggerRequestFromDraft, updateTriggerPatchFromDraft, type TriggerDraft } from '../../lib/triggers';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import * as probe from '../../test/formProbe';

const shipping = { id: '11111111-1111-1111-1111-111111111111', name: 'Ship the release', status: 'active' as const };
const faster = { id: '22222222-2222-2222-2222-222222222222', name: 'Faster onboarding', status: 'proposed' as const };
const oldGoal = { id: '33333333-3333-3333-3333-333333333333', name: 'Old goal', status: 'retired' as const };
const duplicate = { id: '44444444-4444-4444-4444-444444444444', name: 'Duplicate goal', status: 'merged' as const };

/** `GET /api/outcomes/{id}` for an outcome no picker lists, recording each read. */
function answerEndedOutcomes() {
  const read: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    read.push(url);
    const found = [oldGoal, duplicate].find((o) => url === `/api/outcomes/${o.id}`);
    if (!found) return new Response('{}', { status: 404 });
    return new Response(JSON.stringify({ outcome: found }), { status: 200, headers: { 'content-type': 'application/json' } });
  }));
  return read;
}

function item(over: Record<string, unknown> & { id: number }) {
  return {
    team: null,
    teamName: null,
    teamGone: false,
    title: `item ${over.id}`,
    body: '',
    state: 'pending',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: null,
    outcomeId: null,
    ...over,
  };
}

beforeEach(() => {
  listLiveOutcomes.mockReset();
  listLiveOutcomes.mockResolvedValue([shipping, faster]);
  updateBacklogItem.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }] });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const optionsOf = (picker: { props: (name: string) => unknown }) =>
  picker.props('options') as { label: string; value: string }[];

describe('the backlog item editor', () => {
  async function openTheItem(outcomeId: string | null) {
    setActivePinia(createPinia());
    useConsoleStore().teams = [{ id: 'alpha', name: 'Alpha' }] as never;
    useSessionStore().user = { email: 'admin@example.com' } as never;

    const row = item({ id: 1, title: 'first thing', outcomeId });
    backlogItems.mockResolvedValue([row]);
    backlogItem.mockResolvedValue({ item: row, dispatches: [], stats: [] });

    const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    [...document.querySelectorAll('tr')]
      .find((tr) => tr.textContent?.includes('first thing'))!
      .dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();

    return wrapper;
  }

  it('offers None and each active and proposed outcome, and sends the chosen one', async () => {
    updateBacklogItem.mockResolvedValue(item({ id: 1, outcomeId: faster.id }));
    const wrapper = await openTheItem(null);

    const picker = wrapper.findComponent(OutcomePicker).findComponent({ name: 'QSelect' });
    expect(optionsOf(picker)).toEqual([
      { label: 'None', value: '' },
      { label: 'Ship the release', value: shipping.id },
      { label: 'Faster onboarding (proposed)', value: faster.id },
    ]);

    picker.vm.$emit('update:modelValue', faster.id);
    await flushPromises();

    expect(updateBacklogItem).toHaveBeenCalledWith(1, { outcomeId: faster.id });

    wrapper.unmount();
  });

  it('sends an empty outcome to clear it', async () => {
    updateBacklogItem.mockResolvedValue(item({ id: 1 }));
    const wrapper = await openTheItem(shipping.id);

    const picker = wrapper.findComponent(OutcomePicker).findComponent({ name: 'QSelect' });
    expect(picker.props('modelValue')).toBe(shipping.id);

    picker.vm.$emit('update:modelValue', null);
    await flushPromises();

    expect(updateBacklogItem).toHaveBeenCalledWith(1, { outcomeId: '' });

    wrapper.unmount();
  });

  it('shows a retired current value by name with its status, and does not offer it', async () => {
    const read = answerEndedOutcomes();
    const wrapper = await openTheItem(oldGoal.id);

    const picker = wrapper.findComponent(OutcomePicker).findComponent({ name: 'QSelect' });
    expect(optionsOf(picker)).toEqual([
      { label: 'None', value: '' },
      { label: 'Ship the release', value: shipping.id },
      { label: 'Faster onboarding (proposed)', value: faster.id },
      { label: 'Old goal (retired)', value: oldGoal.id, disable: true },
    ]);
    expect(picker.text()).toContain('Old goal (retired)');
    expect(picker.text()).not.toContain(oldGoal.id);
    expect(read).toEqual([`/api/outcomes/${oldGoal.id}`]);

    wrapper.unmount();
  });
});

describe('a retired or merged current value', () => {
  it('shows a merged one in the picker as "(merged)", and drops it once another is chosen', async () => {
    answerEndedOutcomes();
    const wrapper = mount(OutcomePicker, { props: { modelValue: duplicate.id } });
    await flushPromises();

    const picker = wrapper.findComponent({ name: 'QSelect' });
    expect(optionsOf(picker).at(-1)).toEqual({ label: 'Duplicate goal (merged)', value: duplicate.id, disable: true });
    expect(picker.text()).toContain('Duplicate goal (merged)');

    await wrapper.setProps({ modelValue: shipping.id });
    await flushPromises();
    expect(optionsOf(picker).map((option) => option.value)).toEqual(['', shipping.id, faster.id]);

    wrapper.unmount();
  });

  it('reads nothing for a live current value', async () => {
    const read = answerEndedOutcomes();
    const wrapper = mount(OutcomePicker, { props: { modelValue: faster.id } });
    await flushPromises();

    expect(read).toEqual([]);
    expect(optionsOf(wrapper.findComponent({ name: 'QSelect' })).map((option) => option.value)).toEqual(['', shipping.id, faster.id]);

    wrapper.unmount();
  });
});

describe('a trigger', () => {
  function row(over: Partial<TeamTrigger> = {}): TeamTrigger {
    return {
      id: 't1',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'Nightly',
      instruction: 'Tidy up.',
      kind: 'interval',
      expression: null,
      timezone: null,
      intervalSeconds: 3600,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-09-23T10:00:00Z',
      createdBy: 'u1',
      eventType: null,
      filter: null,
      ...over,
    } as TeamTrigger;
  }

  async function saveWithOutcome(trigger: TeamTrigger | null, outcome: string | null) {
    const wrapper = await mountDialog(TriggerEditDialog, { trigger, container: 'Manager', events: [], saving: false });
    await flushPromises();

    const picker = wrapper.findComponent(OutcomePicker).findComponent({ name: 'QSelect' });
    expect(optionsOf(picker).map((option) => option.value)).toEqual(['', shipping.id, faster.id]);

    picker.vm.$emit('update:modelValue', outcome);
    await flushPromises();

    probe.button('Save').click();
    await probe.settle();

    const [[draft]] = wrapper.emitted('save') as [[TriggerDraft]];
    wrapper.unmount();

    return draft;
  }

  it('sends the chosen outcome when an existing trigger is saved', async () => {
    const existing = row();
    const draft = await saveWithOutcome(existing, shipping.id);

    expect(updateTriggerPatchFromDraft(existing, draft)).toMatchObject({ outcomeId: shipping.id });
  });

  it('sends an empty outcome to clear one, and nothing when it is unchanged', async () => {
    const existing = row({ outcomeId: faster.id });

    expect(updateTriggerPatchFromDraft(existing, await saveWithOutcome(existing, null))).toMatchObject({ outcomeId: '' });
    expect(updateTriggerPatchFromDraft(existing, await saveWithOutcome(existing, faster.id))).not.toHaveProperty('outcomeId');
  });

  it('carries the chosen outcome into a new trigger\'s request', async () => {
    const draft = await saveWithOutcome(row(), faster.id);

    expect(createTriggerRequestFromDraft(draft)).toMatchObject({ outcomeId: faster.id });
    expect(createTriggerRequestFromDraft({ ...draft, outcomeId: '' })).not.toHaveProperty('outcomeId');
  });
});
