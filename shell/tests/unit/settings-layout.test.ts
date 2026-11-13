// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import SettingsView from '../../src/renderer/src/components/SettingsView.vue';
import GlobalPolicyDialog from '../../src/renderer/src/components/GlobalPolicyDialog.vue';
import ListEditor from '../../src/renderer/src/components/ListEditor.vue';
import { fromSeconds, toSeconds } from '../../src/renderer/src/timeUnits';
import type { GlobalPolicyDoc, YawbleApi } from '../../src/shared/api';

// Settings > Skills fills the page; Settings > Guardrails is one column of section cards with a sticky Save bar.
// A scripted fake API answers every call: no core, no CLI, no network.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

let wrapper: VueWrapper | null = null;
afterEach(() => { wrapper?.unmount(); wrapper = null; });
function keep(w: VueWrapper) { wrapper = w; return w; }

const policy = (over: Partial<GlobalPolicyDoc['compute']> = {}): GlobalPolicyDoc => ({
  filesystem: { grantableRoots: ['~/src'], readonly: ['/opt/tools'], readwrite: [], denied: ['~/.ssh'] },
  network: { allowHosts: ['api.github.com'] },
  compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800, memoryMb: 2048, cpuPercent: 50, maxProcesses: 64, ...over },
  ui: { clipboard: 'read' },
  app: { maxConcurrentAgents: 4, backend: 'sandbox' },
});

function policyApi(doc: GlobalPolicyDoc) {
  return fakeApi({
    'policy.getGlobal': () => doc,
    'policy.saveGlobal': (p) => ({ ...doc, ...p.policy }),
    'guardrail.get': () => ({ state: 'none', error: null }),
    'guardrail.start': () => ({}),
  });
}

async function mountPolicy(doc: GlobalPolicyDoc) {
  const f = policyApi(doc);
  const w = keep(mount(GlobalPolicyDialog, { props: { api: f.api, inline: true } }));
  await flushPromises();
  return { w, calls: f.calls };
}

const val = (w: VueWrapper, t: string) => (w.get(`[data-test=${t}]`).element as HTMLInputElement).value;
const savePayload = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'policy.saveGlobal').at(-1)!.params;

describe('Settings > Skills fills the page', () => {
  it('the page, the tab pane, the panel and the two panes form one fill chain on Skills only', async () => {
    const f = fakeApi({ 'skills.list': () => [{ kind: 'builtin', id: null, name: 'review', description: 'd', roles: ['member'], body: 'b', teamId: null, team: null }] });
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'skills', teams: [] } }));
    await flushPromises();
    expect(w.get('[data-test=settings]').classes()).toContain('fill');
    expect(w.get('[data-test=settings-pane]').classes()).toContain('fill');
    expect(w.get('.inline-panel').classes()).toContain('fill');
    const layout = w.get('[data-test=skills-layout]');
    expect(layout.classes()).toContain('fill');
    expect(layout.find('[data-test=skills-list]').exists()).toBe(true);
    expect(layout.find('[data-test=skills-pane]').exists()).toBe(true);

    // The text box sits above the button row inside the right pane.
    await w.get('[data-test=skill-review]').trigger('click');
    const pane = w.get('[data-test=skills-pane]');
    const kids = pane.element.children;
    expect(kids[kids.length - 2].getAttribute('data-test')).toBe('skill-view');
    expect(kids[kids.length - 1].querySelector('[data-test=skill-customize]')).not.toBeNull();
  });

  it('other tabs keep their normal page scrolling', async () => {
    const f = fakeApi({ 'policy.getGlobal': () => policy(), 'guardrail.get': () => ({ state: 'none', error: null }) });
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'globalPolicy', teams: [] } }));
    await flushPromises();
    expect(w.get('[data-test=settings]').classes()).not.toContain('fill');
    expect(w.get('[data-test=settings-pane]').classes()).not.toContain('fill');
    expect(w.get('.inline-panel').classes()).not.toContain('fill');
  });
});

