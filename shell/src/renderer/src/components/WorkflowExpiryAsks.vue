<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { WORKFLOW_EXTEND, type BoardCard, type Decision, type YawbleApi } from '../../../shared/api';
import { errorCode } from '../api';
import { expiryWarningText, extendErrorText, until } from '../workflowExpiry';
import BusyLabel from './BusyLabel.vue';
import { vChange } from '../remoteAccess';

/**
 * B085 in Needs you: an open workflow that waits on the person's answer and is warned it will expire, as the core's
 * words ("#<id> will expire tomorrow; it waits on your answer to question <n>", n the decision's seq), with Extend
 * (bus.workflowExtend: resets its expiry clock). The core puts the warning on the bus.decisions item (expiryWarning and
 * expiresAt) while the workflow is warned. The teams looked at are those with a needs-decision card on the Board;
 * filtered by the Board's team name. An older core (no expiryWarning) shows nothing. An Extend on a workflow that has
 * meanwhile ended is refused with already_ended, shown as such.
 */
const props = defineProps<{ api: YawbleApi; cards: readonly BoardCard[]; pollMs?: number; team?: string }>();
const emit = defineEmits<{ count: [n: number] }>();

interface Warning { teamId: number; team: string; d: Decision }
const warnings = ref<Warning[]>([]);
const error = ref<string | null>(null);
const busy = ref<string | null>(null);

/** The teams with a question waiting (a needs-decision card in a workflow), as [teamId, team], sorted: the load key. */
const asking = computed(() => {
  const set = new Map<number, string>();
  for (const c of props.cards) if (c.status === 'needs-decision' && c.workflow != null && c.teamId != null) set.set(c.teamId, c.team);
  return [...set.entries()].sort((a, b) => a[0] - b[0]);
});
const shown = computed(() => warnings.value.filter((x) => !props.team || x.team === props.team));
watch(() => shown.value.length, (n) => emit('count', n), { immediate: true });
const keyOf = (x: Warning) => `${x.teamId}:${x.d.workflow}`;

let loading = false;
async function load() {
  if (loading) return;
  loading = true;
  try {
    const found: Warning[] = [];
    for (const [teamId, team] of asking.value) {
      let list: Decision[];
      try { list = await props.api.call<Decision[]>('bus.decisions', { teamId }); } catch { continue; }
      const seen = new Set<number>();
      for (const d of Array.isArray(list) ? list : [])
        if (d.expiryWarning && !seen.has(d.workflow)) { seen.add(d.workflow); found.push({ teamId, team, d }); }
    }
    warnings.value = found;
  } finally { loading = false; }
}

async function extend(x: Warning) {
  if (busy.value) return;
  busy.value = keyOf(x);
  error.value = null;
  try {
    await props.api.call(WORKFLOW_EXTEND, { teamId: x.teamId, workflow: x.d.workflow });
    warnings.value = warnings.value.filter((y) => y !== x);
    await load();
  } catch (e) {
    error.value = extendErrorText(e, x.d.workflow);
    // An ended workflow has nothing to extend: its line goes (the next load agrees once the core drops the warning).
    if (errorCode(e) === 'already_ended') warnings.value = warnings.value.filter((y) => y !== x);
  } finally { busy.value = null; }
}

watch(() => asking.value.map(([id]) => id).join(','), () => void load());
let timer: ReturnType<typeof setInterval> | null = null;
onMounted(() => { void load(); timer = setInterval(load, Math.max(props.pollMs ?? 30000, 2000)); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
defineExpose({ load });
</script>

<template>
  <div v-if="shown.length || error" class="expiry-asks" data-test="workflow-expiry-asks">
    <p v-if="error" class="error small" data-test="workflow-expiry-error">{{ error }}</p>
    <div v-for="x in shown" :key="keyOf(x)" class="expiry-card" :data-test="`workflow-expiry-${x.d.workflow}`">
      <b data-test="workflow-expiry-text">{{ expiryWarningText(x.d) }}</b>
      <span class="small muted">{{ x.team }} · {{ x.d.from }}: {{ x.d.question }}<template v-if="x.d.expiresAt"> · expires {{ until(x.d.expiresAt) }}</template></span>
      <div class="row">
        <button v-change class="primary" :disabled="busy !== null" data-test="workflow-expiry-extend" title="Reset its expiry clock"
          @click="extend(x)"><BusyLabel :busy="busy === keyOf(x)" text="Extending…">Extend</BusyLabel></button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.expiry-asks { display: flex; flex-direction: column; gap: 8px; }
.expiry-card { display: flex; flex-direction: column; gap: 3px; font-size: 12.5px; padding: 8px 10px; border: 1px solid var(--warn-edge-soft);
  border-left: 3px solid var(--warn-edge); border-radius: 10px; background: var(--surface); }
.expiry-card .muted { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.row { display: flex; gap: 6px; margin-top: 4px; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.small { font-size: 12px; }
.muted { color: var(--muted); }
.error { color: var(--bad); margin: 0; }
@media (max-width: 759.98px) { .row button { min-height: 44px; } }
</style>
