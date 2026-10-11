// @vitest-environment happy-dom
//
// THE ONE MULTIPLE-ITEM INPUT: a list shown as chips - removable when editable, plain when read-only
// - with free text added through a dialog, one item per line, and a fixed set chosen from a dropdown.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

import ChipListInput from '../ChipListInput.vue';
import { resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function mountList(props: Record<string, unknown>) {
  return mount(ChipListInput, { props: props as { modelValue: string[] }, attachTo: document.body });
}

const chips = () => [...document.body.querySelectorAll('[data-chip]')].map((chip) => chip.getAttribute('data-chip'));
const emitted = (wrapper: ReturnType<typeof mountList>) =>
  (wrapper.emitted('update:modelValue') ?? []).map((call) => call[0]);

async function typeAndAdd(value: string) {
  document.body.querySelector<HTMLElement>('[data-chip-add]')!.click();
  await settle();
  const input = document.body.querySelector<HTMLTextAreaElement>('[data-chip-add-input] textarea, textarea[data-chip-add-input]')!;
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
  document.body.querySelector<HTMLElement>('[data-chip-add-submit]')!.click();
  await settle();
}

describe('ChipListInput', () => {
  it('shows each item as a removable chip and removes one', async () => {
    const wrapper = mountList({ modelValue: ['Boston, MA', 'remote'], label: 'locations' });
    await settle();

    expect(chips()).toEqual(['Boston, MA', 'remote']);
    document.body.querySelector<HTMLElement>('[data-chip="remote"] .q-chip__icon--remove')!.click();
    await settle();

    expect(emitted(wrapper)).toEqual([['Boston, MA']]);
    wrapper.unmount();
  });

  it('adds free text through a dialog, one item per line, keeping a value with a comma whole', async () => {
    const wrapper = mountList({ modelValue: ['Systems Analyst'], label: 'positions', itemName: 'position' });
    await settle();

    await typeAndAdd('Business Analyst\n  Boston, MA  \n');

    expect(emitted(wrapper)).toEqual([['Systems Analyst', 'Business Analyst', 'Boston, MA']]);
    wrapper.unmount();
  });

  it('refuses a duplicate and a value the caller refuses, in the dialog, adding nothing', async () => {
    const wrapper = mountList({
      modelValue: ['developer'],
      ignoreCase: true,
      validate: (value: string) => (value.includes(' ') ? `${value} has a space.` : null),
    });
    await settle();

    await typeAndAdd('Developer');
    expect(document.body.textContent).toContain('Developer is already in the list.');

    const input = document.body.querySelector<HTMLTextAreaElement>('[data-chip-add-input] textarea, textarea[data-chip-add-input]')!;
    input.value = 'two words';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    document.body.querySelector<HTMLElement>('[data-chip-add-submit]')!.click();
    await settle();
    expect(document.body.textContent).toContain('two words has a space.');

    expect(emitted(wrapper)).toEqual([]);
    wrapper.unmount();
  });

  it('offers a fixed set from the dropdown with no add button, and only what is not chosen', async () => {
    const wrapper = mountList({ modelValue: ['themuse'], options: ['themuse', 'adzuna', 'usajobs'] });
    await settle();

    expect(document.body.querySelector('[data-chip-add]')).toBeNull();
    const select = wrapper.findComponent({ name: 'QSelect' });
    expect((select.props('options') as { value: string }[]).map((option) => option.value)).toEqual(['adzuna', 'usajobs']);

    select.vm.$emit('update:modelValue', ['themuse', 'adzuna']);
    await settle();
    expect(emitted(wrapper)).toEqual([['themuse', 'adzuna']]);
    wrapper.unmount();
  });

  it('shows read-only items as chips with no remove and no add', async () => {
    const wrapper = mountList({ modelValue: ['Read', 'Write'], readonly: true });
    await settle();

    expect(chips()).toEqual(['Read', 'Write']);
    expect(document.body.querySelector('.q-chip__icon--remove')).toBeNull();
    expect(document.body.querySelector('[data-chip-add]')).toBeNull();
    wrapper.unmount();
  });
});
