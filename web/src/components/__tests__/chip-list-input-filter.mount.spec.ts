// @vitest-environment happy-dom
//
// A LIST FROM A FIXED SET IS FILTERED BY TYPING: the field takes text (on a phone, its keyboard), and
// the dropdown narrows to the options whose words, value or keywords hold it - so "brit", "united",
// "gb" and "en-GB" all find English (United Kingdom) in a list of hundreds. Picking one clears the text.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { QSelect } from 'quasar';

import ChipListInput from '../ChipListInput.vue';
import { resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

const languages = [
  { value: 'en-GB', label: 'English (United Kingdom)', keywords: 'British English' },
  { value: 'en-US', label: 'English (United States)', keywords: 'American English' },
  { value: 'fr-FR', label: 'French (France)' },
  { value: 'de', label: 'German' },
  { value: 'ga', label: 'Irish' },
];

async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function mountList(props: Record<string, unknown>) {
  return mount(ChipListInput, { props: { modelValue: [], options: languages, label: 'Language', ...props }, attachTo: document.body });
}

/** Types into the list's own field, as a person does, and waits for the dropdown to take it. */
async function type(text: string) {
  const input = document.body.querySelector<HTMLInputElement>('[data-chip-list] input')!;
  input.focus();
  input.value = text;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
}

const offered = (wrapper: ReturnType<typeof mountList>) =>
  (wrapper.findComponent(QSelect).props('options') as { value: string }[]).map((option) => option.value);

describe('a list from a fixed set, filtered by typing', () => {
  it('has a field to type in, though it takes no free text', async () => {
    const wrapper = mountList({});
    await settle();

    expect(wrapper.findComponent(QSelect).props('useInput')).toBe(true);
    expect(document.body.querySelector('[data-chip-list] input')).not.toBeNull();
    expect(document.body.querySelector('[data-chip-add]')).toBeNull();

    wrapper.unmount();
  });

  it.each([
    ['brit', 'its other name'],
    ['united', 'its words'],
    ['UNITED KINGDOM', 'its words in any case'],
    ['gb', 'part of its code'],
    ['en-GB', 'its code'],
  ])('finds English (United Kingdom) by "%s" (%s)', async (text) => {
    const wrapper = mountList({});
    await settle();

    await type(text);
    expect(offered(wrapper)).toContain('en-GB');
    expect(offered(wrapper)).not.toContain('fr-FR');

    wrapper.unmount();
  });

  it('narrows to what matches and offers nothing already chosen', async () => {
    const wrapper = mountList({ modelValue: ['en-US'] });
    await settle();

    await type('english');
    expect(offered(wrapper)).toEqual(['en-GB']);

    await type('');
    expect(offered(wrapper)).toEqual(['en-GB', 'fr-FR', 'de', 'ga']);

    wrapper.unmount();
  });

  it('keeps what is typed and says so when it stops matching anything', async () => {
    const wrapper = mountList({ modelValue: ['en-US'] });
    await settle();

    await type('k');
    await type('klingon');
    expect(document.body.querySelector<HTMLInputElement>('[data-chip-list] input')!.value).toBe('klingon');
    expect(offered(wrapper)).toEqual([]);
    expect(document.body.querySelector('[data-chip-no-match]')?.textContent).toBe('Nothing matches "klingon".');

    wrapper.unmount();
  });

  it('adds the picked option by its value and clears the text, so the next pick starts from the whole list', async () => {
    const wrapper = mountList({ modelValue: ['en-US'] });
    await settle();

    await type('brit');
    wrapper.findComponent(QSelect).vm.$emit('update:modelValue', ['en-US', 'en-GB']);
    await settle();

    expect(wrapper.emitted('update:modelValue')!.at(-1)![0]).toEqual(['en-US', 'en-GB']);
    expect(document.body.querySelector<HTMLInputElement>('[data-chip-list] input')!.value).toBe('');

    wrapper.unmount();
  });

  it('takes no typing when read-only or disabled', async () => {
    const wrapper = mountList({ readonly: true });
    await settle();
    expect(wrapper.findComponent(QSelect).props('useInput')).toBe(false);
    wrapper.unmount();

    const disabled = mountList({ disable: true });
    await settle();
    expect(disabled.findComponent(QSelect).props('useInput')).toBe(false);
    disabled.unmount();
  });
});
