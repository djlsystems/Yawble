// @vitest-environment happy-dom
// Card 380 (fault from verify card 356): member pane ⋯ > Remove agent opens the window's confirm (TeamView's remove).
// The menu (v-if) goes on the pick, taking the clicked item with it; the confirm must hand the focus back to the ⋯
// button on Cancel, Escape or the backdrop - not to <body> - and nothing is removed. OK removes once.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import ConfirmDialog from '../../src/renderer/src/components/ConfirmDialog.vue';
import { confirmDialog, confirmState } from '../../src/renderer/src/confirm';
import type { AgentCard, YawbleApi } from '../../src/shared/api';

const card: AgentCard = {
  member: { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
} as unknown as AgentCard;
const api = { getInfo: async () => { throw new Error('no'); }, getAvailableBackends: async () => [], call: async () => ({}), onEvent: () => () => {} } as unknown as YawbleApi;
const FakeScreen = defineComponent({ props: { channel: String }, setup: (_p, { expose }) => { expose({ size: () => ({ cols: 100, rows: 30 }) }); return () => h('div'); } });

const q = (t: string) => document.querySelector<HTMLElement>(`[data-test="${t}"]`);
let removed = 0;
const wrappers: VueWrapper[] = [];
/** The pane with TeamView's remove: ask the confirm, remove only on OK. */
async function setup() {
  removed = 0;
  wrappers.push(mount(ConfirmDialog, { attachTo: document.body }));
  wrappers.push(mount(MemberPane, {
    attachTo: document.body,
    props: { card, api, onRemove: async () => { if (await confirmDialog({ message: 'Remove?', okLabel: 'Remove', danger: true })) removed++; } } as any,
    global: { stubs: { TerminalScreen: FakeScreen } },
  }));
  await flushPromises();
}
/** ⋯ > Remove agent, as a click does it (the item has the focus when it is pressed). */
async function pickRemove() {
  q('member-more')!.click();
  await flushPromises();
  const item = q('remove-agent')!;
  item.focus();
  item.click();
  await flushPromises();
  expect(q('member-more-menu')).toBeNull();
  expect(q('confirm-dialog')).not.toBeNull();
}
afterEach(() => { confirmState.value?.resolve(false); while (wrappers.length) wrappers.pop()!.unmount(); document.body.innerHTML = ''; });

describe('member pane ⋯ > Remove agent: the focus after the confirm', () => {
  it('Cancel: back on the ⋯ button, nothing removed', async () => {
    await setup();
    await pickRemove();
    q('confirm-cancel')!.click();
    await flushPromises();
    expect(q('confirm-dialog')).toBeNull();
    expect(document.activeElement).toBe(q('member-more'));
    expect(removed).toBe(0);
  });

  it('Escape: back on the ⋯ button, nothing removed', async () => {
    await setup();
    await pickRemove();
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    await flushPromises();
    expect(q('confirm-dialog')).toBeNull();
    expect(document.activeElement).toBe(q('member-more'));
    expect(removed).toBe(0);
  });

  it('the backdrop: back on the ⋯ button, nothing removed', async () => {
    await setup();
    await pickRemove();
    q('confirm-backdrop')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    await flushPromises();
    expect(document.activeElement).toBe(q('member-more'));
    expect(removed).toBe(0);
  });

  it('OK removes once', async () => {
    await setup();
    await pickRemove();
    q('confirm-ok')!.click();
    await flushPromises();
    expect(removed).toBe(1);
  });
});
