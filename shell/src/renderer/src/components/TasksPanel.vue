<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { Connection, EnvFromConnection, HostTaskDef, HostTaskInfo, YawbleApi, TaskPreview, TaskRun, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * Host tasks: named programs the person defines, that a team's agents can request and that run on this
 * computer, outside the sandbox, only on the merged base branch of the person's checkout, under an approval tied to what will
 * execute. Only this window writes definitions; an agent can only name a task and give a reason.
 */
const props = defineProps<{ api?: YawbleApi; teams: TeamSummary[] }>();
const api = resolveApi(props.api);

const teamId = ref<number | null>(props.teams[0]?.id ?? null);
const tasks = ref<HostTaskInfo[]>([]);
const repos = ref<string[]>(['main']);
const runs = ref<TaskRun[]>([]);
const openRun = ref<number | null>(null);
const error = ref<string | null>(null);
const notice = ref<string | null>(null);
const busy = ref(false);
/** The action running ("save", "run:<task>", "stop:<run>", ...): that button shows the spinner; all are held. */
const doing = ref<string | null>(null);

/** One "Environment from a Connection" row in the form: the variable, the Connection and which of its secret fields. */
interface BindingRow { name: string; connectionId: number | null; field: string; connectionName: string }
interface Form { original: string | null; name: string; repo: string; program: string; args: string; workdir: string; timeoutSeconds: number; envNames: string; watch: string; pinMode: 'files' | 'commit'; commandLineOnly: boolean; bindings: BindingRow[] }
const form = ref<Form | null>(null);
const preview = ref<TaskPreview | null>(null);

/** The first two tasks, offered as drafts: the person reviews them in the form and presses Save (nothing exists until then). */
const DRAFTS: Record<string, Form> = {
  e2e: { original: null, name: 'e2e', repo: 'main', program: 'npm', args: 'run\ntest:e2e', workdir: 'shell', timeoutSeconds: 900, envNames: '', watch: 'shell/playwright.config.ts', pinMode: 'files', commandLineOnly: false, bindings: [] },
  'core-tests-host': { original: null, name: 'core-tests-host', repo: 'main', program: 'dotnet', args: 'test\n--project\ncore/tests/Yawble.Core.Tests', workdir: '.', timeoutSeconds: 1200, envNames: '', watch: '', pinMode: 'commit', commandLineOnly: false, bindings: [] },
};

const lines = (s: string) => s.split(/\r?\n/).map((x) => x.trim()).filter((x) => x.length > 0);
const names = (s: string) => s.split(/[,\s]+/).map((x) => x.trim()).filter((x) => x.length > 0);
function defOf(f: Form): HostTaskDef {
  return { name: f.name.trim(), repo: f.repo, program: f.program.trim(), args: f.args.split(/\r?\n/).map((x) => x.trim()).filter((x) => x.length > 0), workdir: f.workdir.trim() || '.',
    timeoutSeconds: Number(f.timeoutSeconds), envNames: names(f.envNames), watch: lines(f.watch), pinMode: f.pinMode, commandLineOnly: f.commandLineOnly,
    envFromConnections: f.bindings.filter((b) => !blankRow(b))
      .map((b) => ({ name: b.name.trim(), connectionId: b.connectionId as number, field: b.field, connectionName: b.connectionName })) };
}

/** Over the Cloud Link a task's bindings are shown (names only) but never changed: that is done on the computer itself. */
const remote = computed(() => !!api.remote);
/** The Connections the pickers offer (loaded when the form opens, on the computer only); their secret fields are the field choices. */
const connections = ref<Connection[]>([]);
const connectionsError = ref<string | null>(null);
async function loadConnections() {
  if (remote.value) return;
  try { connections.value = await api.call<Connection[]>('integrations.connections', {}); connectionsError.value = null; }
  catch (e) { connectionsError.value = errorText(e); }
}
const secretFields = (id: number | null) => connections.value.find((c) => c.id === id)?.fields.filter((f) => f.secret) ?? [];
const blankRow = (b: BindingRow) => !b.name.trim() && b.connectionId === null && !b.field;
function addBinding() { form.value?.bindings.push({ name: '', connectionId: null, field: '', connectionName: '' }); }
function removeBinding(i: number) { form.value?.bindings.splice(i, 1); void refreshPreview(); }
/** Picking a Connection keeps the field if it has it, takes its only secret field, or leaves the choice open. */
function pickConnection(b: BindingRow) {
  b.connectionName = connections.value.find((c) => c.id === b.connectionId)?.name ?? '';
  const fs = secretFields(b.connectionId);
  b.field = fs.some((f) => f.key === b.field) ? b.field : fs.length === 1 ? fs[0].key : '';
  void refreshPreview();
}
/** What is wrong with a row before it is sent (the core checks the rest: the name's shape, the names the runtime sets, the field). */
function rowProblem(b: BindingRow, f: Form): string | null {
  if (blankRow(b)) return null;
  const n = b.name.trim();
  if (!n || b.connectionId === null || !b.field) return 'Give a variable name, a Connection and a field, or remove the row.';
  if (f.bindings.filter((x) => x.name.trim() === n).length > 1) return `${n} is bound twice.`;
  if (names(f.envNames).includes(n)) return `${n} is also passed on from your environment: keep one of the two.`;
  return null;
}
const bindingsProblem = computed(() => { const f = form.value; return !!f && f.bindings.some((b) => rowProblem(b, f) !== null); });
/** How a binding reads everywhere: the variable, the Connection's name and the field, never a value. */
const bindingText = (b: Pick<EnvFromConnection, 'name' | 'connectionName' | 'field'>) => `${b.name} ← Connection '${b.connectionName}' (${b.field})`;

/** A run refused for a binding could not read from a Connection; a true environment miss keeps its own label. */
const missingEnvLabel = (r: TaskRun) =>
  r.state === 'refused' || (r.missingEnv ?? '').includes('← Connection') ? 'Could not read from a Connection' : 'Not set in your environment';

const loaded = ref(false); // the first answer arrived (or failed), or there is no team to ask about
async function load() {
  if (teamId.value === null) { loaded.value = true; return; }
  try {
    const r = await api.call<{ tasks: HostTaskInfo[]; repos: string[] }>('tasks.list', { teamId: teamId.value });
    tasks.value = r.tasks; repos.value = r.repos;
    runs.value = await api.call<TaskRun[]>('tasks.runs', { teamId: teamId.value, limit: 30 });
    error.value = null;
  } catch (e) { error.value = errorText(e); }
  finally { loaded.value = true; }
}

async function act(work: () => Promise<unknown>, done?: string, label = '') {
  if (busy.value) return;
  busy.value = true; doing.value = label; error.value = null; notice.value = null;
  try { await work(); if (done) notice.value = done; await load(); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = false; doing.value = null; }
}

function startNew(draft?: string) {
  form.value = draft ? { ...DRAFTS[draft], bindings: [] } : { original: null, name: '', repo: 'main', program: '', args: '', workdir: '.', timeoutSeconds: 600, envNames: '', watch: '', pinMode: 'files', commandLineOnly: false, bindings: [] };
  void loadConnections();
  preview.value = null;
  void refreshPreview();
}
/** The core sends a binding's connectionId as a string ("7"); Connections have numeric ids. Normalised once, on load. */
function connectionIdOf(v: number | string | null | undefined): number | null {
  if (v === null || v === undefined || v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}
function startEdit(t: HostTaskInfo) {
  const d = t.def;
  form.value = { original: d.name, name: d.name, repo: d.repo, program: d.program, args: d.args.join('\n'), workdir: d.workdir, timeoutSeconds: d.timeoutSeconds, envNames: d.envNames.join(', '),
    watch: d.watch.join('\n'), pinMode: d.pinMode, commandLineOnly: d.commandLineOnly,
    bindings: (d.envFromConnections ?? []).map((b) => ({ ...b, connectionId: connectionIdOf(b.connectionId) })) };
  void loadConnections();
  preview.value = null;
  void refreshPreview();
}
async function refreshPreview() {
  if (!form.value || teamId.value === null) return;
  try { preview.value = await api.call<TaskPreview>('tasks.preview', { teamId: teamId.value, def: defOf(form.value) }); }
  catch (e) { preview.value = null; error.value = errorText(e); }
}
async function save() {
  if (!form.value || teamId.value === null) return;
  const f = form.value;
  await act(async () => {
    if (f.original && f.original !== f.name.trim()) throw new Error('A task keeps its name: delete it and add a new one to rename.');
    await api.call('tasks.save', { teamId: teamId.value, def: defOf(f) });
    form.value = null;
  }, 'Saved. Any earlier Always approval was removed; the next request asks.', 'save');
}
const del = (t: HostTaskInfo) => { if (window.confirm(`Delete the task ${t.def.name}? Its run records stay.`)) void act(() => api.call('tasks.delete', { teamId: teamId.value, name: t.def.name }), undefined, `delete:${t.def.name}`); };
const revoke = (t: HostTaskInfo) => act(() => api.call('tasks.revoke', { teamId: teamId.value, name: t.def.name }), 'Always revoked: it asks every time.', `revoke:${t.def.name}`);
async function runNow(t: HostTaskInfo) {
  await act(async () => {
    const r = await api.call<{ kind: string; text: string }>('tasks.runNow', { teamId: teamId.value, name: t.def.name });
    if (r.kind === 'refused') throw new Error(r.text);
    notice.value = r.text;
  }, undefined, `run:${t.def.name}`);
}
const cancel = (runId: number) => act(() => api.call('tasks.cancel', { runId }), 'Stopping: the whole process tree is killed and the agent is told.', `stop:${runId}`);

const approvalText = (t: HostTaskInfo) =>
  t.approval === 'always' ? `Always approved at ${(t.approvedCommit ?? '').slice(0, 8)}` : t.approval === 'always-but-changed' ? 'Always, changed: will ask' : 'Asks every time';
const runText = (r: TaskRun) =>
  r.state === 'running' ? 'running' : r.state === 'interrupted' ? 'interrupted (the app closed)' : r.state === 'refused' ? 'refused at start'
    : r.timedOut ? 'timed out' : r.killedBy === 'person' ? 'stopped by you' : `exit ${r.exitCode}`;
const durationOf = (r: TaskRun) => (r.endedAt ? `${Math.round((Date.parse(r.endedAt) - Date.parse(r.startedAt)) / 1000)} s` : '');
const draftsLeft = computed(() => Object.keys(DRAFTS).filter((n) => !tasks.value.some((t) => t.def.name === n)));

watch(teamId, load);
onMounted(load);
</script>

<template>
  <section class="tasks" data-test="tasks">
    <h3>Host tasks</h3>
    <p class="muted intro">
      Programs you define (the e2e tests, a Docker build, a deploy script) that a team's agents can <em>request</em> by name. They run on this computer, outside any
      sandbox, only when the repository is on its base branch with a clean checkout. Each run needs your approval, tied to the command and the files it will execute;
      "Always for this team" stops applying the moment either changes. A task runs with your rights: do not point one at a program that prints secrets.
    </p>
    <div class="row">
      <label>Team <select v-model="teamId" data-test="tasks-team"><option v-for="t in teams" :key="t.id" :value="t.id">{{ t.name }}</option></select></label>
      <button class="primary" data-test="task-add" :disabled="teamId === null" @click="startNew()">Add task</button>
      <button v-for="n in draftsLeft" :key="n" :data-test="`task-draft-${n}`" title="Fills the form as a draft for you to review; nothing exists until you save" @click="startNew(n)">Draft: {{ n }}</button>
    </div>
    <p v-if="error" class="error" data-test="tasks-error">{{ error }}</p>
    <p v-if="notice" class="ok small" data-test="tasks-notice">{{ notice }}</p>

    <div v-if="form" class="form" data-test="task-form">
      <h4>{{ form.original ? `Edit ${form.original}` : 'New task' }}</h4>
      <div class="grid">
        <label>Name <input v-model="form.name" class="mono" :disabled="!!form.original" data-test="task-name" placeholder="e2e" @input="refreshPreview" /></label>
        <label>Repository <select v-model="form.repo" data-test="task-repo" @change="refreshPreview"><option v-for="r in repos" :key="r" :value="r">{{ r }}</option></select></label>
        <label>Program <input v-model="form.program" class="mono" data-test="task-program" placeholder="npm, or scripts/build.ps1 inside the repo" @input="refreshPreview" /></label>
        <label>Working folder <input v-model="form.workdir" class="mono" data-test="task-workdir" @input="refreshPreview" /></label>
        <label>Time limit (s) <input v-model.number="form.timeoutSeconds" type="number" min="10" max="7200" data-test="task-timeout" /></label>
        <label>Pin mode
          <select v-model="form.pinMode" data-test="task-pinmode" @change="refreshPreview">
            <option value="files">files: ask when the command or its entry-point files change</option>
            <option value="commit">commit: ask after every new commit on the base branch</option>
          </select></label>
      </div>
      <label class="block">Arguments (one per line, never a shell line)<textarea v-model="form.args" rows="3" class="mono" data-test="task-args" @input="refreshPreview"></textarea></label>
      <label class="block">Environment variable names to pass on (names only, never values)<input v-model="form.envNames" class="mono" data-test="task-env" /></label>
      <div class="block bindings" data-test="task-bindings">
        <div class="label">Environment from a Connection</div>
        <p class="muted small">The variable takes a secret field of a Connection when the task starts. The value is masked in the log and never reaches an agent;
          changing a binding asks you again, replacing the token in the Connection does not.</p>
        <template v-if="remote">
          <p v-for="b in form.bindings" :key="b.name" class="mono small" data-test="task-binding-text">{{ bindingText(b) }}</p>
          <p class="muted small" data-test="task-bindings-remote">Bindings are changed on the computer itself.</p>
        </template>
        <template v-else>
          <p v-if="connectionsError" class="error small" data-test="task-bindings-error">{{ connectionsError }}</p>
          <div v-for="(b, i) in form.bindings" :key="i" class="binding" :data-test="`task-binding-${i}`">
            <div class="row">
              <input v-model="b.name" class="mono" placeholder="GH_TOKEN" aria-label="Variable name" data-test="binding-name" @change="refreshPreview" />
              <span aria-hidden="true">←</span>
              <select v-model="b.connectionId" aria-label="Connection" data-test="binding-connection" @change="pickConnection(b)">
                <option :value="null" disabled>Connection…</option>
                <option v-for="c in connections" :key="c.id" :value="c.id">{{ c.name }}</option>
                <option v-if="b.connectionId !== null && !connections.some((c) => c.id === b.connectionId)" :value="b.connectionId">{{ b.connectionName }} (not found)</option>
              </select>
              <select v-model="b.field" aria-label="Field" data-test="binding-field" :disabled="b.connectionId === null" @change="refreshPreview">
                <option value="" disabled>field…</option>
                <option v-for="f in secretFields(b.connectionId)" :key="f.key" :value="f.key">{{ f.label }}</option>
                <option v-if="b.field && !secretFields(b.connectionId).some((f) => f.key === b.field)" :value="b.field">{{ b.field }} (not found)</option>
              </select>
              <button class="mini danger" title="Remove this variable" data-test="binding-remove" @click="removeBinding(i)">Remove</button>
            </div>
            <p v-if="rowProblem(b, form)" class="warn small" data-test="binding-problem">{{ rowProblem(b, form) }}</p>
          </div>
          <button class="small-btn" data-test="binding-add" @click="addBinding">+ Variable from a Connection</button>
        </template>
      </div>
      <label class="block">Also pin these paths (one per line; globs allowed, e.g. scripts/**)<textarea v-model="form.watch" rows="2" class="mono" data-test="task-watch" @input="refreshPreview"></textarea></label>
      <div class="live" data-test="task-preview">
        <p v-if="preview?.problem" class="error small" data-test="task-problem">{{ preview.problem }}</p>
        <template v-else-if="preview">
          <p class="small">Resolved program: <span class="mono">{{ preview.programResolved }}</span></p>
          <p class="small">Pinned files at <span class="mono">{{ (preview.commit ?? '').slice(0, 8) }}</span>:
            <span class="mono">{{ preview.pinned.join(', ') || 'none' }}</span></p>
          <p v-for="w in preview.warnings" :key="w" class="small warn">{{ w }}</p>
          <label v-if="preview.commandLineOnly && form.pinMode === 'files'" class="check small warn">
            <input v-model="form.commandLineOnly" type="checkbox" data-test="task-cmdonly" /> I accept that only the command line is pinned</label>
        </template>
      </div>
      <div class="row">
        <button class="primary" :disabled="busy || !form.name.trim() || !form.program.trim() || bindingsProblem" data-test="task-save" :aria-busy="doing === 'save'" @click="save"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
        <button :disabled="busy" data-test="task-cancel-edit" @click="form = null">Cancel</button>
      </div>
    </div>

    <LoadingNote v-if="!loaded" text="Loading host tasks…" />
    <p v-else-if="!tasks.length && !form" class="muted small" data-test="tasks-empty">No tasks for this team yet.</p>
    <div v-for="t in tasks" :key="t.id" class="task" :data-test="`task-${t.def.name}`">
      <header class="row">
        <div class="grow">
          <b class="mono">{{ t.def.name }}</b> <span class="muted small">{{ t.def.repo }} · in {{ t.def.workdir }} · limit {{ t.def.timeoutSeconds }} s · pin {{ t.def.pinMode }}</span>
          <div class="mono small">{{ t.commandLine }}</div>
          <div v-for="b in t.def.envFromConnections ?? []" :key="b.name" class="mono small" data-test="task-binding-text">{{ bindingText(b) }}</div>
          <div class="small">
            <span :class="t.approval === 'always' ? 'ok' : t.approval === 'always-but-changed' ? 'warn' : 'muted'" data-test="task-approval">{{ approvalText(t) }}</span>
            <template v-if="t.pendingAskId"> · <span class="warn">waiting for you (request {{ t.pendingAskId }}, on the team's page)</span></template>
            <template v-if="t.lastRun"> · last: {{ runText(t.lastRun) }}<template v-if="t.lastRun.requestedBy"> asked by {{ t.lastRun.requestedBy }}</template></template>
          </div>
          <div v-if="t.problem" class="small warn">{{ t.problem }}</div>
        </div>
        <button v-if="t.runningRunId" :disabled="busy" data-test="task-stop" @click="cancel(t.runningRunId)"><BusyLabel :busy="doing === `stop:${t.runningRunId}`" text="Stopping…">Stop</BusyLabel></button>
        <button v-else :disabled="busy" data-test="task-run-now" title="Run it now yourself: same checks, no ask" @click="runNow(t)"><BusyLabel :busy="doing === `run:${t.def.name}`" text="Starting…">Run now</BusyLabel></button>
        <button :disabled="busy" @click="startEdit(t)">Edit</button>
        <button v-if="t.approval !== 'asks'" :disabled="busy" data-test="task-revoke" @click="revoke(t)"><BusyLabel :busy="doing === `revoke:${t.def.name}`" text="Revoking…">Revoke Always</BusyLabel></button>
        <button class="danger" :disabled="busy" @click="del(t)"><BusyLabel :busy="doing === `delete:${t.def.name}`" text="Deleting…">Delete</BusyLabel></button>
      </header>
    </div>

    <h4>Run records</h4>
    <p v-if="!runs.length" class="muted small">No runs yet.</p>
    <table v-else class="tbl" data-test="task-runs">
      <template v-for="r in runs" :key="r.id">
      <tr :data-test="`task-run-${r.id}`" @click="openRun = openRun === r.id ? null : r.id">
        <td>#{{ r.id }}</td><td class="mono">{{ r.task }}</td>
        <td :class="r.state === 'done' && r.exitCode === 0 ? 'ok' : r.state === 'running' ? 'muted' : 'warn'">{{ runText(r) }}</td>
        <td class="muted">{{ durationOf(r) }}</td><td class="muted mono">{{ (r.commit ?? '').slice(0, 8) }}</td>
        <td class="muted">{{ r.requestedBy ?? 'you' }}</td>
        <td><button v-if="r.state === 'running'" class="mini" data-test="run-stop" :disabled="busy" @click.stop="cancel(r.id)"><BusyLabel :busy="doing === `stop:${r.id}`" text="Stopping…">Stop</BusyLabel></button></td>
      </tr>
      <tr v-if="openRun === r.id"><td class="detail" colspan="7">
          <div class="small">Started {{ r.startedAt }}<template v-if="r.endedAt">, ended {{ r.endedAt }}</template></div>
          <div class="small mono">program {{ r.program }}</div>
          <div class="small">definition <span class="mono">{{ r.definitionHash }}</span> · files <span class="mono">{{ r.filesHash }}</span><template v-if="r.askId"> · from request {{ r.askId }}</template></div>
          <div class="small">output {{ r.outputBytes ?? 0 }} bytes<template v-if="r.truncated"> (log cut at its cap)</template><template v-if="r.killedBy"> · killed by {{ r.killedBy }}</template></div>
          <div v-if="r.headAfter && r.commit && r.headAfter !== r.commit" class="small warn">The checkout moved to another commit during the run.</div>
          <div v-if="r.dirtyAfter" class="small warn">The checkout was left with uncommitted changes.</div>
          <div v-if="r.missingEnv" class="small warn" data-test="run-missing-env">{{ missingEnvLabel(r) }}: {{ r.missingEnv }}</div>
          <div v-if="r.logPath" class="small">Log: <span class="mono" data-test="run-log">{{ r.logPath }}</span></div>
      </td></tr>
      </template>
    </table>
  </section>
</template>

<style scoped>
.tasks { margin-top: 18px; border-top: 1px solid var(--line); padding-top: 8px; }
.intro { font-size: 13px; margin: 0 0 10px; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.task { border: 1px solid var(--line); border-radius: 10px; padding: 8px 12px; margin: 8px 0; background: #fff; }
.form { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; margin: 10px 0; background: #fff; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px 12px; }
label { display: flex; flex-direction: column; gap: 2px; font-size: 12.5px; }
label.block { margin-top: 8px; }
label.check { flex-direction: row; gap: 5px; align-items: center; }
input, select, textarea { font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; }
.live { margin: 8px 0; padding: 6px 8px; background: #f6f6f6; border-radius: 6px; }
.tbl { border-collapse: collapse; font-size: 12.5px; width: 100%; }
.tbl td { padding: 3px 10px 3px 0; cursor: pointer; }
.detail { background: #fafafa; padding: 6px 10px; cursor: default; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); font-weight: 400; }
.small { font-size: 12px; margin: 2px 0; }
.ok { color: var(--ok); } .warn { color: var(--warn); } .error { color: var(--bad); }
.danger { color: var(--bad); }
.mini { border: 0; background: transparent; padding: 0 4px; }
.bindings { margin-top: 8px; }
.bindings .label { font-size: 12.5px; }
.binding { margin: 4px 0; }
.small-btn { font-size: 12.5px; margin-top: 2px; }
h4 { margin: 12px 0 4px; font-size: 13px; }
</style>
