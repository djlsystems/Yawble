<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import type { AgentCard, YawbleApi, TeamSummary, Trigger, TriggerFire, WakeManager } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import WatchesSection from './WatchesSection.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

// Triggers: tell a member an instruction on a schedule (every N, cron, once), each fire a
// new workflow. Fires missed while the app was closed are recorded, not caught up. "Only when idle" skips a fire
// while the member is busy.
const props = defineProps<{ api?: YawbleApi; team: TeamSummary; agents: AgentCard[] }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const triggers = ref<Trigger[]>([]);
const loaded = ref(false);
// Save / Run now / Delete: held while the core answers, the clicked one with the spinner.
const work = useBusy();
const error = ref<string | null>(null);
const note = ref<string | null>(null);
const editing = ref(false);
const preview = ref<{ error: string | null; next: string[] }>({ error: null, next: [] });
const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
const form = reactive({
  id: null as number | null, memberId: 0, name: '', kind: 'every' as 'every' | 'cron' | 'once' | 'folder' | 'event', eventName: '',
  every: 60, unit: 60, cron: '0 9 * * 1-5', timezone: localZone, fireAt: '', instruction: '', onlyWhenIdle: true, enabled: true,
  wakeManager: 'onHandbackOrFailure' as WakeManager,
  watchRoot: 'documents' as 'documents' | 'host', watchPath: '', watchGlob: '', quietSec: 10, pollSec: 30,
});
const canPick = typeof api.pickFolder === 'function' && !api.remote;
async function pickWatchFolder() {
  const picked = await api.pickFolder();
  if (picked) form.watchPath = picked;
}
const wakeLabels: Record<WakeManager, string> = {
  onHandbackOrFailure: 'only if it hands back or fails',
  always: 'every time a run ends',
  never: 'never (a hand-back still does)',
};

const members = computed(() => props.agents.map((a) => a.member));
// Opened before the team's agents arrived: a new trigger takes the Manager once they do.
watch(members, (list) => { if (editing.value && !form.memberId && list.length) form.memberId = list.find((m) => m.role === 'manager')?.id ?? list[0].id; });
const nameOf = (id: number) => members.value.find((m) => m.id === id)?.name ?? `#${id}`;
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '—');
const describe = (t: Trigger) =>
  t.schedule.kind === 'every' ? `every ${human(t.schedule.intervalSec ?? 0)}`
    : t.schedule.kind === 'cron' ? `cron ${t.schedule.cron} (${t.schedule.timezone})`
    : t.schedule.kind === 'event' ? `when event ${t.eventName ?? '?'} happens`
    : t.schedule.kind === 'folder' ? `when files change in ${t.watch?.root === 'host' ? t.watch.path : `documents${t.watch?.path ? '/' + t.watch.path : ''}`}${t.watch?.glob ? ` (${t.watch.glob})` : ''}`
    : `once at ${when(t.schedule.fireAt)}`;
function human(sec: number) {
  if (sec % 86400 === 0) return `${sec / 86400} day(s)`;
  if (sec % 3600 === 0) return `${sec / 3600} hour(s)`;
  if (sec % 60 === 0) return `${sec / 60} minute(s)`;
  return `${sec} s`;
}

function payload() {
  return {
    id: form.id, memberId: form.memberId, name: form.name.trim(), kind: form.kind,
    intervalSec: form.kind === 'every' ? Math.round(form.every * form.unit) : form.kind === 'folder' ? Math.max(10, Math.round(form.pollSec)) : null,
    cron: form.kind === 'cron' ? form.cron.trim() : null,
    timezone: form.kind === 'cron' ? form.timezone.trim() : null,
    fireAt: form.kind === 'once' && form.fireAt ? new Date(form.fireAt).toISOString() : null,
    instruction: form.instruction.trim(), onlyWhenIdle: form.onlyWhenIdle, enabled: form.enabled, wakeManager: form.wakeManager,
    ...(form.kind === 'event' ? { eventName: form.eventName.trim() } : {}),
    ...(form.kind === 'folder' ? { watchRoot: form.watchRoot, watchPath: form.watchPath.trim(), watchGlob: form.watchGlob.trim() || null, quietSec: Math.round(form.quietSec) } : {}),
  };
}

