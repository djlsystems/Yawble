// @vitest-environment happy-dom
//
// A PLUGIN'S SETTINGS: required and ordinary settings up front in the manifest's order, the ones its
// manifest marks `advanced` (and does not require) under an Advanced toggle at the end - closed until
// opened, and open by itself when one of them is out of range. No info icon and no Default line under
// a field: a text or number box's default is its placeholder, and Reset to default puts it back. What
// a person must see now - an out-of-range value, Required - stays printed.
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
    region: field({ description: 'Required, so up front though marked advanced.', required: true, advanced: true }),
  },
  secrets: {},
};

async function mountForm(config: Record<string, string | boolean | string[]> = {}) {
  const wrapper = mount(PluginSettingsForm, {
    attachTo: document.body,
    props: { shape, config: { mode: 'dryRun', fromNumber: '', dailySendCap: '', footer: true, region: '', ...config }, secrets: {} },
  });
  await flushPromises();
  return wrapper;
}

const shown = () => [...document.body.querySelectorAll('[data-setting]')].map((el) => el.getAttribute('data-setting'));
const label = (name: string) => document.body.querySelector(`[data-setting="${name}"] .q-field__label, [data-setting="${name}"] .q-toggle__label`)?.textContent?.trim();

const openAdvanced = async () => {
  document.body.querySelector<HTMLButtonElement>('[data-advanced-toggle]')!.click();
  await flushPromises();
};

describe("a plugin member's settings", () => {
  it('shows the required and ordinary settings up front and the advanced ones behind the toggle', async () => {
    const wrapper = await mountForm();

    expect(shown()).toEqual(['mode', 'fromNumber', 'region']);
    const toggle = document.body.querySelector('[data-advanced-toggle]')!;
    expect(toggle.textContent).toContain('Advanced');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    await openAdvanced();
    expect(shown()).toEqual(['mode', 'fromNumber', 'region', 'dailySendCap', 'footer']);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    await openAdvanced();
    expect(shown()).toEqual(['mode', 'fromNumber', 'region']);

    wrapper.unmount();
  });

  it('has no toggle when no setting is marked advanced', async () => {
    const plain: PluginSettingsShape = { config: { mode: shape.config.mode! }, secrets: {} };
    const wrapper = mount(PluginSettingsForm, { attachTo: document.body, props: { shape: plain, config: { mode: 'dryRun' }, secrets: {} } });
    await flushPromises();

    expect(shown()).toEqual(['mode']);
    expect(document.body.querySelector('[data-advanced-toggle]')).toBeNull();

    wrapper.unmount();
  });

  it("labels each by the manifest's label, else its name in words", async () => {
    const wrapper = await mountForm();
    await openAdvanced();

    expect(label('dailySendCap')).toBe('Daily send cap');
    expect(label('fromNumber')).toBe('Sending number');
    expect(label('footer')).toBe('Footer');

    wrapper.unmount();
  });

  it('has no info icon and prints no description, Default line or person-only line', async () => {
    const wrapper = await mountForm();
    await openAdvanced();

    expect(helpTexts()).toEqual([]);
    expect(document.body.querySelector('[data-help]')).toBeNull();
    expect(document.body.textContent).not.toContain('Real sends per day.');
    expect(document.body.textContent).not.toContain('Default:');

    wrapper.unmount();
  });

  it("shows a box's default as its placeholder, and Reset to default empties a changed one", async () => {
    const wrapper = await mountForm({ dailySendCap: '50' });
    await openAdvanced();

    const box = document.body.querySelector<HTMLInputElement>('[data-setting="dailySendCap"] input')!;
    expect(box.getAttribute('placeholder')).toBe('20');
    expect(box.value).toBe('50');

    document.body.querySelector<HTMLButtonElement>('[aria-label="Reset Daily send cap to default"]')!.click();
    await flushPromises();
    expect(wrapper.emitted('update:config')!.at(-1)![0]).toMatchObject({ dailySendCap: '' });

    wrapper.unmount();
  });

  it('keeps an out-of-range value and Required printed, opening Advanced for an advanced one', async () => {
    const wrapper = await mountForm({ dailySendCap: '5000' });

    expect(shown()).toContain('dailySendCap');
    expect(document.body.querySelector('[data-advanced-toggle]')!.getAttribute('aria-expanded')).toBe('true');

    expect(document.body.querySelector('[data-setting="dailySendCap"]')?.textContent).toContain('it must be at most 1000');
    expect(document.body.querySelector('[data-setting="fromNumber"] [data-required]')?.textContent).toBe('Required');

    wrapper.unmount();
  });

  it('keeps Advanced open once it opened by itself, when the person fixes the last bad value there', async () => {
    const wrapper = await mountForm({ dailySendCap: '5000' });
    const toggle = document.body.querySelector('[data-advanced-toggle]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    document.body.querySelector<HTMLButtonElement>('[aria-label="Reset Daily send cap to default"]')!.click();
    await flushPromises();
    await wrapper.setProps({ config: wrapper.emitted('update:config')!.at(-1)![0] as Record<string, string> });
    await flushPromises();

    expect(document.body.querySelector('[data-setting="dailySendCap"]')?.textContent).not.toContain('must be at most');
    expect(shown()).toEqual(['mode', 'fromNumber', 'region', 'dailySendCap', 'footer']);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    // Now it is the person's to close.
    await openAdvanced();
    expect(shown()).toEqual(['mode', 'fromNumber', 'region']);

    wrapper.unmount();
  });

  it("names a field by the manifest's label in a bounds sentence, else by its key", async () => {
    const labelled: PluginSettingsShape = {
      config: { ...shape.config, dailySendCap: { ...shape.config.dailySendCap!, label: 'Daily cap' } },
      secrets: {},
    };
    const wrapper = mount(PluginSettingsForm, {
      attachTo: document.body,
      props: { shape: labelled, config: { mode: 'dryRun', fromNumber: '', dailySendCap: '5000', footer: true, region: '' }, secrets: {} },
    });
    await flushPromises();
    expect(document.body.querySelector('[data-setting="dailySendCap"]')?.textContent).toContain('5000 is out of range for Daily cap: it must be at most 1000.');
    wrapper.unmount();

    const bare = await mountForm({ dailySendCap: '5000' });
    expect(document.body.querySelector('[data-setting="dailySendCap"]')?.textContent).toContain('5000 is out of range for dailySendCap: it must be at most 1000.');
    bare.unmount();
  });
});