describe('timeouts as a number and a unit', () => {
  it('shows a value in the largest unit that divides it and converts back to the same seconds', () => {
    expect(fromSeconds(7200)).toEqual({ value: 2, unit: 'hours' });
    expect(fromSeconds(1800)).toEqual({ value: 30, unit: 'minutes' });
    expect(fromSeconds(90)).toEqual({ value: 90, unit: 'seconds' });
    expect(fromSeconds(5400)).toEqual({ value: 90, unit: 'minutes' });
    for (const s of [7200, 1800, 90, 5400, 61, 3600, 60]) {
      const { value, unit } = fromSeconds(s);
      expect(toSeconds(value, unit)).toBe(s);
    }
    expect(toSeconds(1.5, 'hours')).toBe(5400);
  });

  it('the form shows 7200 s as 2 hours and 1800 s as 30 minutes; 90 s as 90 seconds', async () => {
    const { w } = await mountPolicy(policy({ wallTimeoutSec: 7200, idleTimeoutSec: 90 }));
    expect((w.get('[data-test=global-wallTimeoutSec]').element as HTMLInputElement).value).toBe('2');
    expect((w.get('[data-test=global-wallTimeoutSec-unit]').element as HTMLSelectElement).value).toBe('hours');
    expect((w.get('[data-test=global-idleTimeoutSec]').element as HTMLInputElement).value).toBe('90');
    expect((w.get('[data-test=global-idleTimeoutSec-unit]').element as HTMLSelectElement).value).toBe('seconds');
    // Seconds is offered only where it is needed.
    expect(w.findAll('[data-test=global-wallTimeoutSec-unit] option').map((o) => o.text())).toEqual(['minutes', 'hours']);

    w.unmount();
    const { w: w2 } = await mountPolicy(policy({ idleTimeoutSec: 1800 }));
    expect((w2.get('[data-test=global-idleTimeoutSec]').element as HTMLInputElement).value).toBe('30');
    expect((w2.get('[data-test=global-idleTimeoutSec-unit]').element as HTMLSelectElement).value).toBe('minutes');
  });

  it('a unit switch keeps the duration: 2 hours -> 120 minutes; 90 seconds -> 1.5 minutes; no change to save', async () => {
    const { w, calls } = await mountPolicy(policy({ wallTimeoutSec: 7200, idleTimeoutSec: 90 }));
    await w.get('[data-test=global-wallTimeoutSec-unit]').setValue('minutes');
    expect(val(w, 'global-wallTimeoutSec')).toBe('120');
    expect(val(w, 'global-wallTimeoutSec-unit')).toBe('minutes');
    await w.get('[data-test=global-idleTimeoutSec-unit]').setValue('minutes');
    expect(val(w, 'global-idleTimeoutSec')).toBe('1.5');
    // The same durations: nothing to save.
    expect(w.get('[data-test=save-global]').attributes('disabled')).toBeDefined();
    expect(w.find('[data-test=global-dirty]').exists()).toBe(false);
    await w.get('[data-test=global-wallTimeoutSec-unit]').setValue('hours');
    expect(val(w, 'global-wallTimeoutSec')).toBe('2');

    // Saved back exactly, alongside a real change.
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('5');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(savePayload(calls).policy.compute).toMatchObject({ wallTimeoutSec: 7200, idleTimeoutSec: 90 });
  });

  it('a duration that is no exact decimal in the new unit still saves its own seconds', async () => {
    const { w, calls } = await mountPolicy(policy({ idleTimeoutSec: 100 }));
    await w.get('[data-test=global-idleTimeoutSec-unit]').setValue('minutes');
    expect(Number(val(w, 'global-idleTimeoutSec'))).toBeCloseTo(100 / 60, 12);
    await w.get('[data-test=global-idleTimeoutSec-unit]').setValue('hours');
    await w.get('[data-test=global-idleTimeoutSec-unit]').setValue('seconds');
    expect(val(w, 'global-idleTimeoutSec')).toBe('100');
    await w.get('[data-test=global-idleTimeoutSec-unit]').setValue('minutes');
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('5');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(savePayload(calls).policy.compute.idleTimeoutSec).toBe(100);
  });

  it('a new number in a unit saves that duration in seconds', async () => {
    const { w, calls } = await mountPolicy(policy({ wallTimeoutSec: 7200 }));
    await w.get('[data-test=global-wallTimeoutSec]').setValue('3');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(savePayload(calls).policy.compute.wallTimeoutSec).toBe(10800);
  });
});

