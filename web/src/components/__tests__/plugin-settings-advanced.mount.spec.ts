// @vitest-environment happy-dom
//
// A PLUGIN'S SETTINGS, EVERYDAY FIRST: the settings its manifest marks `advanced` sit behind one
// "Advanced settings (n)" toggle, closed until a person opens it or one of them needs fixing; each
// setting's description is behind its info icon, never printed under it.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PluginSettingsForm from '../PluginSettingsForm.vue';
import type { PluginConfigField } from '../../api/types';
import type { PluginSettingsShape } from '../../lib/pluginSettings';
import { helpTexts, resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

const field = (over: Partial<PluginConfigField>): PluginConfigField => ({
  type: 'string',
  description: '',
  required: false,
  default: null,
  enum: null,
  setBy: 'anyone',
  ...over,
});

const shape: PluginSettingsShape = {
  config: {
    dailySendCap: field({ type: 'number', default: 20, min: 1, max: 1000, integer: true, advanced: true, description: 'Real sends per day.' }),
    mode: field({ enum: ['dryRun', 'send'], default: 'dryRun', description: 'dryRun never sends.' }),
    footer: field({ type: 'bool', default: true, advanced: true, description: 'Adds Reply STOP.' }),
    fromNumber: field({ description: 'The number texts come from.' }),
  },
  secrets: {},
};

async function mountForm(config: Record<string, string | boolean | string[]> = {}) {
  const wrapper = mount(PluginSettingsForm, {
    attachTo: document.body,
    props: { shape, config: { mode: 'dryRun', fromNumber: '', dailySendCap: '20', footer: true, ...config }, secrets: {} },
  });
  await flushPromises();
  return wrapper;
}

const shown = () => [...document.body.querySelectorAll('[data-setting]')].map((el) => el.getAttribute('data-setting'));

describe('a plugin member\'s settings', () => {
  it('shows the everyday settings first and keeps the advanced ones behind one toggle until it is opened', async () => {
    const wrapper = await mountForm();

    expect(shown()).toEqual(['mode', 'fromNumber']);
    const toggle = document.body.querySelector<HTMLElement>('[data-advanced-toggle]')!;
    expect(toggle.textContent).toContain('Advanced settings (2)');

    toggle.click();
    await flushPromises();

    expect(shown()).toEqual(['mode', 'fromNumber', 'dailySendCap', 'footer']);

    wrapper.unmount();
  });

  it('opens Advanced by itself when an advanced setting needs fixing', async () => {
    const wrapper = await mountForm({ dailySendCap: '5000' });

    expect(shown()).toContain('dailySendCap');

    wrapper.unmount();
  });

  it('puts each description, and a number\'s bounds, behind the info icon and prints none of it', async () => {
    const wrapper = await mountForm();
    document.body.querySelector<HTMLElement>('[data-advanced-toggle]')!.click();
    await flushPromises();

    expect(helpTexts()).toEqual([
      'dryRun never sends.',
      'The number texts come from.',
      'Real sends per day. Between 1 and 1000, whole numbers only.',
      'Adds Reply STOP.',
    ]);
    expect(document.body.textContent).not.toContain('Real sends per day.');

    wrapper.unmount();
  });

  it('offers no Advanced toggle for a plugin with no advanced settings', async () => {
    const wrapper = mount(PluginSettingsForm, {
      attachTo: document.body,
      props: { shape: { config: { mode: shape.config.mode! }, secrets: {} }, config: { mode: 'dryRun' }, secrets: {} },
    });
    await flushPromises();

    expect(document.body.querySelector('[data-advanced-toggle]')).toBeNull();

    wrapper.unmount();
  });
});
