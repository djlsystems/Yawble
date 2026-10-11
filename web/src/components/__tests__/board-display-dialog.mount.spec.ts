// @vitest-environment happy-dom
//
// THE FIRST MOUNTED SPEC IN THIS REPOSITORY. The docblock above is the opt-in: the suite's default
// environment stays `node`, so the ~1,180 cases that do not need a DOM do not pay for one.
//
// The subject is chosen for CHEAPNESS, not importance. BoardDisplayDialog has no props beyond its
// v-model, no emits, no API client and no child components, so a failure here is a toolchain
// failure and cannot be mistaken for anything else.
//
// WHAT THIS ADDS OVER `boardSize.spec.ts`, which already tests every function used here: that spec
// proves the RULES are right. This one proves the COMPONENT IS WIRED TO THEM - that the sliders
// carry the lib's bounds rather than copies of the numbers, and that Reset's disabled state is
// `isDefault` rather than something that merely looks like it. A source scan can see the string
// `:disable="isDefault"`; it cannot see whether `isDefault` means anything.
import { afterEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import BoardDisplayDialog from '../BoardDisplayDialog.vue';
import {
  DefaultBoardSize,
  FeedDepthBounds,
  FeedWindowBounds,
  HeightBounds,
  WidthBounds,
} from '../../lib/boardSize';
import { useDisplayStore } from '../../stores/display';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

/** By aria-label, because there are four sliders and position is not a contract. */
function slider(label: string): HTMLElement {
  const found = bodyFind(`[aria-label="${label}"]`);

  if (!found) throw new Error(`no slider labelled "${label}" in the rendered dialog`);

  return found;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim().includes(label));

  if (!found) throw new Error(`no button labelled "${label}" in the rendered dialog`);

  return found as HTMLButtonElement;
}

describe('BoardDisplayDialog, mounted', () => {
  it('renders into the teleported portal rather than into the wrapper', async () => {
    const wrapper = await mountDialog(BoardDisplayDialog);

    // A QDialog teleports to <body>, so the card is NOT inside wrapper.element. A spec that asserts
    // on the wrapper finds a comment node and reads as "the dialog did not render" when it rendered
    // perfectly. This case exists to pin that down for whoever writes the next one.
    expect(wrapper.find('.board-display-card').exists()).toBe(false);
    expect(bodyText()).toContain('Board display');

    wrapper.unmount();
  });

  it.each([
    ['Card width in pixels', WidthBounds],
    ['Card height in pixels', HeightBounds],
    ['How many activity lines each card keeps', FeedDepthBounds],
    ["How many of the team's recent events the board fetches", FeedWindowBounds],
  ])('binds %s to the lib bounds rather than to literals', async (label, bounds) => {
    const wrapper = await mountDialog(BoardDisplayDialog);

    const element = slider(label);

    expect(element.getAttribute('aria-valuemin')).toBe(String(bounds.min));
    expect(element.getAttribute('aria-valuemax')).toBe(String(bounds.max));

    wrapper.unmount();
  });

  it('disables Reset at the default size and enables it once the size moves', async () => {
    setActivePinia(createPinia());

    const display = useDisplayStore();
    display.set({ ...DefaultBoardSize });

    const wrapper = await mountDialog(BoardDisplayDialog, {}, { pinia: false });

    expect(button('Reset').hasAttribute('disabled')).toBe(true);

    display.set({ width: DefaultBoardSize.width + 40 });
    await wrapper.vm.$nextTick();

    expect(button('Reset').hasAttribute('disabled')).toBe(false);

    wrapper.unmount();
  });
});
