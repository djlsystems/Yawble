// @vitest-environment happy-dom
//
// THEMES, MOUNTED: every change is on screen at once through the appearance store (App.vue paints
// what it resolves), Save sends the whole record, Cancel puts back what was saved, a theme of the
// person's own is made from what is on screen and coloured, warned about when hard to read, and
// deleting the one in use falls back to the preset it started from.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { saveAppearance } = vi.hoisted(() => ({ saveAppearance: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  saveAppearance,
}));

import { flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import ThemesDialog from '../ThemesDialog.vue';
import { useAppearanceStore } from '../../stores/appearance';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

function find(test: string): HTMLElement {
  const found = bodyFind(`[data-test="${test}"]`);
  if (!found) throw new Error(`no [data-test="${test}"] in Themes`);
  return found;
}

async function click(test: string) {
  find(test).click();
  await flushPromises();
}

async function open(saved: Record<string, unknown> | null = { selected: 'light' }) {
  setActivePinia(createPinia());
  const store = useAppearanceStore();
  store.$patch({ record: saved, instanceDefault: 'auto', systemDark: false });
  const wrapper = await mountDialog(ThemesDialog, {}, { pinia: false });
  return { wrapper, store };
}

beforeEach(() => {
  localStorage.clear();
  saveAppearance.mockReset();
  saveAppearance.mockResolvedValue(new Response(null, { status: 204 }));
});

afterEach(resetBody);

describe('Themes', () => {
  it('offers Match system and every built-in theme, and shows a choice at once before it is saved', async () => {
    const { wrapper, store } = await open();

    for (const label of ['Match system', 'Light', 'Dark', 'High contrast light', 'High contrast dark', 'Soft dark']) {
      expect(bodyText()).toContain(label);
    }

    await click('theme-choice-high-contrast-dark');

    expect(store.resolved).toMatchObject({ preset: 'high-contrast-dark', dark: true });
    expect(store.record).toEqual({ selected: 'light' });
    expect(saveAppearance).not.toHaveBeenCalled();

    await click('themes-save');

    expect(saveAppearance).toHaveBeenCalledWith({ selected: 'high-contrast-dark' });
    expect(store.record).toEqual({ selected: 'high-contrast-dark' });
    expect(store.draft).toBeNull();

    wrapper.unmount();
  });

  it('puts back what was saved on Cancel', async () => {
    const { wrapper, store } = await open({ selected: 'dark', textSize: 120 });

    await click('theme-choice-soft-dark');
    expect(store.resolved.preset).toBe('soft-dark');

    await click('themes-cancel');

    expect(store.draft).toBeNull();
    expect(store.resolved).toMatchObject({ preset: 'dark', textSize: 120 });
    expect(saveAppearance).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('changes the text size by the slider, a step at a time, on any theme', async () => {
    const { wrapper, store } = await open();

    const slider = find('theme-text-size');
    slider.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', keyCode: 39, bubbles: true }));
    slider.dispatchEvent(new KeyboardEvent('keyup', { key: 'ArrowRight', keyCode: 39, bubbles: true }));
    await flushPromises();

    expect(store.resolved.textSize).toBe(110);
    expect(find('theme-text-size-figure').textContent).toBe('110%');

    wrapper.unmount();
  });

  it('makes a theme of the person\'s own from what is on screen, colours it, and warns when text is hard to read', async () => {
    const { wrapper, store } = await open({ selected: 'soft-dark' });

    expect(find('theme-colours-note').textContent).toContain('Colours are set on your own themes');

    await click('theme-new');
    const name = find('theme-new-name') as HTMLInputElement;
    const input = name.tagName === 'INPUT' ? name : name.querySelector('input')!;
    input.value = 'Big Screen';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await flushPromises();
    await click('theme-new-create');

    expect(store.resolved).toMatchObject({ selected: 'custom:big-screen', preset: 'soft-dark' });
    expect(bodyFind('[data-test="theme-contrast-warning"]')).toBeNull();

    const text = find('theme-colour-text') as HTMLInputElement;
    text.value = '#3a3632';
    text.dispatchEvent(new Event('input', { bubbles: true }));
    await flushPromises();

    expect(store.resolved.custom?.colours?.text).toBe('#3a3632');
    expect(find('theme-contrast-warning').textContent).toContain('4.5:1 is recommended for reading');

    await click('themes-save');

    const sent = saveAppearance.mock.calls[0]![0];
    expect(sent.selected).toBe('custom:big-screen');
    expect(sent.themes).toEqual([
      expect.objectContaining({ id: 'big-screen', name: 'Big Screen', basedOn: 'soft-dark' }),
    ]);
    expect(sent.themes[0].colours.text).toBe('#3a3632');

    wrapper.unmount();
  });

  it('falls back to the preset a theme started from when the theme in use is deleted', async () => {
    const { wrapper, store } = await open({
      selected: 'custom:evening',
      themes: [{ id: 'evening', name: 'Evening', basedOn: 'high-contrast-dark' }],
    });

    expect(bodyText()).toContain('Evening');

    await click('theme-delete-evening');

    expect(store.resolved).toMatchObject({ selected: 'high-contrast-dark', custom: null });
    expect(bodyFind('[data-test="theme-choice-custom-evening"]')).toBeNull();

    wrapper.unmount();
  });

  it('shows a refusal in the server\'s words and keeps the dialog open', async () => {
    saveAppearance.mockRejectedValue(new Error('`textSize` must be a percentage from 90 to 160, in steps of 10.'));
    const { wrapper } = await open();

    await click('theme-choice-dark');
    await click('themes-save');

    expect(find('themes-error').textContent).toContain('`textSize` must be a percentage');
    expect(bodyFind('[data-test="themes-save"]')).not.toBeNull();

    wrapper.unmount();
  });
});
