import { computed, type ComputedRef, type Ref } from 'vue';
import { OUTPUT_DEFAULT, OUTPUT_MIN, clampOutput, outputHeight, previewOutputHeight, setOutputHeight } from './agentOutputHeight';
import { TILE_MAX_H, TILE_MAX_W, TILE_MIN_H, TILE_MIN_W, previewTileSize, renderedWidth, resetTileSize, setTileSize, tileSize } from './teamTileSize';
import { ACTIVITY_DEFAULT, ACTIVITY_MIN, activityWidth, clampActivity, previewActivityWidth, setActivityWidth } from './memberActivityWidth';

/**
 * What TileGrip resizes: a shared {w, h} with preview / keep / reset. The Teams list binds it to its tile size; the
 * team board binds it to the shared output-box height of the member cards (height only, the cards are one column).
 */
export interface GripStore {
  /** The noun in the aria-label ("Resize all <noun>: height") and the data-test prefix. */
  noun: string;
  testId: string;
  size: Readonly<Ref<{ w: number; h: number }>> | ComputedRef<{ w: number; h: number }>;
  /** The width as rendered (a drag starts from it). */
  width: Readonly<Ref<number>> | ComputedRef<number>;
  limits: { minW: number; maxW: number; minH: number; maxH: number };
  preview: (s: { w: number; h: number }) => void;
  commit: () => void;
  reset: () => void;
  /** Sets the size and keeps it (keyboard). */
  set: (s: { w: number; h: number }) => void;
}

export const teamTilesStore: GripStore = {
  noun: 'team tiles', testId: 'tile', size: tileSize, width: renderedWidth,
  limits: { minW: TILE_MIN_W, maxW: TILE_MAX_W, minH: TILE_MIN_H, maxH: TILE_MAX_H },
  preview: previewTileSize, commit: () => setTileSize(tileSize.value), reset: resetTileSize, set: setTileSize,
};

/**
 * The members' Activity tiles on the team board: their shared width (memberActivityWidth.ts). The bar sits to the left
 * of the tile, so TileGrip runs it reversed: dragging right makes the activity narrower.
 */
export const memberActivityStore: GripStore = {
  noun: 'member activity', testId: 'member-activity', size: computed(() => ({ w: activityWidth.value, h: 0 })), width: activityWidth,
  limits: { minW: ACTIVITY_MIN, maxW: clampActivity(Infinity), minH: 0, maxH: 0 },
  preview: (s) => previewActivityWidth(s.w), commit: () => setActivityWidth(activityWidth.value), reset: () => setActivityWidth(ACTIVITY_DEFAULT), set: (s) => setActivityWidth(s.w),
};

const memberSize = computed(() => ({ w: 0, h: outputHeight.value }));
/** The member cards on the team board: the shared output-box height (agentOutputHeight.ts); the width is not adjustable. */
export const memberCardsStore: GripStore = {
  noun: 'member cards', testId: 'member', size: memberSize, width: computed(() => 0),
  limits: { minW: 0, maxW: 0, minH: OUTPUT_MIN, maxH: clampOutput(Infinity) },
  preview: (s) => previewOutputHeight(s.h), commit: () => setOutputHeight(outputHeight.value), reset: () => setOutputHeight(OUTPUT_DEFAULT), set: (s) => setOutputHeight(s.h),
};