async function load() {
  error.value = null;
  try {
    triggers.value = await api.call<Trigger[]>('triggers.list', { teamId: props.team.id });
    loaded.value = true;
  } catch (e) {
    error.value = errorText(e);
  }
}

function startNew() {
  Object.assign(form, { id: null, memberId: members.value.find((m) => m.role === 'manager')?.id ?? members.value[0]?.id ?? 0,
    name: '', kind: 'every', every: 1, unit: 3600, cron: '0 9 * * 1-5', timezone: localZone, fireAt: '', instruction: '', onlyWhenIdle: true, enabled: true, wakeManager: 'onHandbackOrFailure' });
  editing.value = true;
  note.value = null;
}

function edit(t: Trigger) {
  const s = t.schedule;
  const sec = s.intervalSec ?? 3600;
  const unit = sec % 86400 === 0 ? 86400 : sec % 3600 === 0 ? 3600 : sec % 60 === 0 ? 60 : 1;
  Object.assign(form, { id: t.id, memberId: t.memberId, name: t.name, kind: s.kind, every: sec / unit, unit,
    cron: s.cron ?? '0 9 * * 1-5', timezone: s.timezone ?? localZone,
    fireAt: s.fireAt ? new Date(s.fireAt).toISOString().slice(0, 16) : '', instruction: t.instruction, onlyWhenIdle: t.onlyWhenIdle, enabled: t.enabled,
    wakeManager: t.wakeManager ?? 'always',
    eventName: t.eventName ?? '',
    watchRoot: t.watch?.root ?? 'documents', watchPath: t.watch?.path ?? '', watchGlob: t.watch?.glob ?? '', quietSec: t.watch?.quietSec ?? 10,
    pollSec: s.kind === 'folder' ? (s.intervalSec ?? 30) : 30 });
  editing.value = true;
  note.value = null;
}

const save = () => work.run('save', doSave);
async function doSave() {
  error.value = null;
  try {
    const t = await api.call<Trigger>('triggers.save', payload());
    editing.value = false;
    note.value = `Saved ${t.name}. Next fire: ${when(t.nextDueAt)}.`;
    await load();
  } catch (e) {
    error.value = errorText(e);
  }
}

function remove(t: Trigger) {
  if (!window.confirm(`Delete the trigger ${t.name}?`)) return;
  return work.run(`trigger-${t.id}`, () => doRemove(t), 'delete');
}
async function doRemove(t: Trigger) {
  try {
    await api.call('triggers.delete', { id: t.id });
    await load();
  } catch (e) {
    error.value = errorText(e);
  }
}

const runNow = (t: Trigger) => work.run(`trigger-${t.id}`, () => doRunNow(t), 'run');
async function doRunNow(t: Trigger) {
  try {
    const f = await api.call<TriggerFire>('triggers.runNow', { id: t.id });
    note.value = f.outcome === 'fired' ? `${t.name}: told ${nameOf(t.memberId)} now.` : `${t.name}: ${f.outcome} (${f.reason ?? ''}).`;
    await load();
  } catch (e) {
    error.value = errorText(e);
  }
}

let previewTimer: ReturnType<typeof setTimeout> | undefined;
watch(() => [form.kind, form.every, form.unit, form.cron, form.timezone, form.fireAt, editing.value], () => {
  if (!editing.value) return;
  clearTimeout(previewTimer);
  previewTimer = setTimeout(async () => {
    try {
      preview.value = await api.call('triggers.preview', payload());
    } catch (e) {
      preview.value = { error: errorText(e), next: [] };
    }
  }, 300);
});

onMounted(load);
</script>

