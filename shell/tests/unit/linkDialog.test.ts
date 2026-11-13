// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import LinkDialog from '../../src/renderer/src/components/LinkDialog.vue';
import type { CoreEvent, LinkStatus, YawbleApi } from '../../src/shared/api';

const base: LinkStatus = {
  state: 'off', url: null, defaultUrl: 'https://cloud.yawble.ai/', pairUrl: null, machineName: 'DESK', code: null, codeExpiresAt: null,
  instanceId: null, instanceName: null, org: null, workgroup: null, allowHostTerminals: false, viewers: [], error: null,
};

function fake() {
  const calls: { method: string; params: any }[] = [];
  const listeners = new Set<(e: CoreEvent) => void>();
  let status: LinkStatus = { ...base };
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'link.pair') {
        status = { ...status, state: 'pairing', url: params.url, code: 'ABCD-2345', pairUrl: `${params.url}pair?code=ABCD-2345` };
      } else if (method === 'link.cancel') status = { ...base };
      else if (method === 'link.setShowCode') {
        // As the core: refused while this computer is not attached (no link settings to keep it in).
        if (!status.instanceId) throw new Error('bad_state: this computer is not attached to a cloud site');
        status = { ...status, showCode: params.show };
      }
      return status as any;
    },
    onEvent: (l: (e: CoreEvent) => void) => { listeners.add(l); return () => listeners.delete(l); },
  } as unknown as YawbleApi;
  const push = (s: Partial<LinkStatus>) => { status = { ...status, ...s }; for (const l of listeners) l({ event: 'link.state', data: status }); };
  return { api, calls, push };
}

describe('the Cloud Link dialog', () => {
  it('offers yawble.ai, pairs, opens the pair page in the browser and follows the pairing to connected', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const f = fake();
    const w = mount(LinkDialog, { props: { api: f.api } });
    await flushPromises();
    expect(w.text()).toContain('Attach this computer to yawble.ai so you (and your team) can see and control it from a browser.');
    expect((w.get('[data-test=link-url]').element as HTMLInputElement).value).toBe('https://cloud.yawble.ai/');
    expect(w.get('[data-test=link-connect]').text()).toBe('Pair');

    await w.get('[data-test=link-connect]').trigger('click');
    await flushPromises();
    expect(f.calls.find((c) => c.method === 'link.pair')!.params).toEqual({ url: 'https://cloud.yawble.ai/' });
    expect(open).toHaveBeenCalledWith('https://cloud.yawble.ai/pair?code=ABCD-2345', '_blank');
    expect(w.get('[data-test=link-code]').text()).toBe('ABCD-2345');
    expect(w.get('[data-test=link-phase]').text()).toBe('Waiting for the cloud connection');
    expect(w.get('[data-test=link-phase-detail]').text()).toContain('cloud.yawble.ai');

    f.push({ state: 'pairing', error: 'cannot reach the cloud site: refused' });
    await flushPromises();
    expect(w.get('[data-test=link-phase]').attributes('data-phase')).toBe('offline');

    f.push({ state: 'connecting', error: null, code: null, pairUrl: null, instanceId: 'i1', instanceName: 'DESK', org: 'My Org', workgroup: 'My Workgroup' });
    await flushPromises();
    expect(w.get('[data-test=link-phase]').text()).toBe('Attached. Connecting to the cloud…');

    f.push({ state: 'connected' });
    await flushPromises();
    expect(w.get('[data-test=link-phase]').text()).toBe('Connected');
    expect(w.get('[data-test=link-phase-detail]').text()).toContain('DESK in My Org › My Workgroup');
    await w.get('[data-test=link-done]').trigger('click');
    expect(w.get('[data-test=link-name]').text()).toBe('DESK');
    open.mockRestore();
  });

  it('can cancel a pairing and go back', async () => {
    vi.spyOn(window, 'open').mockImplementation(() => null);
    const f = fake();
    const w = mount(LinkDialog, { props: { api: f.api } });
    await flushPromises();
    await w.get('[data-test=link-connect]').trigger('click');
    await flushPromises();
    await w.get('[data-test=link-cancel]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=link-pairing]').exists()).toBe(false);
    expect(w.get('[data-test=link-state]').text()).toBe('Not attached');
  });

  it('hides "Show members\' code" while this computer is not paired', async () => {
    const f = fake();
    const w = mount(LinkDialog, { props: { api: f.api } });
    await flushPromises();
    expect(w.get('[data-test=link-state]').text()).toBe('Not attached');
    expect(w.find('[data-test=link-show-code]').exists()).toBe(false);
    expect(f.calls.some((c) => c.method === 'link.setShowCode')).toBe(false);
  });

  it('shows "Show members\' code" when paired and toggles it with link.setShowCode', async () => {
    const f = fake();
    f.push({ state: 'connected', url: 'https://cloud.yawble.ai/', instanceId: 'i1', instanceName: 'DESK' });
    const w = mount(LinkDialog, { props: { api: f.api } });
    await flushPromises();
    const box = () => w.get('[data-test=link-show-code]').element as HTMLInputElement;
    expect(box().checked).toBe(true); // on unless turned off

    await w.get('[data-test=link-show-code]').setValue(false);
    await flushPromises();
    expect(f.calls.filter((c) => c.method === 'link.setShowCode').map((c) => c.params)).toEqual([{ show: false }]);
    expect(w.find('[data-test=link-error]').exists()).toBe(false);
    expect(box().checked).toBe(false);

    await w.get('[data-test=link-show-code]').setValue(true);
    await flushPromises();
    expect(f.calls.filter((c) => c.method === 'link.setShowCode').map((c) => c.params)).toEqual([{ show: false }, { show: true }]);
    expect(box().checked).toBe(true);

    // Detached by a status push: the box goes away with the attached view.
    f.push({ ...base });
    await flushPromises();
    expect(w.find('[data-test=link-show-code]').exists()).toBe(false);
  });
});
