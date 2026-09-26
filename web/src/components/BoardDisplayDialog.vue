<script setup lang="ts">
import { computed } from 'vue';
import { useDisplayStore } from '../stores/display';
import {
  DefaultBoardSize,
  FeedDepthBounds,
  FeedWindowBounds,
  HeightBounds,
  WidthBounds,
} from '../lib/boardSize';

/**
 * How big this viewer wants the board's member cards.
 *
 * In the ACCOUNT MENU rather than the ribbon, and that is a decision rather than convenience. Card
 * size is a per-viewer display preference - not team data, not an administrative act - and the
 * ribbon's groups are NEW / TEAMS / ACTIVE TEAM / ADMIN, none of which it belongs to. A ribbon
 * action also has to be added in three separate files or it half-exists. The account menu already
 * hosts *User profile*, which is the same kind of thing.
 *
 * It applies LIVE, with no Save. There is nothing to submit - the value is in this browser and
 * nowhere else - and a size you cannot see while choosing it is a size you have to guess at. The
 * store persists on every change, so closing the dialog is not a commit and cancelling is not a
 * thing that could exist.
 */
const open = defineModel<boolean>({ required: true });

const display = useDisplayStore();

/**
 * Bound with an explicit getter and setter rather than `v-model` straight onto the store, so every
 * change goes through `set()` and is clamped and persisted. Writing `display.width` directly would
 * put an unclamped number into state and never reach storage.
 */
const width = computed({
  get: () => display.width,
  set: (value: number) => display.set({ width: value }),
});

const height = computed({
  get: () => display.height,
  set: (value: number) => display.set({ height: value }),
});

const feedDepth = computed({
  get: () => display.feedDepth,
  set: (value: number) => display.set({ feedDepth: value }),
});

const feedWindow = computed({
  get: () => display.feedWindow,
  set: (value: number) => display.set({ feedWindow: value }),
});

const isDefault = computed(
  () =>
    display.width === DefaultBoardSize.width
    && display.height === DefaultBoardSize.height
    && display.feedDepth === DefaultBoardSize.feedDepth
    && display.feedWindow === DefaultBoardSize.feedWindow,
);
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="board-display-card os-dialog-sm">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Board display</div>
        <div class="text-caption os-text-muted">
          How big each member's card is on this device. Saved in this browser only — everyone else
          keeps their own.
        </div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <!-- Sliders rather than number boxes. This is a size, and a size is chosen by looking at
             it: dragging shows the board reflow underneath, where typing 640 and tabbing away
             makes a person guess. It also removes the empty-box case entirely - a slider cannot
             produce NaN, which the clamp guards against anyway but should not have to meet. -->
        <div>
          <div class="row items-center justify-between">
            <div class="text-body2">Card width</div>
            <div class="text-caption os-text-muted">{{ width }}px</div>
          </div>
          <q-slider
            v-model="width"
            :min="WidthBounds.min"
            :max="WidthBounds.max"
            :step="10"
            label
            :label-value="`${width}px`"
            aria-label="Card width in pixels"
          />
          <div class="text-caption os-text-muted">
            A maximum, not a fixed size — a card never grows wider than the screen, so this stays
            usable on a phone whatever it is set to.
          </div>
        </div>

        <div>
          <div class="row items-center justify-between">
            <div class="text-body2">Card height</div>
            <div class="text-caption os-text-muted">{{ height }}px</div>
          </div>
          <q-slider
            v-model="height"
            :min="HeightBounds.min"
            :max="HeightBounds.max"
            :step="20"
            label
            :label-value="`${height}px`"
            aria-label="Card height in pixels"
          />
          <div class="text-caption os-text-muted">
            Each card scrolls its own activity, newest first, so a busy member does not stretch
            the ones beside it.
          </div>
        </div>

        <div>
          <div class="row items-center justify-between">
            <div class="text-body2">Events per member</div>
            <div class="text-caption os-text-muted">{{ feedDepth }}</div>
          </div>
          <q-slider
            v-model="feedDepth"
            :min="FeedDepthBounds.min"
            :max="FeedDepthBounds.max"
            :step="5"
            label
            :label-value="`${feedDepth}`"
            aria-label="How many activity lines each card keeps"
          />
          <!-- Says what it CANNOT do, because the obvious reading is wrong. Raising this does not
               recover lines already dropped - the store trims on every append - so it takes effect
               on what arrives next. And the card was never the history: the message log keeps
               everything, and `harness status <member>` answers a deeper tail for one member. -->
          <div class="text-caption os-text-muted">
            How many lines each card keeps. Applies to new activity — raising it does not bring back
            lines already dropped, and the full history is in the log either way.
          </div>
        </div>

        <div>
          <div class="row items-center justify-between">
            <div class="text-body2">Team events fetched</div>
            <div class="text-caption os-text-muted">{{ feedWindow }}</div>
          </div>
          <q-slider
            v-model="feedWindow"
            :min="FeedWindowBounds.min"
            :max="FeedWindowBounds.max"
            :step="50"
            label
            :label-value="`${feedWindow}`"
            aria-label="How many of the team's recent events the board fetches"
          />
          <!-- The distinction from the slider above is the whole reason this one exists, so the
               hint leads with it. That one trims what a card KEEPS; this decides what ARRIVES, and
               no amount of the former rescues a member the fetch never returned. -->
          <div class="text-caption os-text-muted">
            How far back the board looks, across the whole team. Cards filter this to their own, so
            a member that has been quiet while others were busy needs a larger number to appear at
            all — raising the setting above cannot help if nothing was fetched.
          </div>
        </div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Reset" :disable="isDefault" @click="display.reset()" />
        <q-btn flat no-caps label="Done" color="primary" @click="open = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
</style>
