<script setup lang="ts">
import { vChange } from '../remoteAccess';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { usePoll } from '../usePoll';
import type { NetworkAsk, NetworkAskChoice, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import NetworkAskCard from './NetworkAskCard.vue';
import { usePhone } from '../phone';
import { groupAsks } from '../networkAskGroups';

/**
 * The waiting network asks in Needs you (network.asks, pending only): one card per team, member and registrable domain
 * (one member's crl3.digicert.com and ocsp.digicert.com are one item, answered together; networkAskGroups.ts). Refreshed on
 * network.asksChanged (an ask raised or answered, here or in another view) and on a slow poll (the count of a repeated
 * refusal changes without an event). `banner`: the window-wide strip above every view (App.vue); otherwise a plain list
 * (the Board's Needs You lane), filtered by the Board's team and member names. An older core without network.asks
 * shows nothing.
 */
const props = defineProps<{ api: YawbleApi; pollMs?: number; banner?: boolean; team?: string; member?: string }>();
const emit = defineEmits<{ count: [n: number] }>();
/** A phone: the banner's cards edge to edge; App.vue's .banners box holds every banner in at most 40% of the screen (it scrolls). */
const phone = usePhone();

const asks = ref<NetworkAsk[]>([]);
const shown = computed(() => asks.value.filter((a) => (!props.team || a.team === props.team) && (!props.member || a.memberName === props.member)));
const items = computed(() => groupAsks(shown.value));
watch(() => items.value.length, (n) => emit('count', n), { immediate: true });

let loading = false;
let again = false;
async function load() {
  if (loading) { again = true; return; }
  loading = true;
  let ok = true;
  try {
    do {
      again = false;
      try { asks.value = await props.api.call<NetworkAsk[]>('network.asks', {}); ok = true; }
      catch { asks.value = []; ok = false; } // an older core, or a remote view that may not read them: nothing to show
    } while (again);
  } finally { loading = false; }
  return ok; // false slows the poll down
}

const poll = usePoll(load, Math.max(props.pollMs ?? 5000, 5000), { autoStart: false, immediate: false });
let off: (() => void) | null = null;
onMounted(() => {
  void load();
  off = props.api.onEvent?.((e) => { if (e.event === 'network.asksChanged') void load(); }) ?? null;
  poll.start();
});
onBeforeUnmount(() => { off?.(); });

/**
 * Answer all (two or more items): one choice for every waiting item, each answered as its own card would answer it with
 * the host target (network.answer per item, every id of a group). One click arms it and names what it does; Confirm
 * sends. An item whose choices leave the choice out is skipped and counted.
 */
const BULK: { choice: NetworkAskChoice; label: string; says: string }[] = [
  { choice: 'agent', label: 'Allow all for their agents', says: 'allow each host for the agent that asked' },
  { choice: 'team', label: 'Allow all for their teams', says: 'allow each host for the whole team of the agent that asked' },
  { choice: 'everywhere', label: 'Allow all everywhere', says: 'allow every host for every agent (Settings > Guardrails)' },
  { choice: 'deny', label: 'Deny all', says: 'deny every request; each agent is told' },
];
const armed = ref<NetworkAskChoice | null>(null);
const bulkBusy = ref(false);
const bulkError = ref<string | null>(null);
const armedSays = computed(() => BULK.find((b) => b.choice === armed.value)?.says ?? '');
watch(() => items.value.length, (n) => { if (n < 2) armed.value = null; });

async function answerAll() {
  const choice = armed.value;
  if (!choice) return;
  bulkBusy.value = true;
  bulkError.value = null;
  let failed = 0;
  let skipped = 0;
  let first: string | null = null;
  try {
    for (const g of items.value) {
      const lead = g.asks[0];
      if (lead.choices?.length && !lead.choices.includes(choice)) { skipped++; continue; }
      try {
        await props.api.call('network.answer', { id: lead.id, choice, ...(g.asks.length > 1 ? { ids: g.asks.map((a) => a.id) } : {}) });
      } catch (e) { failed++; first ??= errorText(e); }
    }
  } finally {
    bulkBusy.value = false;
    armed.value = null;
    const parts = [failed ? `${failed} could not be answered (${first})` : '', skipped ? `${skipped} skipped: that answer is not offered for them` : ''].filter(Boolean);
    bulkError.value = parts.length ? parts.join('; ') : null;
    await load();
  }
}

defineExpose({ load });
</script>

<template>
  <section v-if="items.length" class="network-asks" :class="{ banner, phone }" data-test="network-asks">
    <p v-if="banner" class="title"><b>Needs you</b>: {{ items.length === 1 ? 'an agent asks' : `${items.length} agents ask` }} to reach a host it was refused</p>
    <div v-if="items.length > 1" class="bulk" data-test="network-asks-bulk">
      <template v-if="!armed">
        <span class="bulk-label">Answer all {{ items.length }}:</span>
        <button v-for="b in BULK" :key="b.choice" v-change :data-test="`network-asks-all-${b.choice}`" :disabled="bulkBusy" @click="armed = b.choice; bulkError = null">{{ b.label }}</button>
      </template>
      <template v-else>
        <span class="bulk-label" data-test="network-asks-all-says">This will {{ armedSays }}, for all {{ items.length }} requests below.</span>
        <button class="primary" v-change data-test="network-asks-all-confirm" :disabled="bulkBusy" :aria-busy="bulkBusy" @click="answerAll">{{ bulkBusy ? 'Answering…' : 'Confirm' }}</button>
        <button data-test="network-asks-all-cancel" :disabled="bulkBusy" @click="armed = null">Cancel</button>
      </template>
      <p v-if="bulkError" class="error small" data-test="network-asks-all-error">{{ bulkError }}</p>
    </div>
    <NetworkAskCard v-for="g in items" :key="g.key" :ask="g.asks[0]" :group="g.asks" :api="api" @answered="load" />
  </section>
</template>

<style scoped>
.network-asks { display: flex; flex-direction: column; gap: 8px; }
.banner { padding: 8px 18px; border-bottom: 1px solid var(--line); border-left: 3px solid var(--warn); background: var(--side); }
.title { margin: 0; font-size: 13px; }
.banner.phone { padding: 8px 8px; }
.bulk { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 6px 10px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); font-size: 12.5px; }
.bulk button { font-size: 12.5px; }
.bulk-label { font-weight: 600; margin-right: 4px; }
.bulk .error { flex-basis: 100%; margin: 0; color: var(--bad); }
.phone .bulk { flex-direction: column; align-items: stretch; }
.phone .bulk button { width: 100%; font-size: 14px; min-height: 44px; }
</style>
