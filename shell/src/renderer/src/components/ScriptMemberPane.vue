<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { AgentCard, TeamSummary, Trigger, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi, when } from '../api';
import { readOnly, vChange } from '../remoteAccess';
import { useBusy } from '../busy';
import BusyLabel from './BusyLabel.vue';
import ScriptMemberIntegrations from './ScriptMemberIntegrations.vue';
import ScriptEditor from './scripts/ScriptEditor.vue';
import { scriptAtVersion, scriptsApi, shortValue, type ScriptMember, type ScriptMemberFile, type ScriptMemberState, type ScriptRun } from '../scriptMembers';

// A script member's tile on the team page: its name, script@version (on latest: the version it runs),
// the last run's status and time and the next scheduled trigger; under it the last runs (scripts.runs) and the saved
// variables (scripts.memberState), each with a Reset (scripts.memberResetVariable) that a Visitor does not get. Its
// integrations show what it uses for each one the script names, and Change sets or clears an override.
const props = defineProps<{ card: AgentCard; api?: YawbleApi; pollMs?: number; pending?: 'remove' | null }>();
/** triggers: open the Triggers dialog, on this trigger when it is the member's only one. */
const emit = defineEmits<{ remove: []; triggers: [triggerId: number | null] }>();
const api = resolveApi(props.api);
const rpc = scriptsApi(api);

const member = ref<ScriptMember | null>(null);
const runs = ref<ScriptRun[]>([]);
/** The files its script wrote (run.log and the like): text files show their newest lines. */
const files = ref<ScriptMemberFile[]>([]);
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
    try { files.value = (await rpc.memberFiles(at, 30)).files ?? []; } catch { /* an older core: no files section */ }
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

// Edit script: the script editor on this member's script, opened from the member, so the designer shows the member's
// overrides ("Real name → Other") and Use real names resolves through its bindings. Save makes a new version of the
// script, as the library does; the member's pin and bindings stay as they are. A Visitor gets the editor read-only.
const editing = ref(false);
const editTeams = computed<TeamSummary[]>(() => (member.value
  ? [{ id: member.value.teamId, name: member.value.team, repoPath: null, members: 0, running: 0, queued: 0 }] : []));
function closeEditor() { editing.value = false; void refresh(); }

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
      <button v-if="member" type="button" class="small-btn edit" data-test="script-member-edit"
        :title="readOnly ? `Open ${member.script} (read-only)` : `Edit ${member.script}: the designer shows this member's integrations`"
        @click="editing = true">{{ readOnly ? 'Open script' : 'Edit script' }}</button>
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
      <ScriptMemberIntegrations v-if="member" :api="api" :member="member" @updated="(m) => { member = m; }" />
      <h4>Last runs</h4>
      <ul v-if="runs.length" class="runs" data-test="script-member-runs">
        <li v-for="r in runs" :key="r.id" data-test="script-member-run">
          <span class="status" :class="r.status">{{ statusLabel[r.status] ?? r.status }}</span>
          <span class="muted">{{ when(r.startedAt) }} · v{{ r.version }} · {{ triggerLabel(r.trigger) }}</span>
          <span v-if="r.reason" class="reason" :title="r.reason">{{ r.reason }}</span>
        </li>
      </ul>
      <p v-else class="muted small" data-test="script-member-no-runs">No runs yet.</p>
      <template v-for="f in files.filter((x) => x.tail && x.tail.length)" :key="f.path">
        <h4 class="mono" :data-test="`script-member-file-${f.path}`">{{ f.path }} <span class="muted">· {{ when(f.modifiedAt) }}</span></h4>
        <ol class="log mono" :data-test="`script-member-file-lines-${f.path}`">
          <li v-for="(line, i) in [...f.tail!].reverse()" :key="i">{{ line }}</li>
        </ol>
      </template>
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
    <Teleport to="body">
      <div v-if="editing && member" class="editor-sheet" role="dialog" :aria-label="`${member.script} for ${card.member.name}`" data-test="script-member-editor">
        <ScriptEditor :api="api" :teams="editTeams" :script="{ id: member.scriptId, key: member.script }" :member-id="member.memberId"
          :back-label="`← ${card.member.name}`" @close="closeEditor" @saved="refresh" />
      </div>
    </Teleport>
  </section>
</template>

<style scoped>
.pane { width: 100%; box-sizing: border-box; background: var(--surface); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 14px;
  padding: 6px 10px; display: flex; flex-direction: column; gap: 4px; min-height: 0; }
.pane.running { border-left-color: var(--ok); }
.title { display: flex; align-items: center; gap: 6px; min-width: 0; }
.title strong { font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 0 1 auto; min-width: 0; }
.title .small-btn { margin-left: auto; }
.title .edit ~ .small-btn { margin-left: 0; }
.editor-sheet { position: fixed; inset: 0; z-index: 50; background: var(--surface); padding: 12px 16px; overflow: auto; box-sizing: border-box;
  display: flex; flex-direction: column; }
.trig { border: none; cursor: pointer; display: inline-flex; align-items: center; gap: 5px; background: var(--chip); color: var(--ok); font: inherit; font-size: 11.5px; }
.trig:hover { text-decoration: underline; }
.trig .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--ok); animation: trig-pulse 1.6s ease-in-out infinite; }
@keyframes trig-pulse { 0%, 100% { opacity: 1; transform: scale(1); } 50% { opacity: .3; transform: scale(.7); } }
@media (prefers-reduced-motion: reduce) { .trig .dot { animation: none; } }
.chip { font-size: 11.5px; background: var(--chip); color: var(--ink-soft); border-radius: 10px; padding: 1px 8px; }
.facts { display: flex; flex-direction: column; gap: 2px; font-size: 12.5px; color: var(--ink-soft); }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.body { display: flex; flex-direction: column; min-height: 0; flex: 1 1 auto; overflow: auto; }
/* The body scrolls as a whole; its parts keep their own height. A part that scrolls itself (the file's lines) would
   otherwise shrink to a sliver with a scroll bar and no way to make it taller. */
.body > * { flex: none; }
.log { list-style: none; margin: 0; padding: 4px 6px; background: var(--code-bg, var(--chip)); border-radius: 4px; font-size: 11.5px; max-height: 160px; overflow: auto; gap: 1px; }
.log li { white-space: pre-wrap; word-break: break-word; }
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
