<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { NetworkAsk, YawbleApi } from '../../../shared/api';
import NetworkAskCard from './NetworkAskCard.vue';

/**
 * The waiting network asks in Needs you (network.asks, pending only): one card per team, member and host. Refreshed on
 * network.asksChanged (an ask raised or answered, here or in another view) and on a slow poll (the count of a repeated
 * refusal changes without an event). `banner`: the window-wide strip above every view (App.vue); otherwise a plain list
 * (the Board's Needs You lane), filtered by the Board's team and member names. An older core without network.asks
 * shows nothing.
 */
const props = defineProps<{ api: YawbleApi; pollMs?: number; banner?: boolean; team?: string; member?: string }>();
const emit = defineEmits<{ count: [n: number] }>();

const asks = ref<NetworkAsk[]>([]);
const shown = computed(() => asks.value.filter((a) => (!props.team || a.team === props.team) && (!props.member || a.memberName === props.member)));
watch(() => shown.value.length, (n) => emit('count', n), { immediate: true });

let loading = false;
let again = false;
async function load() {
  if (loading) { again = true; return; }
  loading = true;
  try {
    do {
      again = false;
      try { asks.value = await props.api.call<NetworkAsk[]>('network.asks', {}); }
      catch { asks.value = []; } // an older core, or a remote view that may not read them: nothing to show
    } while (again);
  } finally { loading = false; }
}

let timer: ReturnType<typeof setInterval> | null = null;
let off: (() => void) | null = null;
onMounted(() => {
  void load();
  off = props.api.onEvent?.((e) => { if (e.event === 'network.asksChanged') void load(); }) ?? null;
  timer = setInterval(load, Math.max(props.pollMs ?? 5000, 5000));
});
onBeforeUnmount(() => { off?.(); if (timer) clearInterval(timer); });

defineExpose({ load });
</script>

<template>
  <section v-if="shown.length" class="network-asks" :class="{ banner }" data-test="network-asks">
    <p v-if="banner" class="title"><b>Needs you</b>: {{ shown.length === 1 ? 'an agent asks' : `${shown.length} agents ask` }} to reach a host it was refused</p>
    <NetworkAskCard v-for="a in shown" :key="a.id" :ask="a" :api="api" @answered="load" />
  </section>
</template>

<style scoped>
.network-asks { display: flex; flex-direction: column; gap: 8px; }
.banner { padding: 8px 18px; border-bottom: 1px solid var(--line); border-left: 3px solid var(--warn); background: var(--side); }
.title { margin: 0; font-size: 13px; }
</style>
