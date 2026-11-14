import { ref } from 'vue';

/**
 * The width of every member's Activity tile on a team page: one value for all members, so dragging the bar between any
 * member's card and its activity resizes them all evenly. Kept across starts (blocked storage just means the default).
 */
export const ACTIVITY_MIN = 200;
export const ACTIVITY_DEFAULT = 360;
const KEY = 'yawble.memberActivityWidth';

const max = (): number => Math.max(ACTIVITY_MIN, Math.round((typeof window !== 'undefined' ? window.innerWidth : 1400) * 0.6));
export const clampActivity = (w: number): number => Math.min(max(), Math.max(ACTIVITY_MIN, Math.round(w)));

export const activityWidth = ref(clampActivity(((): number => {
  try { return Number(localStorage.getItem(KEY)) || ACTIVITY_DEFAULT; } catch { return ACTIVITY_DEFAULT; }
})()));

/** Sets the shared width (clamped) without saving: used while dragging. */
export function previewActivityWidth(w: number): void { activityWidth.value = clampActivity(w); }

/** Sets the shared width (clamped) and keeps it. */
export function setActivityWidth(w: number): void {
  activityWidth.value = clampActivity(w);
  try { localStorage.setItem(KEY, String(activityWidth.value)); } catch { /* not kept */ }
}
