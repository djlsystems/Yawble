import { ref } from 'vue';

/**
 * The height of every agent's output box on a team page: one value for all members, so dragging any member's edge
 * resizes them all evenly. Kept across starts (a blocked storage just means the default next time).
 */
export const OUTPUT_MIN = 48;
export const OUTPUT_DEFAULT = 120;
const KEY = 'yawble.agentOutput';

const max = (): number => Math.max(OUTPUT_MIN, Math.round((typeof window !== 'undefined' ? window.innerHeight : 900) * 0.7));
export const clampOutput = (h: number): number => Math.min(max(), Math.max(OUTPUT_MIN, Math.round(h)));

/** The height last chosen (dragged or kept), before the window's limit: a rotation back to a taller window restores it. */
let wanted = ((): number => {
  try { return Number(localStorage.getItem(KEY)) || OUTPUT_DEFAULT; } catch { return OUTPUT_DEFAULT; }
})();

export const outputHeight = ref(clampOutput(wanted));

/** Sets the shared height (clamped) without saving: used while dragging. */
export function previewOutputHeight(h: number): void { wanted = h; outputHeight.value = clampOutput(h); }

/** Sets the shared height (clamped) and keeps it. */
export function setOutputHeight(h: number): void {
  wanted = h;
  outputHeight.value = clampOutput(h);
  try { localStorage.setItem(KEY, String(outputHeight.value)); } catch { /* not kept */ }
}

/**
 * Card 334: the limit (70% of the window's height) follows the window. A rotation or resize clamps the chosen height
 * again to the new limit, without saving it, so a phone or iPad turned to landscape and back gets its height back.
 */
export function followWindowHeight(): void { outputHeight.value = clampOutput(wanted); }
if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') window.addEventListener('resize', followWindowHeight);
