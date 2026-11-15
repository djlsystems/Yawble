// B037 verify: what a phone screen is checked for. Run in the page: horizontal overflow (the page wider than the window),
// controls that are visible but off the screen's left or right edge, and controls smaller than 44 x 44 px.
import type { Page } from '@playwright/test';

export const MIN_TARGET = 44;

export interface Control { what: string; test: string | null; label: string; x: number; y: number; w: number; h: number; inScroller: boolean }
export interface Audit {
  width: number; height: number; scrollWidth: number; bodyScrollWidth: number; overflow: boolean;
  /** Elements whose right edge is past the window (the cause of an overflow), outermost first, at most 8. */
  wide: string[];
  offScreen: Control[]; small: Control[]; controls: number;
}

export async function audit(page: Page, scope = 'body'): Promise<Audit> {
  return page.evaluate(([scope, min]) => {
    const W = window.innerWidth;
    const H = window.innerHeight;
    const SEL = 'button, a[href], input:not([type=hidden]), select, textarea, summary, [role=button], [role=tab], [role=menuitem], [role=switch], [role=checkbox]';
    const describe = (el: Element) => {
      const t = el.getAttribute('data-test');
      const cls = typeof (el as HTMLElement).className === 'string' ? (el as HTMLElement).className.trim().split(/\s+/).slice(0, 2).join('.') : '';
      return `${el.tagName.toLowerCase()}${t ? `[data-test=${t}]` : ''}${cls ? `.${cls}` : ''}`;
    };
    const visible = (el: Element) => {
      if (el.closest('[inert], [aria-hidden=true]')) return false;
      if (!(el as HTMLElement).checkVisibility?.({ checkOpacity: true, checkVisibilityCSS: true } as any)) return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    };
    // An element inside something that scrolls sideways (a tab strip, a table, a code view) may sit past the edge on
    // purpose: noted, but not an off-screen fault by itself.
    const inScroller = (el: Element) => {
      for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
        const s = getComputedStyle(p);
        if (/(auto|scroll)/.test(s.overflowX) && p.scrollWidth > p.clientWidth + 1) return true;
      }
      return false;
    };
    // A control clipped away by an ancestor that hides overflow (e.g. a closed panel 0 px tall) is not on screen.
    const clippedAway = (el: Element, r: DOMRect) => {
      for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
        const s = getComputedStyle(p);
        if (s.overflowX === 'visible' && s.overflowY === 'visible') continue;
        const pr = p.getBoundingClientRect();
        if (r.right <= pr.left || r.left >= pr.right || r.bottom <= pr.top || r.top >= pr.bottom) return true;
      }
      return false;
    };
    const root = document.querySelector(scope) ?? document.body;
    const offScreen: any[] = [];
    const small: any[] = [];
    let controls = 0;
    for (const el of root.querySelectorAll(SEL)) {
      if (!visible(el)) continue;
      const r = el.getBoundingClientRect();
      if (clippedAway(el, r)) continue;
      // Covered by something else (a sheet or the drawer over the page): not what a tap there reaches.
      const cx = Math.min(Math.max(r.left + r.width / 2, 0), W - 1);
      const cy = r.top + r.height / 2;
      if (cy >= 0 && cy < H) {
        const top = document.elementFromPoint(cx, cy);
        const lab0 = el.closest('label');
        if (top && !el.contains(top) && !top.contains(el) && !(lab0 && lab0.contains(top))) continue;
      }
      // A control wrapped in a label counts at the label's size (the label is what is tapped).
      const lab = (el as HTMLInputElement).labels?.[0] ?? el.closest('label');
      const hit = lab && visible(lab) ? lab.getBoundingClientRect() : r;
      controls++;
      const c = {
        what: describe(el), test: el.getAttribute('data-test'),
        label: ((el.getAttribute('aria-label') || (el as HTMLElement).innerText || el.getAttribute('title') || (el as HTMLInputElement).placeholder || '') + '').trim().replace(/\s+/g, ' ').slice(0, 50),
        x: Math.round(r.left), y: Math.round(r.top), w: Math.round(Math.max(r.width, hit.width)), h: Math.round(Math.max(r.height, hit.height)), inScroller: inScroller(el),
      };
      if (r.right > W + 1 || r.left < -1) offScreen.push(c);
      if (c.w < (min as number) - 0.5 || c.h < (min as number) - 0.5) small.push(c);
    }
    const wide: string[] = [];
    for (const el of document.body.querySelectorAll('*')) {
      if (wide.length >= 8) break;
      if (!visible(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.right > W + 1 && !wide.some((w) => false && w)) {
        if (el.parentElement && el.parentElement.getBoundingClientRect().right > W + 1 && wide.length) continue;
        wide.push(`${describe(el)} right=${Math.round(r.right)}`);
      }
    }
    const scrollWidth = document.documentElement.scrollWidth;
    const bodyScrollWidth = document.body.scrollWidth;
    return { width: W, height: H, scrollWidth, bodyScrollWidth, overflow: scrollWidth > W || bodyScrollWidth > W, wide, offScreen, small, controls };
  }, [scope, MIN_TARGET] as const);
}
