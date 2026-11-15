import { getCurrentInstance, onBeforeUnmount, readonly, ref, type Ref } from 'vue';

/**
 * The phone layout (B037): a window below 760 px wide. The screens' scoped styles use the same width in
 * `@media (max-width: 759.98px)`; this is for what CSS cannot do (one board lane at a time, the list or the file,
 * entries as cards). At 760 px and wider nothing changes.
 */
export const NarrowQuery = '(max-width: 759.98px)';

type Media = Pick<MediaQueryList, 'matches'> & Partial<Pick<MediaQueryList, 'addEventListener' | 'removeEventListener' | 'addListener' | 'removeListener'>>;

function media(): Media | null {
  try { return typeof matchMedia === 'function' ? (matchMedia(NarrowQuery) as Media) : null; } catch { return null; }
}

/**
 * True while the window is phone-wide, following it as it is resized. In a component the listener goes when the
 * component unmounts; without matchMedia (or when it throws) it is always false: the desktop layout.
 */
export function useNarrow(): Readonly<Ref<boolean>> {
  const m = media();
  const narrow = ref(!!m?.matches);
  if (!m) return readonly(narrow);
  const onChange = () => { narrow.value = !!m.matches; };
  if (m.addEventListener) m.addEventListener('change', onChange);
  else m.addListener?.(onChange);
  if (getCurrentInstance()) {
    onBeforeUnmount(() => {
      if (m.removeEventListener) m.removeEventListener('change', onChange);
      else m.removeListener?.(onChange);
    });
  }
  return readonly(narrow);
}

/** A horizontal swipe: at least this far, and clearly more across than down. */
export const SwipeMin = 50;

/**
 * Swipe left or right on an element with pointer events (touch, pen or mouse): `onSwipe(-1)` for a swipe to the left
 * (the next one), `onSwipe(1)` to the right (the previous one). A mostly vertical move scrolls and is not a swipe.
 */
export function useSwipe(onSwipe: (direction: -1 | 1) => void) {
  let start: { x: number; y: number; id: number } | null = null;
  return {
    down(e: PointerEvent) { if (e.isPrimary !== false) start = { x: e.clientX, y: e.clientY, id: e.pointerId }; },
    up(e: PointerEvent) {
      const s = start;
      start = null;
      if (!s || s.id !== e.pointerId) return;
      const dx = e.clientX - s.x;
      const dy = e.clientY - s.y;
      if (Math.abs(dx) >= SwipeMin && Math.abs(dx) > Math.abs(dy) * 1.5) onSwipe(dx < 0 ? -1 : 1);
    },
    cancel() { start = null; },
  };
}
