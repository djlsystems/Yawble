<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { AgentCard, Trigger, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi, when } from '../api';
import { readOnly, vChange } from '../remoteAccess';
import { useBusy } from '../busy';
import BusyLabel from './BusyLabel.vue';
import { scriptAtVersion, scriptsApi, shortValue, type ScriptMember, type ScriptMemberState, type ScriptRun } from '../scriptMembers';

// B134 (card 598): a script member's tile on the team page: its name, script@version (on latest: the version it runs),
// the last run's status and time and the next scheduled trigger; under it the last runs (scripts.runs) and the saved
// variables (scripts.memberState), each with a Reset (scripts.memberResetVariable) that a Visitor does not get.
const props = defineProps<{ card: AgentCard; api?: YawbleApi; pollMs?: number; pending?: 'remove' | null }>();
/** triggers: open the Triggers dialog, on this trigger when it is the member's only one. */
const emit = defineEmits<{ remove: []; triggers: [triggerId: number | null] }>();
const api = resolveApi(props.api);
const rpc = scriptsApi(api);

const member = ref<ScriptMember | null>(null);
const runs = ref<ScriptRun[]>([]);
const state = ref<ScriptMemberState | null>(null);
const error = ref<string | null>(null);
const busy = useBusy();
/** This member's triggers; the enabled ones light the Triggers pill. */
const mine = ref<Trigger[]>([]);
const active = computed(() => mine.value.filter((t) => t.enabled));
const pillTitle = computed(() => active.value.map((t) => `${t.name}: ${t.schedule.kind === 'every' ? `every ${Math.round((t.schedule.intervalSec ?? 0) / 60)} min` : t.schedule.kind}`).join(', '));
const id = computed(() => props.card.member.id);

async function refresh() {
  const at = id.value;
  try {
    const [m, r, s] = await Promise.all([rpc.memberGet(at), rpc.runs(at, 10), rpc.memberState(at)]);
    if (at !== id.value) return;
    member.value = m; runs.value = r; state.value = s;
    error.value = null;
    try { mine.value = (await api.call<Trigger[]>('triggers.list', { teamId: props.card.member.teamId })).filter((t) => t.memberId === at); } catch { /* the pill just stays hidden */ }
  } catch (e) {
    if (at === id.value) error.value = errorText(e);
  }
}