<template>
  <Modal :title="`Triggers · ${team.name}`" wide @close="emit('close')">
    <p class="muted small">A trigger tells one agent an instruction on a schedule; each fire starts a new workflow. Every fire of an
      agent is a paid model run: prefer the longest schedule that does the job. Fires that pass while the app is closed are
      recorded as missed, not caught up.</p>
    <p v-if="error" class="error" data-test="triggers-error">{{ error }}</p>
    <p v-if="note" class="muted small" data-test="triggers-note">{{ note }}</p>

    <LoadingNote v-if="!editing && !loaded && !error" text="Loading triggers…" />
    <table v-else-if="!editing && loaded" class="list" data-test="triggers-table">
      <tr><th>Name</th><th>Agent</th><th>Schedule</th><th>Next</th><th>Last</th><th></th></tr>
      <tr v-for="t in triggers" :key="t.id" :data-test="`trigger-${t.name}`">
        <td><strong>{{ t.name }}</strong><span v-if="!t.enabled" class="chip">off</span></td>
        <td>{{ nameOf(t.memberId) }}</td>
        <td>{{ describe(t) }}<span v-if="t.onlyWhenIdle" class="chip">only when idle</span>
          <div class="muted small" data-test="trigger-wake">Manager: {{ wakeLabels[t.wakeManager ?? 'always'] }}</div></td>
        <td>{{ when(t.nextDueAt) }}</td>
        <td>{{ t.lastOutcome ?? '—' }}<span v-if="t.lastReason" class="muted small"> · {{ t.lastReason }}</span>
          <span v-if="t.missedCount" class="chip warn">{{ t.missedCount }} missed</span></td>
        <td class="actions">
          <button data-test="trigger-run-now" :disabled="work.is(`trigger-${t.id}`)" @click="runNow(t)"><BusyLabel :busy="work.is(`trigger-${t.id}`, 'run')" text="Running…">Run now</BusyLabel></button>
          <button data-test="trigger-edit" :disabled="work.is(`trigger-${t.id}`)" @click="edit(t)">Edit</button>
          <button class="danger" data-test="trigger-delete" :disabled="work.is(`trigger-${t.id}`)" @click="remove(t)"><BusyLabel :busy="work.is(`trigger-${t.id}`, 'delete')" text="Deleting…">Delete</BusyLabel></button>
        </td>
      </tr>
      <tr v-if="triggers.length === 0"><td colspan="6" class="muted">No triggers yet.</td></tr>
    </table>
    <button v-if="!editing" class="primary" data-test="trigger-new" @click="startNew">+ New trigger</button>

    <div v-else class="form">
      <label>Name <input v-model="form.name" placeholder="weekday status report" data-test="trigger-name" /></label>
      <label>Agent
        <select v-model="form.memberId" data-test="trigger-member">
          <option v-for="m in members" :key="m.id" :value="m.id">{{ m.name }} ({{ m.role }})</option>
        </select>
      </label>
      <div class="row kinds">
        <label v-for="k in ['every', 'cron', 'once', 'folder', 'event']" :key="k" class="check">
          <input v-model="form.kind" type="radio" :value="k" :data-test="`trigger-kind-${k}`" /> {{ k === 'every' ? 'Every' : k === 'cron' ? 'Cron' : k === 'once' ? 'Once' : k === 'folder' ? 'Folder change' : 'Event' }}
        </label>
      </div>
      <template v-if="form.kind === 'event'">
        <label>Event name <input v-model="form.eventName" class="mono" placeholder="web.pr (or web.* for several)" data-test="trigger-event" /></label>
        <p class="muted small">Fires when a watch (below) publishes this event, with the item it found. The agent is told the instruction and the item.</p>
      </template>
      <template v-if="form.kind === 'folder'">
        <div class="row fields">
          <label>Watch
            <select v-model="form.watchRoot" data-test="trigger-watch-root">
              <option value="documents">the team's documents</option>
              <option value="host" :disabled="api.remote">a folder on this computer</option>
            </select>
          </label>
          <label class="grow">{{ form.watchRoot === 'documents' ? 'Folder inside the documents (blank: all of them)' : 'Folder' }}
            <input v-model="form.watchPath" class="mono" :placeholder="form.watchRoot === 'documents' ? 'inbox' : 'C:\\Scans'" data-test="trigger-watch-path" />
          </label>
          <button v-if="form.watchRoot === 'host' && canPick" type="button" data-test="trigger-watch-pick" @click="pickWatchFolder">Pick…</button>
        </div>
        <div class="row fields">
          <label>Files <input v-model="form.watchGlob" class="mono num-wide" placeholder="*.pdf (blank: all)" data-test="trigger-watch-glob" /></label>
          <label>Look every <span class="unit"><input v-model.number="form.pollSec" type="number" min="10" class="num" data-test="trigger-watch-poll" /> s</span></label>
          <label>Settled after <span class="unit"><input v-model.number="form.quietSec" type="number" min="0" max="3600" class="num" data-test="trigger-watch-quiet" /> s</span></label>
        </div>
        <p class="muted small">
          Fires when files are added, removed or change size, once the folder has stayed still for the settle time (a copy in
          progress fires once, after). The agent is told what changed.
          <template v-if="form.watchRoot === 'host'"> To read files there, the agent needs that folder in its guardrails (read-only is enough).</template>
        </p>
      </template>
      <div v-if="form.kind === 'every'" class="row">
        Every <input v-model.number="form.every" type="number" min="1" class="num" data-test="trigger-every" />
        <select v-model.number="form.unit" data-test="trigger-unit">
          <option :value="60">minutes</option><option :value="3600">hours</option><option :value="86400">days</option>
        </select>
      </div>
      <div v-if="form.kind === 'cron'" class="row fields">
        <label class="grow">Cron (minute hour day month weekday) <input v-model="form.cron" class="mono" data-test="trigger-cron" /></label>
        <label>Timezone <input v-model="form.timezone" data-test="trigger-timezone" /></label>
      </div>
      <label v-if="form.kind === 'once'">At <input v-model="form.fireAt" type="datetime-local" data-test="trigger-at" /></label>
      <p v-if="form.kind !== 'folder' && form.kind !== 'event'" class="muted small" data-test="trigger-preview">
        <template v-if="preview.error">{{ preview.error }}</template>
        <template v-else-if="preview.next.length">Next: {{ preview.next.map(when).join(' · ') }}</template>
      </p>
      <label>Instruction <textarea v-model="form.instruction" rows="6" class="wide" data-test="trigger-instruction"
        placeholder="What the agent should do each time (it is told this as a new piece of work)." /></label>
      <label>Wake the Manager when a run ends
        <select v-model="form.wakeManager" data-test="trigger-wake-manager">
          <option value="onHandbackOrFailure">Only if it hands back or fails (recommended)</option>
          <option value="always">Every time</option>
          <option value="never">Never (a hand-back still wakes it)</option>
        </select>
      </label>
      <p class="muted small">Each Manager wake is a paid run. When it is not woken, a run that ends without handing back closes its own workflow.</p>
      <label class="check"><input v-model="form.onlyWhenIdle" type="checkbox" data-test="trigger-idle" /> Only when the agent is idle (skip a fire while it is busy)</label>
      <label class="check"><input v-model="form.enabled" type="checkbox" data-test="trigger-enabled" /> Enabled</label>
      <div class="row end">
        <button :disabled="work.is('save')" @click="editing = false">Cancel</button>
        <button class="primary" data-test="trigger-save" :disabled="!form.name.trim() || !form.instruction.trim() || work.is('save')" :aria-busy="work.is('save')" @click="save"><BusyLabel :busy="work.is('save')" text="Saving…">Save</BusyLabel></button>
      </div>
    </div>
    <WatchesSection v-if="!editing" :api="api" :team="team" />
  </Modal>
