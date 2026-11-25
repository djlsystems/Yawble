<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue';
import { dimTitleBar } from '../titleBar';
import { useEscapeClose } from '../escapeClose';
import { usePhone } from '../phone';

// A dialog over the page. With `inline` the same content sits in a page instead (the Settings page's tabs): no
// backdrop, no header, nothing dimmed. `fill` (inline only): the panel is a flex column that takes the page's
// remaining height, so its content can fill it and scroll on its own (Settings > Skills).
// `full`: as wide and as tall as the window allows, its body a flex column whose content scrolls on its own (Logs).
const props = defineProps<{ title: string; wide?: boolean; inline?: boolean; fill?: boolean; full?: boolean }>();
const emit = defineEmits<{ close: [] }>();
onMounted(() => { if (!props.inline) dimTitleBar(true); });
onBeforeUnmount(() => { if (!props.inline) dimTitleBar(false); });
useEscapeClose(() => emit('close'), () => !props.inline);
// A phone (phone.ts): every dialog is a full-screen sheet, its header (title and a tap-sized Close) kept at the top
// and its body scrolling; there is no backdrop left to tap, so Close (or Escape) closes it.
const phone = usePhone();
</script>

<template>
  <section v-if="inline" class="inline-panel" :class="{ fill }" :aria-label="title"><slot /></section>
  <div v-else class="backdrop" :class="{ sheet: phone }" @mousedown.self="emit('close')">
    <section class="modal" :class="{ wide, full, sheet: phone }" role="dialog" aria-modal="true" :aria-label="title" :data-sheet="phone ? 'true' : undefined">
      <header class="row">
        <h2 class="grow">{{ title }}</h2>
        <button class="icon" data-test="modal-close" title="Close" aria-label="Close" @click="emit('close')">✕</button>
      </header>
      <div class="body"><slot /></div>
    </section>
  </div>
</template>

<style scoped>
.backdrop { position: fixed; inset: 0; background: var(--backdrop); display: flex; align-items: flex-start; justify-content: center; padding-top: var(--dialog-top); z-index: 10; }
.modal { background: var(--surface); border-radius: 8px; width: min(640px, calc(100vw - 32px)); max-height: calc(100vh - var(--dialog-top) - 24px); max-height: calc(100dvh - var(--dialog-top) - 24px); display: flex; flex-direction: column; box-shadow: 0 10px 40px var(--shadow); }
.modal.wide { width: min(980px, calc(100vw - 32px)); }
.modal.full { width: min(1320px, calc(100vw - 32px)); height: calc(100vh - var(--dialog-top) - 24px); height: calc(100dvh - var(--dialog-top) - 24px); }
.modal.full .body { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
/* flex: none: the header never shrinks in the modal's column. Shrunk (iOS Safari did, on a phone sheet with a tall body),
   its title and Close spilled over the body and the body's first lines sat under the title. */
header { flex: none; padding: 10px 16px; border-bottom: 1px solid var(--line); background: var(--surface); }
h2 { margin: 0; font-size: 16px; }
.body { padding: 12px 16px 16px; overflow: auto; }
.inline-panel { min-width: 0; }
.inline-panel.fill { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; }
/* A narrow window: normal flow, the Settings page scrolls. */
@media (max-width: 720px) { .inline-panel.fill { display: block; } }
/* The phone sheet: the whole screen (dvh: what the browser really shows), clear of a notch and the home bar. */
.backdrop.sheet { padding: 0; align-items: stretch; }
.modal.sheet, .modal.sheet.wide, .modal.sheet.full { width: 100%; height: 100dvh; max-height: none; border-radius: 0; box-shadow: none; }
.modal.sheet { overflow: hidden; }
.modal.sheet header { position: relative; z-index: 1; padding: calc(4px + env(safe-area-inset-top, 0px)) calc(6px + env(safe-area-inset-right, 0px)) 4px calc(14px + env(safe-area-inset-left, 0px)); }
.modal.sheet h2 { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
/* Close keeps its 44 px tap target, but without the button box: a bordered 44 px square around a small ✕ looked oversized
   next to the title. */
.modal.sheet header .icon { width: 44px; height: 44px; flex: none; border-color: transparent; background: transparent; font-size: 18px; }
.modal.sheet:not(.full) .body { flex: 1 1 0; min-height: 0; overflow: auto; }
.modal.sheet .body { padding: 12px calc(12px + env(safe-area-inset-right, 0px)) calc(16px + env(safe-area-inset-bottom, 0px)) calc(12px + env(safe-area-inset-left, 0px)); }
</style>