let timer: ReturnType<typeof setInterval> | null = null;
onMounted(() => {
  void refresh();
  timer = setInterval(refresh, props.pollMs ?? 5000);
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
watch(id, () => { member.value = null; runs.value = []; state.value = null; void refresh(); });

const variables = computed(() => Object.entries(state.value?.variables ?? {}));
const last = computed(() => member.value?.lastRun ?? null);
const running = computed(() => member.value?.running === true || last.value?.status === 'running');

function reset(name: string) {
  return busy.run(`reset-${name}`, async () => {
    error.value = null;
    try {
      await rpc.memberResetVariable(id.value, name);
      state.value = await rpc.memberState(id.value);
    } catch (e) {
      error.value = errorText(e);
    }
  });
}

const statusLabel: Record<string, string> = { running: 'Running', succeeded: 'Succeeded', failed: 'Failed', skipped: 'Skipped', interrupted: 'Interrupted' };
const triggerLabel = (t: string) => (t.startsWith('trigger:') ? `trigger ${t.slice(8)}` : t);
</script>

<template>
  <section class="pane script-pane" :class="{ running }" :data-test="`script-member-${card.member.name}`" :aria-label="card.member.name">
    <div class="title">
      <strong data-test="script-member-name" :title="card.member.name">{{ card.member.name }}</strong>
      <span class="chip" data-test="script-member-kind">script</span>
      <button v-if="active.length" class="chip trig" data-test="script-member-triggers" :title="pillTitle"
        @click="emit('triggers', mine.length === 1 ? mine[0]!.id : null)"><span class="dot" aria-hidden="true" />{{ active.length === 1 ? 'trigger on' : `${active.length} triggers on` }}</button>
      <button v-if="card.member.role === 'member'" v-change class="danger small-btn" data-test="script-member-remove" :disabled="running || !!pending"
        :aria-busy="pending === 'remove'" @click="emit('remove')">{{ pending === 'remove' ? 'Removing…' : 'Remove' }}</button>
    </div>
    <div class="facts" data-test="script-member-facts">
      <span class="mono" data-test="script-member-version">{{ member ? scriptAtVersion(member) : '…' }}</span>
      <span data-test="script-member-last-run">Last run:
        <template v-if="last"><span class="status" :class="last.status" data-test="script-member-last-status">{{ statusLabel[last.status] ?? last.status }}</span>
          {{ when(last.endedAt ?? last.startedAt) }}</template>
        <template v-else-if="member">never</template>
      </span>
      <span data-test="script-member-next">Next trigger: {{ member ? (member.nextTriggerAt ? when(member.nextTriggerAt) : 'none scheduled') : '…' }}</span>
    </div>
    <p v-if="error" class="error small" data-test="script-member-pane-error">{{ error }}</p>
    <div class="body">
      <h4>Last runs</h4>
      <ul v-if="runs.length" class="runs" data-test="script-member-runs">
        <li v-for="r in runs" :key="r.id" data-test="script-member-run">
          <span class="status" :class="r.status">{{ statusLabel[r.status] ?? r.status }}</span>
          <span class="muted">{{ when(r.startedAt) }} · v{{ r.version }} · {{ triggerLabel(r.trigger) }}</span>
          <span v-if="r.reason" class="reason" :title="r.reason">{{ r.reason }}</span>
        </li>
      </ul>
      <p v-else class="muted small" data-test="script-member-no-runs">No runs yet.</p>
      <h4>Saved variables</h4>
      <ul v-if="variables.length" class="vars" data-test="script-member-variables">
        <li v-for="[name, value] in variables" :key="name" :data-test="`script-variable-${name}`">
          <span class="mono name">{{ name }}</span>
          <span class="mono value" :title="shortValue(value)">{{ shortValue(value) }}</span>
          <button v-if="!readOnly" v-change class="small-btn" :data-test="`script-variable-reset-${name}`" :disabled="busy.is(`reset-${name}`)"
            title="Forget it: the next run starts it from its initial value" @click="reset(name)"><BusyLabel :busy="busy.is(`reset-${name}`)" text="Resetting…">Reset</BusyLabel></button>
        </li>
      </ul>
      <p v-else class="muted small" data-test="script-member-no-variables">None saved.</p>
    </div>
  </section>
</template>

<style scoped>
.pane { width: 100%; box-sizing: border-box; background: var(--surface); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 6px;
  padding: 6px 10px; display: flex; flex-direction: column; gap: 4px; min-height: 0; }
.pane.running { border-left-color: var(--ok); }
.title { display: flex; align-items: center; gap: 6px; min-width: 0; }
.title strong { font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 0 1 auto; min-width: 0; }
.title .small-btn { margin-left: auto; }
.trig { border: none; cursor: pointer; display: inline-flex; align-items: center; gap: 5px; background: var(--chip); color: var(--ok); font: inherit; font-size: 11.5px; }
.trig:hover { text-decoration: underline; }
.trig .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--ok); animation: trig-pulse 1.6s ease-in-out infinite; }
@keyframes trig-pulse { 0%, 100% { opacity: 1; transform: scale(1); } 50% { opacity: .3; transform: scale(.7); } }
@media (prefers-reduced-motion: reduce) { .trig .dot { animation: none; } }
.chip { font-size: 11.5px; background: var(--chip); color: var(--ink-soft); border-radius: 10px; padding: 1px 8px; }
.facts { display: flex; flex-direction: column; gap: 2px; font-size: 12.5px; color: var(--ink-soft); }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.body { display: flex; flex-direction: column; min-height: 0; flex: 1 1 auto; overflow: auto; }
h4 { font-size: 12px; margin: 6px 0 2px; color: var(--ink-soft); font-weight: 600; }
ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 3px; font-size: 12.5px; }
.runs li { display: flex; flex-wrap: wrap; gap: 6px; align-items: baseline; }
.reason { color: var(--ink-soft); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 100%; }
.vars li { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 8px; align-items: center; }
.vars .value { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--ink-soft); }
.status { font-weight: 600; }
.status.succeeded { color: var(--ok); }
.status.failed, .status.interrupted { color: var(--bad); }
.status.running { color: var(--accent); }
.status.skipped { color: var(--muted); }
.small-btn { font-size: 12px; padding: 1px 8px; }
.danger { color: var(--bad); }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); margin: 0; }
</style>
