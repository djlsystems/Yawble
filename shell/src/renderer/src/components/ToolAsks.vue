<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ToolAsk, ToolInstallResult, YawbleApi } from '../../../shared/api';
import ToolAskCard from './ToolAskCard.vue';

/**
 * The waiting tool asks in Needs you (tools.asks, pending and installing): one card per team and tool. Refreshed on
 * tools.asksChanged (an ask raised, answered or installed, here or in another view) and on a slow poll. `banner`: the
 * window-wide strip above every view (App.vue); otherwise a plain list (the Board's Needs You lane), filtered by the
 * Board's team and member names. After an install the item goes and a short line says the tool is installed, until
 * closed. An older core without tools.asks shows nothing.
 */
const props = defineProps<{ api: YawbleApi; pollMs?: number; banner?: boolean; team?: string; member?: string }>();
const emit = defineEmits<{ count: [n: number] }>();

const asks = ref<ToolAsk[]>([]);
const shown = computed(() => asks.value.filter((a) => (!props.team || a.team === props.team) && (!props.member || a.memberName === props.member)));
watch(() => shown.value.length, (n) => emit('count', n), { immediate: true });
/** The last install that finished here: its message, shown until closed. */
const done = ref<ToolInstallResult | null>(null);

let loading = false;
let again = false;
async function load() {
  if (loading) { again = true; return; }
  loading = true;
  try {
    do {
      again = false;
      try { asks.value = await props.api.call<ToolAsk[]>('tools.asks', {}); }
      catch { asks.value = []; } // an older core, or a remote view that may not read them: nothing to show
    } while (again);
  } finally { loading = false; }
}

let timer: ReturnType<typeof setInterval> | null = null;
let off: (() => void) | null = null;
onMounted(() => {
  void load();
  off = props.api.onEvent?.((e) => { if (e.event === 'tools.asksChanged') void load(); }) ?? null;
  timer = setInterval(load, Math.max(props.pollMs ?? 5000, 5000));
});
onBeforeUnmount(() => { off?.(); if (timer) clearInterval(timer); });

defineExpose({ load });
</script>

<template>
  <section v-if="shown.length || done" class="tool-asks" :class="{ banner }" data-test="tool-asks">
    <p v-if="banner && shown.length" class="title"><b>Needs you</b>: {{ shown.length === 1 ? 'an agent asks' : `${shown.length} agents ask` }} for a developer tool</p>
    <ToolAskCard v-for="a in shown" :key="a.id" :ask="a" :api="api" @installed="done = $event" @answered="load" />
    <p v-if="done" class="done small" data-test="tool-ask-done" role="status">
      {{ done.message }} <button class="link small" data-test="tool-ask-done-close" @click="done = null">Close</button>
    </p>
  </section>
</template>

<style scoped>
.tool-asks { display: flex; flex-direction: column; gap: 8px; }
.banner { padding: 8px 18px; border-bottom: 1px solid var(--line); border-left: 3px solid var(--warn); background: var(--side); }
.title { margin: 0; font-size: 13px; }
.done { margin: 0; color: var(--ok); }
.small { font-size: 12px; }
</style>
