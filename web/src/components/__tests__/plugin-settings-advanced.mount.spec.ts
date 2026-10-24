// @vitest-environment happy-dom
//
// A PLUGIN'S SETTINGS, ALL ON THE ONE SCREEN: every setting in the manifest's order, the ones its
// manifest marks `advanced` included - the mark is accepted and ignored, so there is no toggle. No
// info icon and no Default line under a field: a text or number box's default is its placeholder,
// and Reset to default puts it back. What a person must see now - an out-of-range value, Required -
// stays printed.
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
    fromNumber: field({ description: 'The number texts come from.', required: true, label: 'Sending number' }),
  },
  secrets: {},
};

async function mountForm(config: Record<string, string | boolean | string[]> = {}) {
  const wrapper = mount(PluginSettingsForm, {
    attachTo: document.body,
    props: { shape, config: { mode: 'dryRun', fromNumber: '', dailySendCap: '', footer: true, ...config }, secrets: {} },
  });
  await flushPromises();
  return wrapper;
}

const shown = () => [...document.body.querySelectorAll('[data-setting]')].map((el) => el.getAttribute('data-setting'));
const label = (name: string) => document.body.querySelector(`[data-setting="${name}"] .q-field__label, [data-setting="${name}"] .q-toggle__label`)?.textContent?.trim();

describe("a plugin member's settings", () => {
  it("shows every setting in the manifest's order, the advanced ones included, with no toggle", async () => {
    const wrapper = await mountForm();

    expect(shown()).toEqual(['dailySendCap', 'mode', 'footer', 'fromNumber']);
    expect(document.body.querySelector('[data-advanced-toggle]')).toBeNull();
    expect(document.body.textContent).not.toContain('Advanced settings');

    wrapper.unmount();
  });

  it("labels each by the manifest's label, else its name in words", async () => {
    const wrapper = await mountForm();

    expect(label('dailySendCap')).toBe('Daily send cap');
    expect(label('fromNumber')).toBe('Sending number');
    expect(label('footer')).toBe('Footer');

    wrapper.unmount();
  });

  it('has no info icon and prints no description, Default line or person-only line', async () => {
    const wrapper = await mountForm();

    expect(helpTexts()).toEqual([]);
    expect(document.body.querySelector('[data-help]')).toBeNull();
    expect(document.body.textContent).not.toContain('Real sends per day.');
    expect(document.body.textContent).not.toContain('Default:');

    wrapper.unmount();
  });

  it("shows a box's default as its placeholder, and Reset to default empties a changed one", async () => {
    const wrapper = await mountForm({ dailySendCap: '50' });

    const box = document.body.querySelector<HTMLInputElement>('[data-setting="dailySendCap"] input')!;
    expect(box.getAttribute('placeholder')).toBe('20');
    expect(box.value).toBe('50');

    document.body.querySelector<HTMLButtonElement>('[aria-label="Reset Daily send cap to default"]')!.click();
    await flushPromises();
    expect(wrapper.emitted('update:config')!.at(-1)![0]).toMatchObject({ dailySendCap: '' });

    wrapper.unmount();
  });

  it('keeps an out-of-range value and Required printed', async () => {
    const wrapper = await mountForm({ dailySendCap: '5000' });

    expect(document.body.querySelector('[data-setting="dailySendCap"]')?.textContent).toContain('it must be at most 1000');
    expect(document.body.querySelector('[data-setting="fromNumber"] [data-required]')?.textContent).toBe('Required');

    wrapper.unmount();
  });
});
