// @vitest-environment happy-dom
//
// A SETTING WITH `options`: a dropdown of each choice's words that stores its value - a named set
// (every language Chrome accepts, every IANA time zone) or a manifest's own list - filtered as the
// person types; a list setting with options is chips of the words. A value stored before the field
// had options shows the Host's sentence and does not hold Save.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { QSelect } from 'quasar';
import PluginSettingsForm from '../PluginSettingsForm.vue';
import type { PluginConfigField } from '../../api/types';
import { refusedOutOfRange, type PluginSettingsShape } from '../../lib/pluginSettings';
import { resetBody } from '../../test/mountQuasar';

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

const zones = [
  { value: 'UTC', label: 'UTC', keywords: 'Etc/UTC GMT' },
  { value: 'America/New_York', label: 'America/New_York', keywords: 'US/Eastern' },
  { value: 'Europe/London', label: 'Europe/London' },
];
const languages = [
  { value: 'en-GB', label: 'English (United Kingdom)', keywords: 'British English' },
  { value: 'en-US', label: 'English (United States)', keywords: 'American English' },
  { value: 'de-DE', label: 'German (Germany)' },
];

const shape: PluginSettingsShape = {
  config: {
    timeZone: field({ label: 'Time zone', default: 'UTC', enum: zones.map((z) => z.value), options: zones, optionSet: 'timeZone' }),
    languages: field({ type: 'list', label: 'Language', default: ['en-US'], enum: languages.map((l) => l.value), options: languages, optionSet: 'locale' }),
  },
  secrets: {},
};

async function mountForm(config: Record<string, string | string[]>, over: Partial<PluginSettingsShape> = {}) {
  const wrapper = mount(PluginSettingsForm, {
    attachTo: document.body,
    props: { shape: { ...shape, ...over }, config: { timeZone: 'UTC', languages: ['en-US'], ...config }, secrets: {} },
  });
  await flushPromises();
  return wrapper;
}

const timeZoneSelect = (wrapper: ReturnType<typeof mount>) =>
  wrapper.findAllComponents(QSelect).find((s) => s.element.closest('[data-setting="timeZone"]'))!;

describe('a setting with options', () => {
  it('offers every choice by its words, shows the chosen one by its words, and stores its value', async () => {
    const wrapper = await mountForm({ timeZone: 'Europe/London' });
    const select = timeZoneSelect(wrapper);

    expect(select.props('options')).toEqual(zones);
    expect(select.props('emitValue')).toBe(true);
    expect(select.props('mapOptions')).toBe(true);
    expect(document.body.querySelector<HTMLInputElement>('[data-setting="timeZone"] input')!.value).toBe('Europe/London');

    select.vm.$emit('update:modelValue', 'America/New_York');
    await flushPromises();
    expect(wrapper.emitted('update:config')!.at(-1)![0]).toMatchObject({ timeZone: 'America/New_York' });

    wrapper.unmount();
  });

  it('filters the choices by what is typed, in their words or their values', async () => {
    const wrapper = await mountForm({});
    const select = timeZoneSelect(wrapper);

    select.vm.$emit('filter', 'york', (fn: () => void) => fn());
    await flushPromises();
    expect(select.props('options')).toEqual([zones[1]]);

    select.vm.$emit('filter', 'us/eastern', (fn: () => void) => fn());
    await flushPromises();
    expect(select.props('options')).toEqual([zones[1]]);

    select.vm.$emit('filter', '', (fn: () => void) => fn());
    await flushPromises();
    expect(select.props('options')).toEqual(zones);

    wrapper.unmount();
  });

  it.each(['brit', 'united', 'gb', 'en-GB'])('filters a list setting by typing "%s", finding English (United Kingdom)', async (text) => {
    const wrapper = await mountForm({ languages: [] });
    const input = document.body.querySelector<HTMLInputElement>('[data-setting="languages"] input')!;

    input.focus();
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await flushPromises();
    await new Promise((resolve) => setTimeout(resolve, 10));
    await flushPromises();

    const list = wrapper.findAllComponents(QSelect).find((s) => s.element.closest('[data-setting="languages"]'))!;
    const offered = (list.props('options') as { value: string }[]).map((o) => o.value);
    expect(offered).toContain('en-GB');
    expect(offered).not.toContain('de-DE');

    wrapper.unmount();
  });

  it("shows a list's chosen items by their words", async () => {
    const wrapper = await mountForm({ languages: ['en-GB', 'de-DE'] });

    const chips = [...document.body.querySelectorAll('[data-setting="languages"] .q-chip')].map((c) => c.textContent?.trim());
    expect(chips.join(' ')).toContain('English (United Kingdom)');
    expect(chips.join(' ')).toContain('German (Germany)');

    wrapper.unmount();
  });

  it("shows the Host's sentence for a value stored before the options, and does not hold Save for it", async () => {
    const stored = { config: { timeZone: 'Mars/Olympus' }, outOfRange: { timeZone: "`timeZone` must be an IANA time zone, such as Europe/London, or UTC; 'Mars/Olympus' is not." } };
    const wrapper = await mountForm({ timeZone: 'Mars/Olympus' }, { stored });

    expect(document.body.querySelector('[data-setting="timeZone"]')?.textContent).toContain("'Mars/Olympus' is not");
    expect(refusedOutOfRange({ ...shape, stored }, { timeZone: 'Mars/Olympus', languages: ['en-US'] })).toEqual([]);

    wrapper.unmount();
  });
});
