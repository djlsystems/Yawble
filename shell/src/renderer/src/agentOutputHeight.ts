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

export const outputHeight = ref(clampOutput(((): number => {
  try { return Number(localStorage.getItem(KEY)) || OUTPUT_DEFAULT; } catch { return OUTPUT_DEFAULT; }
})()));

/** Sets the shared height (clamped) without saving: used while dragging. */
export function previewOutputHeight(h: number): void { outputHeight.value = clampOutput(h); }

/** Sets the shared height (clamped) and keeps it. */
export function setOutputHeight(h: number): void {
  outputHeight.value = clampOutput(h);
  try { localStorage.setItem(KEY, String(outputHeight.value)); } catch { /* not kept */ }
}
