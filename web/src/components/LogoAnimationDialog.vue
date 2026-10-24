<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref } from 'vue';
import { CloseAfterMs, LeadInMs, LogoStage } from '../lib/logoAnimation';
import { productAddress, productMarkLarge, productSignOff, productTitle } from '../presentation/product';

/**
 * The logo animation, full screen: the mark at the middle of a small graph drawn in the Monitor's
 * style (`LogoStage`). It plays from the start each time it opens, after a moment of empty ground; Escape ends it, and so does the close
 * button, which appears once the picture has had `CloseAfterMs` to itself.
 */
const open = defineModel<boolean>({ required: true });

const canvas = ref<HTMLCanvasElement | null>(null);
const stage = ref<{ $el: HTMLElement } | null>(null);
let scene: LogoStage | null = null;
let observer: ResizeObserver | null = null;
const closeShown = ref(false);
let closeTimer: ReturnType<typeof setTimeout> | null = null;
let startTimer: ReturnType<typeof setTimeout> | null = null;
const reducedMotion = globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;

function loadMark(): HTMLImageElement | null {
  if (typeof Image === 'undefined') return null;
  const image = new Image();
  image.src = productMarkLarge;
  return image;
}

function begin() {
  if (!canvas.value || !stage.value) return;
  const ground = stage.value.$el;
  const css = getComputedStyle(canvas.value);
  scene = new LogoStage(canvas.value, {
    fontFamily: css.fontFamily || 'sans-serif',
    fontPx: parseFloat(css.fontSize) || 14,
    reducedMotion,
    mark: loadMark(),
    signOff: { name: `${productTitle}.`, line: productSignOff, address: productAddress },
  });
  scene.readWall(canvas.value);
  const fit = () => {
    if (!scene) return;
    scene.resize(ground.clientWidth, ground.clientHeight);
  };
  fit();
  if (typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(fit);
    observer.observe(ground);
  }
  scene.start();
}

function end() {
  if (startTimer !== null) clearTimeout(startTimer);
  startTimer = null;
  if (closeTimer !== null) clearTimeout(closeTimer);
  closeTimer = null;
  closeShown.value = false;
  observer?.disconnect();
  observer = null;
  scene?.destroy();
  scene = null;
}

/** Each opening plays from the start, after `LeadInMs` of empty ground: the dialog's content is made when it shows. */
async function shown() {
  end();
  await nextTick();
  startTimer = setTimeout(begin, LeadInMs);
  closeTimer = setTimeout(() => (closeShown.value = true), LeadInMs + CloseAfterMs);
}

onBeforeUnmount(end);
</script>

<template>
  <!-- The mark that opens it is a link home, so the click that opens it also navigates: kept open across that. -->
  <q-dialog v-model="open" maximized no-route-dismiss transition-show="fade" transition-hide="fade" @show="shown" @hide="end">
    <!-- The pointer is hidden while the picture plays, and back with the close button. -->
    <q-card
      ref="stage"
      class="logo-stage os-dialog-xl"
      :class="{ 'logo-stage-full': true, 'logo-stage--playing': !closeShown }"
      data-test="logo-animation"
    >
      <canvas ref="canvas" class="logo-canvas" role="img" :aria-label="productTitle" />
      <q-btn
        v-if="closeShown"
        class="logo-close"
        flat
        round
        dense
        icon="close"
        aria-label="Close"
        data-test="logo-animation-close"
        @click="open = false"
      />
    </q-card>
  </q-dialog>
</template>

<style scoped>
.logo-stage {
  position: relative;
  overflow: hidden;
  border-radius: 0;
  background: var(--os-monitor-wall-ground);
}

/* The whole screen. Not a width of the dialog's own: the scale's cap is for a dialog sitting on
   the page, and a maximized one does not sit on anything. */
.logo-stage.logo-stage-full {
  width: 100vw;
  height: 100vh;
  max-width: none;
}

.logo-stage--playing,
.logo-stage--playing * {
  cursor: none;
}

.logo-canvas {
  display: block;
  font-family: var(--os-font);
  font-size: 1rem;
}

.logo-close {
  position: absolute;
  top: 12px;
  right: 12px;
  color: var(--os-monitor-wall-ink);
  animation: logo-close-in 0.6s ease;
}

@keyframes logo-close-in {
  from {
    opacity: 0;
  }
}
</style>

<style>
/* Over the Concierge's button and panel (z-index 7000), as the full screen it is. */
.q-dialog:has(.logo-stage) {
  z-index: 7100;
}
</style>
