import { onBeforeUnmount, onMounted } from 'vue';

/**
 * Escape closes the dialog on top. Every open dialog registers its close here, in the order it opened; one keydown
 * listener calls the newest one, so a dialog opened over another (a Connection over an Integration) closes first and
 * each press closes one layer. An Escape something else already handled (a menu, a field that clears itself) is left alone.
 */
const stack: (() => void)[] = [];
let listening = false;

function onKey(e: KeyboardEvent): void {
  if (e.key !== 'Escape' || e.defaultPrevented || e.isComposing || !stack.length) return;
  e.preventDefault();
  stack[stack.length - 1]!();
}

export function pushEscape(close: () => void): () => void {
  stack.push(close);
  if (!listening) { window.addEventListener('keydown', onKey); listening = true; }
  return () => {
    const i = stack.lastIndexOf(close);
    if (i >= 0) stack.splice(i, 1);
    if (!stack.length && listening) { window.removeEventListener('keydown', onKey); listening = false; }
  };
}

/** For a dialog component: Escape calls `close` while it is mounted (and `enabled()`, when given, says so). */
export function useEscapeClose(close: () => void, enabled: () => boolean = () => true): void {
  let release: (() => void) | null = null;
  onMounted(() => { if (enabled()) release = pushEscape(close); });
  onBeforeUnmount(() => { release?.(); release = null; });
}
