<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { usePoll } from '../usePoll';
import { DELIVERABLE_EXTEND, type BoardCard, type DeliverableSummary, type YawbleApi } from '../../../shared/api';
import { expiresSoon, expiryWarningText, extendErrorText, until } from '../deliverableExpiry';
import BusyLabel from './BusyLabel.vue';
import { vChange } from '../remoteAccess';

/**
 * Expiring deliverables in Needs you: an open deliverable that waits on the person's answer and will expire within a day, as
 * "#<id> will expire tomorrow; it waits on your answer to question <n>", with Extend (bus.deliverableExtend: resets
 * its expiry clock). The teams looked at are those with a needs-decision card on the Board; a deliverable shows when it has
 * such a card (or the core names its question) and its expiresAt (bus.deliverables) is within a day. Filtered by the Board's team name. An older core (no
 * expiresAt) shows nothing.
 */
const props = defineProps<{ api: YawbleApi; cards: readonly BoardCard[]; pollMs?: number; team?: string }>();
const emit = defineEmits<{ count: [n: number] }>();

interface Warning { teamId: number; team: string; w: DeliverableSummary }
const warnings = ref<Warning[]>([]);
const error = ref<string | null>(null);
const busy = ref<string | null>(null);

/** The teams with a question waiting (a needs-decision card in a deliverable), as "teamId:team", sorted: the load key. */
const asking = computed(() => {
  const set = new Map<number, string>();
  for (const c of props.cards) if (c.status === 'needs-decision' && c.deliverable != null && c.teamId != null) set.set(c.teamId, c.team);
  return [...set.entries()].sort((a, b) => a[0] - b[0]);
});
const askingDeliverables = computed(() => new Set(props.cards.filter((c) => c.status === 'needs-decision' && c.deliverable != null).map((c) => `${c.teamId}:${c.deliverable}`)));
const shown = computed(() => warnings.value.filter((x) => !props.team || x.team === props.team));
watch(() => shown.value.length, (n) => emit('count', n), { immediate: true });

let loading = false;
async function load() {
  if (loading) return;
  loading = true;
  try {
    const found: Warning[] = [];
    for (const [teamId, team] of asking.value) {
      let list: DeliverableSummary[];
      try { list = await props.api.call<DeliverableSummary[]>('bus.deliverables', { teamId, limit: 50 }); } catch { continue; }
      for (const w of Array.isArray(list) ? list : [])
        if (expiresSoon(w) && (w.question != null || askingDeliverables.value.has(`${teamId}:${w.id}`))) found.push({ teamId, team, w });
    }
    warnings.value = found;
  } finally { loading = false; }
}

async function extend(x: Warning) {
  const key = `${x.teamId}:${x.w.id}`;
  if (busy.value) return;
  busy.value = key;
  error.value = null;
  try {
    await props.api.call(DELIVERABLE_EXTEND, { teamId: x.teamId, deliverable: x.w.id });
    warnings.value = warnings.value.filter((y) => y !== x);
    await load();
  } catch (e) { error.value = extendErrorText(e, x.w.id); } finally { busy.value = null; }
}

watch(() => asking.value.map(([id]) => id).join(','), () => void load());
usePoll(load, Math.max(props.pollMs ?? 30000, 2000));
defineExpose({ load });
</script>

<template>
  <div v-if="shown.length || error" class="expiry-asks" data-test="deliverable-expiry-asks">
    <p v-if="error" class="error small" data-test="deliverable-expiry-error">{{ error }}</p>
    <div v-for="x in shown" :key="`${x.teamId}:${x.w.id}`" class="expiry-card" :data-test="`deliverable-expiry-${x.w.id}`">
      <b data-test="deliverable-expiry-text">{{ expiryWarningText(x.w) }}</b>
      <span class="small muted">{{ x.team }} · {{ x.w.title }} · expires {{ until(x.w.expiresAt!) }}</span>
      <div class="row">
        <button v-change class="primary" :disabled="busy !== null" data-test="deliverable-expiry-extend" title="Reset its expiry clock"
          @click="extend(x)"><BusyLabel :busy="busy === `${x.teamId}:${x.w.id}`" text="Extending…">Extend</BusyLabel></button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.expiry-asks { display: flex; flex-direction: column; gap: 8px; }
.expiry-card { display: flex; flex-direction: column; gap: 3px; font-size: 12.5px; padding: 8px 10px; border: 1px solid var(--warn-edge-soft);
  border-left: 3px solid var(--warn-edge); border-radius: 10px; background: var(--surface); }
.row { display: flex; gap: 6px; margin-top: 4px; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.small { font-size: 12px; }
.muted { color: var(--muted); }
.error { color: var(--bad); margin: 0; }
@media (max-width: 759.98px) { .row button { min-height: 44px; } }
</style>
