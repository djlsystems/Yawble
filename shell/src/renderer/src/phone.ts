import { getCurrentScope, onScopeDispose, readonly, ref, type Ref } from 'vue';

// The phone layout: below 760 px wide the screens change to one column (the sidebar becomes a drawer, a dialog a
// full-screen sheet, Settings one section per screen); at 760 px and wider nothing changes. Components ask usePhone()
// and put a `phone` class on their root, so the layout is plain CSS keyed on that class (and testable without a real
// media query engine). The same width is in styles.css's @media rules (PHONE_QUERY).

/** The first width that is not a phone: at this width and wider, the desktop layout. */
export const PHONE_BREAKPOINT = 760;
/** The media query of a phone (anything narrower than PHONE_BREAKPOINT; .98 so a fractional 759.5 px is still a phone). */
export const PHONE_QUERY = `(max-width: ${PHONE_BREAKPOINT - 0.02}px)`;

type Media = Pick<MediaQueryList, 'matches'> & Partial<Pick<MediaQueryList, 'addEventListener' | 'removeEventListener'>>;

function media(): Media | null {
  try { return typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia(PHONE_QUERY) : null; } catch { return null; }
}

/** Whether the window is phone-width now, following it as it changes; the listener goes with the component's scope. */
export function usePhone(): Readonly<Ref<boolean>> {
  const m = media();
  const phone = ref(!!m?.matches);
  if (m?.addEventListener) {
    // Read from the list itself, not the event (whatever calls the listener, the list says the answer now).
    const on = () => { phone.value = !!m.matches; };
    m.addEventListener('change', on);
    if (getCurrentScope()) onScopeDispose(() => m.removeEventListener?.('change', on));
  }
  return readonly(phone);
}

/**
 * How much of the window's bottom the on-screen keyboard covers, in px (0 with no keyboard, or with no visualViewport):
 * the layout viewport's height less the visual viewport's bottom edge. The Concierge pads its bottom by this, so the
 * message box stays above the keyboard.
 */
export function keyboardInset(win: Pick<Window, 'innerHeight'> & { visualViewport?: Pick<VisualViewport, 'height' | 'offsetTop'> | null } = window): number {
  const vv = win.visualViewport;
  if (!vv) return 0;
  return Math.max(0, Math.round(win.innerHeight - vv.height - vv.offsetTop));
}

/** keyboardInset() as a ref, following the visual viewport's resize and scroll while the component is mounted. */
export function useKeyboardInset(): Readonly<Ref<number>> {
  const inset = ref(0);
  if (typeof window === 'undefined') return readonly(inset);
  const vv = window.visualViewport;
  const update = () => { inset.value = keyboardInset(window); };
  update();
  if (vv && typeof vv.addEventListener === 'function') {
    vv.addEventListener('resize', update);
    vv.addEventListener('scroll', update);
    if (getCurrentScope()) onScopeDispose(() => { vv.removeEventListener('resize', update); vv.removeEventListener('scroll', update); });
  }
  return readonly(inset);
}

// ---- tooltips by tap
// A title is a hover tooltip; a phone has no hover. On a phone a tap on something that only explains itself (a dot, a
// chip, a cut-off name: an element with a title that is not a control) shows its title in a small tip; the next tap,
// or a few seconds, hides it. A control keeps its tap for what it does (its label or aria-label says what that is).

const INTERACTIVE = 'button, a[href], input, select, textarea, label, summary, [role=button], [role=tab], [role=menuitem], [contenteditable=true]';
export const TAP_TIP_MS = 4000;

/** The element whose title a tap at `target` shows, or null (no title there, or the tap lands on a control). */
export function tapTitleTarget(target: EventTarget | null): HTMLElement | null {
  if (!(target instanceof Element)) return null;
  if (target.closest(INTERACTIVE)) return null;
  const el = target.closest<HTMLElement>('[title]');
  return el && el.getAttribute('title')?.trim() ? el : null;
}

/** Turns tap tips on for `doc` while `isPhone()` says so; returns the function that turns them off. */
export function installTapTitles(isPhone: () => boolean, doc: Document = document): () => void {
  let tip: HTMLElement | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  const hide = () => {
    if (timer) { clearTimeout(timer); timer = null; }
    tip?.remove();
    tip = null;
  };
  const onClick = (e: Event) => {
    const had = !!tip;
    hide();
    if (!isPhone()) return;
    const el = tapTitleTarget(e.target);
    if (!el) return;
    if (had && el.dataset.tapTipShown === '1') { delete el.dataset.tapTipShown; return; } // a second tap on it: just hide
    for (const o of doc.querySelectorAll<HTMLElement>('[data-tap-tip-shown]')) delete o.dataset.tapTipShown;
    el.dataset.tapTipShown = '1';
    tip = doc.createElement('div');
    tip.className = 'tap-tip';
    tip.setAttribute('role', 'tooltip');
    tip.setAttribute('data-test', 'tap-tip');
    tip.textContent = el.getAttribute('title');
    const r = el.getBoundingClientRect();
    const view = doc.defaultView;
    const width = view?.innerWidth ?? 360;
    tip.style.left = `${Math.max(8, Math.min(r.left, width - 268))}px`;
    tip.style.top = `${Math.round(r.bottom + 6)}px`;
    doc.body.appendChild(tip);
    timer = setTimeout(hide, TAP_TIP_MS);
  };
  doc.addEventListener('click', onClick, true);
  return () => { doc.removeEventListener('click', onClick, true); hide(); };
}
