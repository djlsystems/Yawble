import { getCurrentInstance, onBeforeUnmount, readonly, ref, type Ref } from 'vue';

/** The host's phone layout: a window below 760 px wide. At 760 px and wider nothing changes. */
export const NarrowQuery = '(max-width: 759.98px)';

type Media = Pick<MediaQueryList, 'matches'> & Partial<Pick<MediaQueryList, 'addEventListener' | 'removeEventListener' | 'addListener' | 'removeListener'>>;

/** True while the window is phone-wide, following resizes; false without matchMedia (the desktop layout). */
export function useNarrow(): Readonly<Ref<boolean>> {
  let m: Media | null = null;
  try { m = typeof matchMedia === 'function' ? (matchMedia(NarrowQuery) as Media) : null; } catch { m = null; }
  const narrow = ref(!!m?.matches);
  if (!m) return readonly(narrow);
  const media = m;
  const onChange = () => { narrow.value = !!media.matches; };
  if (media.addEventListener) media.addEventListener('change', onChange);
  else media.addListener?.(onChange);
  if (getCurrentInstance()) {
    onBeforeUnmount(() => {
      if (media.removeEventListener) media.removeEventListener('change', onChange);
      else media.removeListener?.(onChange);
    });
  }
  return readonly(narrow);
}
