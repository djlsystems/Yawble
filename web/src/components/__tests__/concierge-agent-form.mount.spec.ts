// @vitest-environment happy-dom
//
// THE CONCIERGE TAB OF TENANT SETTINGS. It chooses
// the Agent only: what the Concierge is told is the built-in Concierge prompt.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';

const { concierge, listCatalog, setConcierge } = vi.hoisted(() => ({
  concierge: vi.fn(),
  listCatalog: vi.fn(),
  setConcierge: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
  setConcierge,
}));

import ConciergeAgentForm from '../ConciergeAgentForm.vue';
import { useConsoleStore } from '../../stores/console';
import { bodyText, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

const interactive = { name: 'claude-tui', mode: 'Interactive' };

beforeEach(() => {
  concierge.mockReset();
  listCatalog.mockReset();
  setConcierge.mockReset();
  setConcierge.mockResolvedValue(undefined);
  listCatalog.mockResolvedValue({
    agents: [interactive],
  });
});

afterEach(resetBody);

type Form = { load: () => Promise<void>; save: () => Promise<void>; dirty: boolean };

async function loaded(settings: Record<string, unknown> = { agent: 'claude-tui' }) {
  concierge.mockResolvedValue({ ...settings });
  setActivePinia(createPinia());
  vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue(undefined as never);

  const wrapper = mount(ConciergeAgentForm, { attachTo: document.body });
  await (wrapper.vm as unknown as { load: () => Promise<void> }).load();
  await settle();

  return wrapper;
}

describe('ConciergeAgentForm, mounted', () => {
  it('offers no Prompt and no added instructions', async () => {
    const wrapper = await loaded();

    const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((select) => select.props('label'));
    expect(labels).toEqual(['Agent']);
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(bodyText()).toContain('built-in Concierge prompt');

    wrapper.unmount();
  });

  // A FRESH VOLUME: nobody has chosen, the stored agent is NULL and the server runs its default.
  describe('with no Agent chosen', () => {
    const unchosen = {
      agent: null,
      effective: {
        agent: 'grok',
        agentSource: 'default',
        auth: { installed: true, signedIn: true, detail: null },
      },
    };

    it('says so and names the default from effective', async () => {
      const wrapper = await loaded(unchosen);

      const line = document.body.querySelector('.concierge-default-agent-text');
      expect(line?.textContent).toContain('No Agent chosen');
      expect(line?.textContent).toContain('grok');
      expect(bodyText()).not.toContain('null');

      wrapper.unmount();
    });

    it('still says so when the server does not name the default', async () => {
      const wrapper = await loaded({ agent: null });

      const line = document.body.querySelector('.concierge-default-agent-text');
      expect(line?.textContent).toContain('No Agent chosen. The Concierge will use the default.');

      wrapper.unmount();
    });

    it('sends the Agent once one is picked', async () => {
      const wrapper = await loaded(unchosen);

      await wrapper.findAllComponents({ name: 'QSelect' })[0]!.vm.$emit('update:modelValue', 'claude-tui');
      await (wrapper.vm as unknown as Form).save();

      expect(setConcierge.mock.calls[0]![0]).toEqual({ agent: 'claude-tui' });

      wrapper.unmount();
    });
  });

  it('clearing a chosen Agent sends a blank one, back to the default', async () => {
    const wrapper = await loaded();

    await wrapper.findAllComponents({ name: 'QSelect' })[0]!.vm.$emit('update:modelValue', null);
    await settle();
    expect(document.body.querySelector('.concierge-default-agent-text')?.textContent).toContain('No Agent chosen');

    await (wrapper.vm as unknown as Form).save();

    expect(setConcierge.mock.calls[0]![0]).toEqual({ agent: '' });

    wrapper.unmount();
  });

  it('saves nothing when nothing moved', async () => {
    const wrapper = await loaded();

    await (wrapper.vm as unknown as Form).save();

    expect(setConcierge).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
