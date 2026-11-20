<script setup lang="ts">
// B115: a chat's Rename asked by the cloud page around the embedded remote view (embed.ts, chatAction rename). The
// sidebar renames in its row; embedded there is no sidebar, so the new title is asked here. Enter or Rename = the trimmed
// title, Escape, Cancel or a click on the backdrop = cancel (null).
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';

const props = defineProps<{ title: string }>();
const emit = defineEmits<{ done: [title: string | null] }>();

const text = ref(props.title);
const box = ref<HTMLInputElement | null>(null);

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); emit('done', null); }
}
onMounted(() => { document.addEventListener('keydown', onKey, true); void nextTick(() => box.value?.select()); });
onBeforeUnmount(() => document.removeEventListener('keydown', onKey, true));

function save() {
  const t = text.value.trim();
  emit('done', t || null);
}
</script>

<template>
  <div class="rename-scrim" data-test="embed-rename-backdrop" @mousedown.self="emit('done', null)">
    <form class="rename" role="dialog" aria-modal="true" aria-label="Rename chat" data-test="embed-rename" @submit.prevent="save">
      <label class="lbl" for="embed-rename-title">Rename chat</label>
      <input id="embed-rename-title" ref="box" v-model="text" data-test="embed-rename-input" maxlength="200" />
      <div class="buttons">
        <button type="button" data-test="embed-rename-cancel" @click="emit('done', null)">Cancel</button>
        <button type="submit" class="primary" data-test="embed-rename-ok" :disabled="!text.trim()">Rename</button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.rename-scrim {
  position: fixed; inset: 0; z-index: 1000; display: flex; align-items: flex-start; justify-content: center; background: var(--backdrop);
  padding: var(--dialog-top) 16px 16px;
}
.rename {
  width: min(440px, 100%); background: var(--surface); color: var(--ink); border: 1px solid var(--line); border-radius: 10px;
  box-shadow: 0 10px 40px var(--shadow); padding: 18px; display: flex; flex-direction: column; gap: 12px;
}
.lbl { font-weight: 600; font-size: 14px; }
.buttons { display: flex; justify-content: flex-end; gap: 8px; }
.buttons button { min-height: 36px; min-width: 88px; }
@media (max-width: 760px) {
  .rename-scrim { padding-top: 12vh; }
  .buttons button { flex: 1; min-width: 0; min-height: 44px; }
}
</style>
