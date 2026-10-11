// @vitest-environment happy-dom
//
// THE FILTER BOX the tiled dialogs share: one input, the dialog's own attributes on it, and a clear
// that reads as '' - never null - so a dialog's matcher never sees null.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

import FilterText from '../FilterText.vue';
import { resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function mountBox(modelValue: string) {
  return mount(FilterText, {
    props: { modelValue },
    attrs: { 'data-thing-filter': '', placeholder: 'Filter: name', 'aria-label': 'Filter things', class: 'thing-filter' },
    attachTo: document.body,
  });
}

const emitted = (wrapper: ReturnType<typeof mountBox>) =>
  (wrapper.emitted('update:modelValue') ?? []).map((call) => call[0]);

describe('the shared filter box', () => {
  it('is one input carrying the dialog\'s own marker, class, placeholder and label', async () => {
    mountBox('');
    await settle();

    expect(document.body.querySelectorAll('input')).toHaveLength(1);
    const input = document.body.querySelector<HTMLInputElement>('input[data-thing-filter], [data-thing-filter] input')!;
    expect(input).not.toBeNull();
    expect(input.getAttribute('placeholder')).toBe('Filter: name');
    expect(document.body.querySelector('.thing-filter input')).toBe(input);
    expect(document.body.querySelector('[aria-label="Filter things"]')).not.toBeNull();
  });

  it('emits what is typed', async () => {
    const wrapper = mountBox('');
    await settle();

    const input = document.body.querySelector<HTMLInputElement>('input')!;
    input.value = 'Job';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();

    expect(emitted(wrapper)).toEqual(['Job']);
  });

  it('reads a clear as an empty string, never null', async () => {
    const wrapper = mountBox('job');
    await settle();

    document.body.querySelector<HTMLElement>('.q-field__focusable-action')!.click();
    await settle();

    expect(emitted(wrapper)).toEqual(['']);
  });
});
