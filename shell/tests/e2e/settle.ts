// Waits for the page to come to rest, for the specs that measure or screenshot what a click, a navigation or a resize
// left on screen. They waited a fixed time before (page.waitForTimeout); these wait for the condition that time stood for:
//
// - settled(page): the web fonts are loaded (document.fonts.ready), no finite CSS animation or transition is still
//   running (a sheet sliding in, a drawer, a fade; endless ones such as a spinner or a pulse are left alone), and the
//   layout held still for two frames in a row (the page's size, its scroll size and how many elements it has; a list
//   that is still filling in from the fake core changes these).
// - resized(page, w, h): setViewportSize, then the page has settled at the new size.
//
// It is not under phone/ on purpose: a spec that imports from phone/ joins the phone project (playwright.config.ts).
import { expect, type Page } from '@playwright/test';

/** The fonts are loaded, no finite animation or transition is running, and the layout held still for two frames. */
export async function settled(page: Page, timeout = 10_000): Promise<void> {
  await page.evaluate(async () => { await document.fonts.ready; });
  await expect.poll(() => page.evaluate(async () => {
    const running = document.getAnimations().filter((a) => {
      if (a.playState !== 'running') return false;
      const end = a.effect?.getComputedTiming().endTime;
      return typeof end === 'number' && Number.isFinite(end);
    }).length;
    if (running) return `${running} animation(s) running`;
    const frame = () => new Promise<void>((r) => requestAnimationFrame(() => r()));
    const shape = () => {
      const d = document.documentElement;
      return [innerWidth, innerHeight, d.scrollWidth, d.scrollHeight, document.body?.scrollHeight ?? 0, document.getElementsByTagName('*').length].join(',');
    };
    const a = shape(); await frame(); const b = shape(); await frame(); const c = shape();
    return a === b && b === c ? 'still' : `layout moving: ${a} / ${b} / ${c}`;
  }), { message: 'the page comes to rest', timeout, intervals: [50, 100, 200] }).toBe('still');
}

/** Sets the viewport (Playwright applies it before returning), then waits until the page has settled at that size. */
export async function resized(page: Page, width: number, height: number): Promise<void> {
  await page.setViewportSize({ width, height });
  await settled(page);
}
