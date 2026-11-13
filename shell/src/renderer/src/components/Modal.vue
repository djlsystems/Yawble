<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue';
import { dimTitleBar } from '../titleBar';
import { useEscapeClose } from '../escapeClose';

// A dialog over the page. With `inline` the same content sits in a page instead (the Settings page's tabs): no
// backdrop, no header, nothing dimmed. `fill` (inline only): the panel is a flex column that takes the page's
// remaining height, so its content can fill it and scroll on its own (Settings > Skills).
// `full`: as wide and as tall as the window allows, its body a flex column whose content scrolls on its own (Logs).
const props = defineProps<{ title: string; wide?: boolean; inline?: boolean; fill?: boolean; full?: boolean }>();
const emit = defineEmits<{ close: [] }>();
onMounted(() => { if (!props.inline) dimTitleBar(true); });
onBeforeUnmount(() => { if (!props.inline) dimTitleBar(false); });
useEscapeClose(() => emit('close'), () => !props.inline);
</script>

<template>
  <section v-if="inline" class="inline-panel" :class="{ fill }" :aria-label="title"><slot /></section>
  <div v-else class="backdrop" @mousedown.self="emit('close')">
    <section class="modal" :class="{ wide, full }" role="dialog" :aria-label="title">
      <header class="row">
        <h2 class="grow">{{ title }}</h2>
        <button class="icon" data-test="modal-close" title="Close" @click="emit('close')">✕</button>
      </header>
      <div class="body"><slot /></div>
    </section>
  </div>
</template>

<style scoped>
.backdrop { position: fixed; inset: 0; background: rgba(20, 26, 36, .35); display: flex; align-items: flex-start; justify-content: center; padding-top: var(--dialog-top); z-index: 10; }
.modal { background: #fff; border-radius: 8px; width: min(640px, calc(100vw - 32px)); max-height: calc(100vh - var(--dialog-top) - 24px); display: flex; flex-direction: column; box-shadow: 0 10px 40px rgba(0,0,0,.2); }
.modal.wide { width: min(980px, calc(100vw - 32px)); }
.modal.full { width: min(1320px, calc(100vw - 32px)); height: calc(100vh - var(--dialog-top) - 24px); }
.modal.full .body { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
header { padding: 10px 16px; border-bottom: 1px solid var(--line); }
h2 { margin: 0; font-size: 16px; }
.body { padding: 12px 16px 16px; overflow: auto; }
.inline-panel { min-width: 0; }
.inline-panel.fill { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; }
/* A narrow window: normal flow, the Settings page scrolls. */
@media (max-width: 720px) { .inline-panel.fill { display: block; } }
</style>