describe('ListEditor', () => {
  function editor(initial: string[]) {
    let value = initial;
    const w = keep(mount(ListEditor, {
      props: { modelValue: value, 'onUpdate:modelValue': (v: string[]) => { value = v; void w.setProps({ modelValue: v }); } },
    }));
    return { w, get: () => value };
  }

  it('adds on Enter, trimmed, ignoring duplicates and blanks; × removes', async () => {
    const { w, get } = editor(['a.org']);
    const input = w.get('[data-test=list-input]');
    await input.setValue('  b.org  ');
    await input.trigger('keydown', { key: 'Enter' });
    expect(get()).toEqual(['a.org', 'b.org']);
    expect((input.element as HTMLInputElement).value).toBe('');
    await input.setValue('a.org');
    await input.trigger('keydown', { key: 'Enter' });
    await input.setValue('   ');
    await input.trigger('keydown', { key: 'Enter' });
    expect(get()).toEqual(['a.org', 'b.org']);
    await input.setValue('c.org, d.org,c.org');
    await input.trigger('keydown', { key: 'Enter' });
    expect(get()).toEqual(['a.org', 'b.org', 'c.org', 'd.org']);
    await w.findAll('[data-test=list-remove]')[1].trigger('click');
    expect(get()).toEqual(['a.org', 'c.org', 'd.org']);
    expect(w.findAll('[data-test=list-value]').map((v) => v.text())).toEqual(['a.org', 'c.org', 'd.org']);
  });

  it('a folder with spaces stays one entry; an empty list shows None', async () => {
    const { w, get } = editor([]);
    expect(w.get('[data-test=list-none]').text()).toBe('None');
    const input = w.get('[data-test=list-input]');
    await input.setValue('C:\\Program Files\\Tools');
    await input.trigger('keydown', { key: 'Enter' });
    expect(get()).toEqual(['C:\\Program Files\\Tools']);
    expect(w.find('[data-test=list-none]').exists()).toBe(false);
  });
});

