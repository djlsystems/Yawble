<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import FolderAskCard from './FolderAskCard.vue';
import { usePhone } from '../phone';
import { FOLDER_ASKS_CHANGED, folderAnswerText, folderAskMember, pendingFolderAsks, type FolderAsk, type FolderAskChoice } from '../folderAsks';

/**
 * The waiting folder asks in Needs you (folder.asks, pending only): one card per ask. Refreshed on
 * folder.asksChanged (an ask raised or answered, here or in another view) and on a slow poll. `banner`: the window-wide
 * strip above every view (App.vue); otherwise a plain list (the Board's Needs You lane, a member's pane), filtered by
 * team and member names. After an answer the item goes and a short line says what it did ("applies from its next run")
 * until closed. An older core without folder.asks shows nothing.
 */
const props = defineProps<{ api: YawbleApi; pollMs?: number; banner?: boolean; team?: string; member?: string }>();
const emit = defineEmits<{ count: [n: number] }>();
const phone = usePhone();

const asks = ref<FolderAsk[]>([]);
const shown = computed(() => pendingFolderAsks(asks.value)
  .filter((a) => (!props.team || a.team === props.team) && (!props.member || folderAskMember(a) === props.member)));
watch(() => shown.value.length, (n) => emit('count', n), { immediate: true });
/** The last answer given here, shown until closed. */
const done = ref<string | null>(null);

let loading = false;
let again = false;
async function load() {
  if (loading) { again = true; return; }
  loading = true;
  try {
    do {
      again = false;
      try {
        const r = await props.api?.call<FolderAsk[]>('folder.asks', {});
        asks.value = Array.isArray(r) ? r : [];
      } catch { asks.value = []; } // an older core, or a remote view that may not read them: nothing to show
    } while (again);
  } finally { loading = false; }
}

function answered(a: FolderAsk, choice: FolderAskChoice) {
  done.value = folderAnswerText(a, choice);
  void load();
}

let timer: ReturnType<typeof setInterval> | null = null;
let off: (() => void) | null = null;
onMounted(() => {
  void load();
  off = props.api?.onEvent?.((e) => { if (e.event === FOLDER_ASKS_CHANGED) void load(); }) ?? null;
  timer = setInterval(load, Math.max(props.pollMs ?? 5000, 5000));
});
onBeforeUnmount(() => { off?.(); if (timer) clearInterval(timer); });

defineExpose({ load });
</script>

<template>
  <section v-if="shown.length || done" class="folder-asks" :class="{ banner, phone }" data-test="folder-asks">
    <p v-if="banner && shown.length" class="title"><b>Needs you</b>: {{ shown.length === 1 ? 'an agent asks' : `${shown.length} agents ask` }} for a folder outside its clone</p>
    <FolderAskCard v-for="a in shown" :key="a.id" :ask="a" :api="api" @answered="answered" />
    <p v-if="done" class="done small" data-test="folder-ask-answered" role="status">
      {{ done }} <button class="link small" data-test="folder-ask-answered-close" @click="done = null">Close</button>
    </p>
  </section>
</template>

<style scoped>
.folder-asks { display: flex; flex-direction: column; gap: 8px; }
.banner { padding: 8px 18px; border-bottom: 1px solid var(--line); border-left: 3px solid var(--warn); background: var(--side); }
.title { margin: 0; font-size: 13px; }
.banner.phone { padding: 8px 8px; }
.done { margin: 0; color: var(--ok); overflow-wrap: anywhere; }
.small { font-size: 12px; }
</style>