</template>

<style scoped>
.list { width: 100%; border-collapse: collapse; margin: 8px 0; }
.list th, .list td { text-align: left; padding: 4px 6px; border-bottom: 1px solid var(--line); vertical-align: top; }
.actions { white-space: nowrap; }
.chip { margin-left: 6px; font-size: 11px; padding: 0 5px; border-radius: 8px; background: var(--chip); }
.chip.warn { background: var(--warn-chip); }
.form { display: flex; flex-direction: column; gap: 12px; }
/* Every field: its label above it. Checkboxes and radios keep theirs beside them. */
.form > label:not(.check), .form .fields > label:not(.check) { display: flex; flex-direction: column; gap: 4px; }
.form > label:not(.check) > input:not([type=number]):not([type=datetime-local]), .form .fields > label.grow > input { width: 100%; box-sizing: border-box; }
.form > label:not(.check) > select { align-self: flex-start; max-width: 100%; }
.form .fields { align-items: flex-end; flex-wrap: wrap; gap: 12px; }
.form .fields > label.grow { flex: 1 1 220px; }
.form .fields > label > select { max-width: 100%; }
.unit { display: inline-flex; align-items: center; gap: 6px; }
.wide { width: 100%; box-sizing: border-box; resize: vertical; min-height: 120px; font: inherit; }
.form > p { margin: 0; }
.kinds { gap: 14px; }
.num-wide { width: 140px; }
.num { width: 70px; }
.end { justify-content: flex-end; gap: 8px; }
.mono { font-family: monospace; }
</style>