describe('Settings > Guardrails', () => {
  it('shows the sections in order, one column', async () => {
    const { w } = await mountPolicy(policy());
    const ids = w.findAll('[data-test=global-form] > *').map((e) => e.attributes('data-test'));
    expect(ids).toEqual(['global-section-limits', 'global-section-network', 'global-section-files', 'global-section-desktop',
      'global-section-sandbox', 'global-guardrail', 'global-savebar']);
    // The caps that are not enforced (memory, CPU, processes) are not shown at all.
    expect(w.text()).not.toContain('Not enforced yet');
    for (const k of ['memoryMb', 'cpuPercent', 'maxProcesses']) expect(w.find(`[data-test=global-${k}]`).exists()).toBe(false);
    expect(w.get('[data-test=global-section-limits]').text()).toContain('most sandboxes at once across all teams: member runs, Manager runs and interactive sessions');
    expect(w.get('[data-test=global-section-network]').text()).toContain('beyond the model API and the team bus');
    expect(w.get('[data-test=global-section-files]').text()).toContain("a team's settings can grant folders inside these");
    expect(w.get('[data-test=global-readwrite] [data-test=list-none]').text()).toBe('None');
  });

  it('Save is disabled until something changes, then shows Unsaved changes; after the save, Saved', async () => {
    const { w, calls } = await mountPolicy(policy());
    const save = w.get('[data-test=save-global]');
    expect(save.attributes('disabled')).toBeDefined();
    expect(w.find('[data-test=global-dirty]').exists()).toBe(false);
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('3');
    expect(save.attributes('disabled')).toBeUndefined();
    expect(w.get('[data-test=global-dirty]').text()).toBe('Unsaved changes');
    // Back to the saved value: nothing to save again.
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('4');
    expect(save.attributes('disabled')).toBeDefined();
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('3');
    await save.trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'policy.saveGlobal')).toHaveLength(1);
    expect(w.get('[data-test=global-saved]').text()).toBe('Saved');
    expect(w.find('[data-test=global-dirty]').exists()).toBe(false);
    expect(save.attributes('disabled')).toBeDefined();
    expect(w.emitted('changed')).toHaveLength(1);
  });

  it('sends the same payload shape as before, with the stored caps sent back exactly as loaded', async () => {
    const { w, calls } = await mountPolicy(policy());
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('6');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(savePayload(calls)).toEqual({
      policy: {
        filesystem: { grantableRoots: ['~/src'], readonly: ['/opt/tools'], readwrite: [], denied: ['~/.ssh'] },
        network: { allowHosts: ['api.github.com'] },
        compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800, memoryMb: 2048, cpuPercent: 50, maxProcesses: 64 },
        ui: { clipboard: 'read' },
        app: { maxConcurrentAgents: 6, backend: 'sandbox', heavyWorkAtOnce: 1 },
      },
    });
  });

  it('caps left empty are still left out, as before', async () => {
    const { w, calls } = await mountPolicy(policy({ memoryMb: null, cpuPercent: null, maxProcesses: null } as any));
    await w.get('[data-test=global-clipboard]').setValue('none');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(savePayload(calls).policy.compute).toEqual({ wallTimeoutSec: 7200, idleTimeoutSec: 1800 });
    expect(savePayload(calls).policy.ui).toEqual({ clipboard: 'none' });
  });

  it('hosts and folders are edited as lists and saved as lists', async () => {
    const { w, calls } = await mountPolicy(policy());
    const hosts = w.get('[data-test=global-allowHosts]');
    await hosts.get('[data-test=list-input]').setValue(' example.org ');
    await hosts.get('[data-test=list-input]').trigger('keydown', { key: 'Enter' });
    await w.get('[data-test=global-denied] [data-test=list-remove]').trigger('click');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    const p = savePayload(calls).policy;
    expect(p.network.allowHosts).toEqual(['api.github.com', 'example.org']);
    expect(p.filesystem.denied).toEqual([]);
  });

  it('text typed in an Add field but not added is a change, and Save adds it (trimmed, deduped) then saves in one click', async () => {
    const { w, calls } = await mountPolicy(policy());
    const save = w.get('[data-test=save-global]');
    await w.get('[data-test=global-allowHosts] [data-test=list-input]').setValue('  example.org , api.github.com');
    expect(save.attributes('disabled')).toBeUndefined();
    expect(w.get('[data-test=global-dirty]').text()).toBe('Unsaved changes');
    await w.get('[data-test=global-readwrite] [data-test=list-input]').setValue('  ~/work  ');
    await save.trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'policy.saveGlobal')).toHaveLength(1);
    const p = savePayload(calls).policy;
    expect(p.network.allowHosts).toEqual(['api.github.com', 'example.org']);
    expect(p.filesystem.readwrite).toEqual(['~/work']);
    expect(val(w, 'global-allowHosts] [data-test=list-input')).toBe('');
    expect(w.get('[data-test=global-saved]').text()).toBe('Saved');
    expect(save.attributes('disabled')).toBeDefined();
  });

  it('blank text in an Add field is no change', async () => {
    const { w } = await mountPolicy(policy());
    await w.get('[data-test=global-denied] [data-test=list-input]').setValue('  , ');
    expect(w.get('[data-test=save-global]').attributes('disabled')).toBeDefined();
  });

  it('+ Package registries adds the registry hosts once', async () => {
    const { w, calls } = await mountPolicy(policy());
    await w.get('[data-test=global-add-registries]').trigger('click');
    await w.get('[data-test=global-add-registries]').trigger('click');
    const shown = w.findAll('[data-test=global-allowHosts] [data-test=list-value]').map((v) => v.text());
    expect(shown).toEqual(['api.github.com', 'api.nuget.org', 'globalcdn.nuget.org', 'registry.npmjs.org', 'pypi.org', 'files.pythonhosted.org']);
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(savePayload(calls).policy.network.allowHosts).toEqual(shown);
  });

  it('a refused save shows its error in the footer and stays unsaved', async () => {
    const f = fakeApi({
      'policy.getGlobal': () => policy(),
      'policy.saveGlobal': () => { throw new Error("Error invoking remote method 'core:call': CoreRpcError: network.allowHosts: '10.0.0.1' is an IP literal"); },
      'guardrail.get': () => ({ state: 'none', error: null }),
    });
    const w = keep(mount(GlobalPolicyDialog, { props: { api: f.api, inline: true } }));
    await flushPromises();
    await w.get('[data-test=global-maxConcurrentAgents]').setValue('5');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=global-savebar] [data-test=global-error]').text()).toBe("network.allowHosts: '10.0.0.1' is an IP literal");
    expect(w.find('[data-test=global-saved]').exists()).toBe(false);
    expect(w.get('[data-test=save-global]').attributes('disabled')).toBeUndefined();
  });

  it('a timeout that is not a whole number of seconds is refused before saving', async () => {
    const { w, calls } = await mountPolicy(policy());
    await w.get('[data-test=global-wallTimeoutSec]').setValue('1.00001');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=global-error]').text()).toBe('Wall-clock timeout: a whole number of seconds');
    expect(calls.some((c) => c.method === 'policy.saveGlobal')).toBe(false);
  });

  it('the Guardrail check card runs the same RPC as before', async () => {
    const { w, calls } = await mountPolicy(policy());
    const card = w.get('[data-test=global-guardrail]');
    expect(card.element.tagName).toBe('SECTION');
    expect(card.text()).toContain('Guardrail check');
    expect(card.text()).toContain('Runs the global policy alone as a synthetic agent.');
    const run = card.get('[data-test=guardrail-run]');
    expect(run.text()).toBe('Run check');
    await run.trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'guardrail.start')!.params).toEqual({});
    expect(calls.filter((c) => c.method === 'guardrail.get').every((c) => JSON.stringify(c.params) === '{}')).toBe(true);
  });
});

