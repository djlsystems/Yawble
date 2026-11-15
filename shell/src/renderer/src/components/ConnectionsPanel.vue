<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { Connection, TeamSummary, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import ConnectionDialog from './ConnectionDialog.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * Connections: the sign-ins (API keys and tokens) that APIs share, kept on this computer. Values are shown masked, never in full.
 * They are managed only on the computer itself, so a remote view shows a sentence and no list.
 */
const props = defineProps<{ api?: YawbleApi; teams?: TeamSummary[] }>();
const emit = defineEmits<{ (e: 'count', n: number): void }>();
const api = resolveApi(props.api);
const remote = computed(() => !!api.remote);

const list = ref<Connection[]>([]);
const error = ref<string | null>(null);
const note = ref<string | null>(null);
const busy = ref<number | null>(null);
/** Which action runs for `busy` (test, remove, signin, done): its button shows the spinner. */
const busyWhat = ref<string | null>(null);
const doing = (id: number, what: string) => busy.value === id && busyWhat.value === what;
function begin(id: number, what: string) {
  if (busy.value !== null) return false;
  busy.value = id; busyWhat.value = what;
  return true;
}
function end() { busy.value = null; busyWhat.value = null; }
const dialog = ref<{ connection: Connection | null } | null>(null);
/** The website login whose sign-in window is open (Sign in pressed, Done not yet). */
const signingIn = ref<number | null>(null);
/** Website logins whose Test runs in the background: "checking..." until connections.changed. */
const checking = ref(new Set<number>());

const loaded = ref(false); // the first answer arrived (or failed)
async function load() {
  if (remote.value) return;
  try { list.value = await api.call<Connection[]>('integrations.connections', {}); error.value = null; }
  catch (e) { error.value = errorText(e); }
  finally { loaded.value = true; }
}

function statusText(c: Connection) {
  if (checking.value.has(c.id)) return 'checking…';
  if (c.status === 'works') return 'works';
  if (c.status === 'needs_reconnect') return `needs reconnect${c.statusReason ? `: ${c.statusReason}` : ''}`;
  return `untested${c.statusReason ? `: ${c.statusReason}` : ''}`;
}
function statusWord(s: Connection['status']) { return s === 'works' ? 'works' : s === 'needs_reconnect' ? 'needs reconnect' : 'untested'; }
async function test(c: Connection) {
  if (!begin(c.id, 'test')) return;
  error.value = null;
  note.value = null;
  try {
    const r = await api.call<{ status: Connection['status']; reason: string | null; changed?: boolean; checking?: boolean }>('integrations.connectionTest', { id: c.id });
    if (r.checking) {
      // a website login is checked in a hidden window in the background; the result comes with connections.changed
      checking.value = new Set([...checking.value, c.id]);
      return;
    }
    // an unclear answer (a 5xx, a timeout, nothing to call) changes nothing, and the reason says so
    note.value = r.changed === false
      ? `${c.name}: ${r.reason ?? 'status unchanged'}`
      : `${c.name}: ${statusWord(r.status)}${r.reason ? ` (${r.reason})` : ''}`;
    await load();
  } catch (e) { error.value = errorText(e); }
  finally { end(); }
}
/** The host tasks that take a variable from this Connection, as "release (team Ops)"; the team's id when its name is not known. */
function taskUses(c: Connection): string[] {
  return (c.usedByTasks ?? []).map((u) => `${u.taskName} (team ${props.teams?.find((t) => t.id === u.teamId)?.name ?? u.teamId})`);
}
async function remove(c: Connection) {
  const tasks = taskUses(c);
  const parts: string[] = [];
  if (c.usedBy.length) parts.push(`${c.name} is used by ${c.usedBy.join(', ')}: those APIs will need a new connection.`);
  if (tasks.length) parts.push(`${tasks.length === 1 ? 'The host task' : 'The host tasks'} ${tasks.join(', ')} take a variable from it and will not start without it.`);
  const msg = parts.length ? `${parts.join(' ')} Remove it anyway?` : `Remove the connection ${c.name}?`;
  if (busy.value !== null || !window.confirm(msg)) return;
  if (!begin(c.id, 'remove')) return;
  error.value = null;
  try { await api.call('integrations.connectionRemove', { id: c.id, confirm: true }); await load(); }
  catch (e) { error.value = errorText(e); }
  finally { end(); }
}
async function signIn(c: Connection) {
  if (!begin(c.id, 'signin')) return;
  error.value = null;
  try { await api.call('integrations.websiteSignIn', { connectionId: c.id }); signingIn.value = c.id; }
  catch (e) { error.value = errorText(e); }
  finally { end(); }
}
async function signInDone(c: Connection) {
  if (!begin(c.id, 'done')) return;
  error.value = null;
  try { await api.call('integrations.websiteSignInDone', { connectionId: c.id }); signingIn.value = null; await load(); }
  catch (e) { error.value = errorText(e); }
  finally { end(); }
}
/** An MCP server's sign-in is bound to its resource: it is signed in again from the server, never here. */
function serverBound(c: Connection) { return c.kind === 'oauth' && c.fields.some((f) => f.key === 'resource' && !f.secret && !!f.shown); }
function closed() { dialog.value = null; void load(); }

watch(list, (v) => emit('count', v.length));
let off: (() => void) | null = null;
onMounted(() => {
  if (!remote.value) off = api.onEvent?.((e) => { if (e.event === 'connections.changed') { signingIn.value = null; checking.value = new Set(); void load(); } }) ?? null;
  void load();
});
onBeforeUnmount(() => off?.());
</script>

<template>
  <section class="connections" data-test="connections">
    <p v-if="remote" class="muted" data-test="connections-remote">Connections are managed on the computer itself.</p>
    <template v-else>
      <p class="muted small">
        The keys and tokens your APIs sign in with, and your website logins. They stay on this computer, are shown masked, and are never given to an agent.
      </p>
      <LoadingNote v-if="!loaded" text="Loading connections…" />
      <p v-if="error" class="error" data-test="connections-error">{{ error }}</p>
      <p v-if="note" class="muted small" data-test="connections-note">{{ note }}</p>
      <div v-for="c in list" :key="c.id" class="card" :data-test="`connection-${c.id}`">
        <header class="row">
          <div class="grow">
            <b>{{ c.name }}</b> <span class="muted small" data-test="connection-kind">{{ c.kind === 'website' ? 'Website login' : c.kind === 'oauth' ? 'OAuth sign-in' : 'API key or token' }}</span>
            <div v-if="signingIn === c.id" class="small" data-test="connection-signin-note">A sign-in window is open. Sign in there, then press Done.</div>
            <div class="small mono" data-test="connection-fields">
              <span v-for="f in c.fields" :key="f.key" class="fld">{{ f.label }}: {{ f.shown ?? 'not set' }}</span>
            </div>
            <div class="small" :class="c.status === 'works' ? 'ok' : c.status === 'needs_reconnect' ? 'bad' : 'muted'" data-test="connection-status">{{ statusText(c) }}</div>
            <div v-if="serverBound(c)" class="muted small" :data-test="`connection-mcp-note-${c.id}`">Sign in again from Settings → Integrations → MCP servers (Sign in).</div>
            <div class="small" data-test="connection-usedby">
              <template v-if="c.usedBy.length">Used by {{ c.usedBy.join(', ') }}</template>
              <span v-else-if="!taskUses(c).length" class="muted">not used yet</span>
            </div>
            <div v-if="taskUses(c).length" class="small" :data-test="`connection-usedby-tasks-${c.id}`">Used by host {{ taskUses(c).length === 1 ? 'task' : 'tasks' }} {{ taskUses(c).join(', ') }}: removing it stops {{ taskUses(c).length === 1 ? 'it' : 'them' }} from starting</div>
          </div>
          <template v-if="c.kind === 'website' && !remote">
            <button v-if="signingIn !== c.id" :disabled="busy === c.id" :data-test="`connection-signin-${c.id}`" @click="signIn(c)"><BusyLabel :busy="doing(c.id, 'signin')" text="Opening…">{{ c.status === 'needs_reconnect' ? 'Reconnect' : 'Sign in' }}</BusyLabel></button>
            <button v-else class="primary" :disabled="busy === c.id" :data-test="`connection-signin-done-${c.id}`" @click="signInDone(c)"><BusyLabel :busy="doing(c.id, 'done')" text="Checking…">Done</BusyLabel></button>
          </template>
          <button v-if="c.kind === 'oauth' && !remote && !serverBound(c)" :class="{ primary: c.status === 'needs_reconnect' }" :data-test="`connection-signin-${c.id}`" @click="dialog = { connection: c }">{{ c.status === 'needs_reconnect' ? 'Reconnect' : 'Sign in again' }}</button>
          <button :disabled="busy === c.id || checking.has(c.id)" :data-test="`connection-test-${c.id}`" @click="test(c)"><BusyLabel :busy="doing(c.id, 'test') || checking.has(c.id)" text="Testing…">Test</BusyLabel></button>
          <button :data-test="`connection-edit-${c.id}`" @click="dialog = { connection: c }">Edit</button>
          <button class="link danger" :disabled="busy === c.id" :data-test="`connection-remove-${c.id}`" @click="remove(c)"><BusyLabel :busy="doing(c.id, 'remove')" text="Removing…">Remove</BusyLabel></button>
        </header>
      </div>
      <div class="row"><button data-test="connection-add" @click="dialog = { connection: null }">+ Add connection</button></div>
      <ConnectionDialog v-if="dialog" :key="dialog.connection?.id ?? 'new'" :api="api" :connection="dialog.connection" @close="closed" />
    </template>
  </section>
</template>

<style scoped>
.card { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; margin: 8px 0; background: var(--surface); }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.fld { margin-right: 12px; }
.ok { color: var(--ok); } .bad { color: var(--bad); }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); font-weight: 400; } .small { font-size: 12px; }
.link { border: 0; background: transparent; color: var(--accent); }
.link.danger { color: var(--bad); }
.error { color: var(--bad); }
</style>