describe('no CSS containment on what hosts a Modal', () => {
  // A size container (container-type), `contain` or a transform on an ancestor becomes the containing block of a
  // position: fixed Modal opened inside a Settings tab (Agents dialogs, IntegrationDialog, Companion Revoke), so the
  // backdrop would be clipped by the scrolling page. happy-dom applies no SFC styles, so this reads the style blocks.
  const styleOf = (file: string) => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/components', file), 'utf8');
    return [...src.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)].map((m) => m[1]).join('\n').replace(/\/\*[\s\S]*?\*\//g, '');
  };
  const containment = /\bcontainer(-type)?\s*:|\bcontain\s*:|\btransform\s*:/;

  it('the Settings page, its tab pane and the inline panel carry none', () => {
    for (const file of ['SettingsView.vue', 'Modal.vue', 'SkillsDialog.vue', 'ListEditor.vue']) {
      expect(styleOf(file), file).not.toMatch(containment);
    }
  });

  it('Guardrails: only the cards of rows are containers, and the Guardrail check card is not one', async () => {
    const rules = styleOf('GlobalPolicyDialog.vue').split('}').filter((r) => containment.test(r)).map((r) => r.split('{')[0].trim());
    expect(rules).toEqual(['.card.rows']);
    const { w } = await mountPolicy(policy());
    expect(w.get('[data-test=global-guardrail]').classes()).not.toContain('rows');
    expect(w.get('[data-test=global-form]').classes()).not.toContain('rows');
    expect(w.findAll('.rows [data-test=guardrail-panel]')).toHaveLength(0);
  });
});
