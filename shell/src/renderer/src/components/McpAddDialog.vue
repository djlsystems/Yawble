<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useEscapeClose } from '../escapeClose';
import type { Connection, McpServerInfo, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import ConnectionDialog from './ConnectionDialog.vue';

/**
 * Add an MCP server: import what Claude Code, Claude Desktop, Cursor or VS Code already have, or fill a simple form (on this computer, or
 * a remote URL), or paste a config and let the form fill itself. Secrets are typed into masked boxes (or a Connection is picked) and are
 * never shown back in full; an import is made by key, so this screen never holds a candidate's secret.
 */
const props = defineProps<{ api?: YawbleApi }>();
const emit = defineEmits<{ (e: 'close'): void; (e: 'saved'): void; (e: 'discover', name: string): void }>();
const api = resolveApi(props.api);
const remote = computed(() => !!api.remote);
const remoteSentence = 'Sign-ins and imports are managed on the computer itself.';

interface Candidate {
  key: string; name: string; transport: 'stdio' | 'http'; command: string; args: string[]; url: string | null;
  env: { name: string; secret: boolean; value?: string; shown?: string | null }[]; sources: string[]; alreadyAdded: string | null | boolean;
  auth?: { type: string; header?: string | null } | null; tokenShown?: string | null; note?: string | null;
}
interface Draft {
  name: string | null; transport: 'stdio' | 'http'; command: string | null; args: string[]; url: string | null;
  auth: { type: string; header?: string | null } | null; env: { name: string; value: string | null; secret: boolean; needed: boolean }[]; note: string | null;
}
interface EnvRow { id: number; name: string; value: string; secret: boolean; needed: boolean }

// ---- import from this computer
const candidates = ref<Candidate[]>([]);
const ticked = ref<Record<string, boolean>>({});
const importing = ref(false);
const imported = ref<string[]>([]);
const importNotes = ref<string[]>([]);
const alreadyName = (c: Candidate) => (typeof c.alreadyAdded === 'string' ? c.alreadyAdded : c.name);
const isAdded = (c: Candidate) => !!c.alreadyAdded;
const tickedKeys = computed(() => candidates.value.filter((c) => ticked.value[c.key] && !isAdded(c)).map((c) => c.key));
const what = (c: Candidate) => (c.transport === 'http' ? (c.url ?? '') : [c.command, ...c.args].join(' '));
const sourcesText = (c: Candidate) => `from ${c.sources.join(', ')}`;
/** The first scan of this computer's MCP configs has answered (the import box shows the loading state until then). */
const scanned = ref(false);
async function scan() {
  if (remote.value) return;
  try { candidates.value = await api.call<Candidate[]>('integrations.mcpImportScan', {}); } catch { candidates.value = []; } finally { scanned.value = true; }
}
async function importSelected() {
  if (tickedKeys.value.length === 0 || importing.value) return;
  importing.value = true;
  error.value = null;
  try {
    const r = await api.call<{ imported: string[]; alreadyAdded?: string[]; missing?: string[]; failed?: { key: string; name: string; error: string }[] }>(
      'integrations.mcpImport', { keys: tickedKeys.value });
    imported.value = r.imported ?? [];
    importNotes.value = [
      ...(r.failed ?? []).map((f) => `${f.name}: ${f.error}`),
      ...(r.alreadyAdded ?? []).map((n) => `${n} was already added`),
      ...(r.missing ?? []).map(() => 'One server is no longer in its config file'),
    ];
    if (imported.value.length) emit('saved');
    await scan();
  } catch (e) { error.value = errorText(e); }
  finally { importing.value = false; }
}

// ---- the form
const mode = ref<'local' | 'remote'>('local');
const name = ref('');
const command = ref('');
const argsText = ref('');
let nextRow = 1;
const envRows = ref<EnvRow[]>([]);
const url = ref('');
const signIn = ref<'oauth' | 'token' | 'none'>('none');
const token = ref('');
const otherHeader = ref(false);
const headerName = ref('');
const connections = ref<Connection[]>([]);
/** A website login signs in a website in this app's window, never an MCP server: not offered. */
const mcpConnections = computed(() => connections.value.filter((c) => c.kind !== 'website'));
const pickConnection = ref('');
const newConnection = ref(false);
const error = ref<string | null>(null);
const info = ref<string | null>(null);
const busy = ref<string | null>(null);

const hasSecretRow = computed(() => envRows.value.some((r) => r.secret));
const needsToken = computed(() => mode.value === 'remote' && signIn.value === 'token');
/** A Connection is sent only when a secret row or a token is what it would hold. */
const usesConnection = computed(() => pickConnection.value !== '' && (needsToken.value || (mode.value === 'local' && hasSecretRow.value)));
const existingNames = ref<string[]>([]);
/** Set after the first successful save here: the name is then fixed (a second save is the same server). */
const savedName = ref<string | null>(null);
watch([mode, signIn], () => { pickConnection.value = ''; });
const blockedBy = computed(() => {
  if (!name.value.trim()) return 'Give the server a name.';
  if (mode.value === 'local' && !command.value.trim()) return 'Give the command that starts the server.';
  if (mode.value === 'remote' && !url.value.trim()) return 'Give the server’s address.';
  if (mode.value === 'local') {
    if (envRows.value.some((r) => !r.name.trim())) return 'Each environment variable needs a name.';
    const missing = envRows.value.find((r) => r.value === '' && (r.needed || (r.secret && !remote.value)) && !(r.secret && usesConnection.value));
    if (missing) return missing.secret ? `Paste a value for ${missing.name} or pick a Connection.` : `Fill in ${missing.name} first.`;
  } else if (needsToken.value && token.value === '' && !usesConnection.value) return 'Paste the token or pick a Connection.';
  return null;
});
const hasBearerChoice = computed(() => needsToken.value);

function addRow(init: Partial<EnvRow> = {}) { envRows.value = [...envRows.value, { id: nextRow++, name: '', value: '', secret: false, needed: false, ...init }]; }
function removeRow(id: number) { envRows.value = envRows.value.filter((r) => r.id !== id); }
async function loadConnections() {
  if (remote.value) return;
  try { connections.value = await api.call<Connection[]>('integrations.connections', {}); } catch { connections.value = []; }
}
function onPick(v: string) {
  if (v === 'new') { newConnection.value = true; return; }
  pickConnection.value = v;
}
function connectionSaved(c: Connection) {
  connections.value = [...connections.value.filter((x) => x.id !== c.id), c];
  pickConnection.value = String(c.id);
}

// ---- paste a config
const pasteOpen = ref(false);
const pasteText = ref('');
const drafts = ref<Draft[]>([]);
/** Fill the form: the pasted config is being read by the core. */
const filling = ref(false);
async function fill() {
  if (filling.value) return;
  filling.value = true;
  try { await doFill(); } finally { filling.value = false; }
}
async function doFill() {
  error.value = null;
  info.value = null;
  drafts.value = [];
  try {
    const text = pasteText.value;
    pasteText.value = '';
    const r = await api.call<{ drafts: Draft[] }>('integrations.mcpParse', { text });
    const list = r?.drafts ?? [];
    if (list.length === 0) info.value = 'That doesn’t look like an MCP config';
    else if (list.length === 1) apply(list[0]!);
    else drafts.value = list;
  } catch (e) { error.value = errorText(e); }
}
function apply(d: Draft) {
  drafts.value = [];
  mode.value = d.transport === 'http' ? 'remote' : 'local';
  name.value = d.name ?? '';
  command.value = d.command ?? '';
  argsText.value = (d.args ?? []).join('\n');
  url.value = d.url ?? '';
  envRows.value = [];
  for (const e of d.env ?? []) addRow({ name: e.name, value: e.value ?? '', secret: e.secret, needed: e.needed });
  const a = d.auth;
  signIn.value = a?.type === 'oauth' ? 'oauth' : a?.type === 'bearer' || a?.type === 'header' ? 'token' : 'none';
  otherHeader.value = a?.type === 'header' && !!a.header && a.header.toLowerCase() !== 'authorization';
  headerName.value = otherHeader.value ? (a?.header ?? '') : '';
  info.value = d.note ?? null;
  pasteOpen.value = false;
}


// ---- save and discover
function secretsToSend(): Record<string, string> {
  const s: Record<string, string> = {};
  if (usesConnection.value) return s;
  if (mode.value === 'remote') { if (needsToken.value && token.value) s.token = token.value; }
  else for (const r of envRows.value) if (r.secret && r.value) s[r.name.trim()] = r.value;
  return s;
}
function saveParams(): Record<string, unknown> {
  const p: Record<string, unknown> = { name: name.value.trim() };
  if (mode.value === 'remote') {
    p.transport = 'http'; // only a remote server carries one: the Cloud Link refuses a save with a transport on a local one
    p.url = url.value.trim();
    if (!remote.value) {
      p.auth = signIn.value === 'oauth' ? { type: 'oauth' }
        : signIn.value === 'token' ? (otherHeader.value && headerName.value.trim() ? { type: 'header', header: headerName.value.trim() } : { type: 'bearer' })
          : { type: 'none' };
    }
  } else {
    p.command = command.value.trim();
    p.args = argsText.value.split('\n').map((a) => a.trim()).filter((a) => a.length);
    p.env = envRows.value.map((r) => (r.secret ? { name: r.name.trim(), secret: true } : { name: r.name.trim(), value: r.value, secret: false }));
  }
  const secrets = secretsToSend();
  if (Object.keys(secrets).length) p.secrets = secrets;
  if (usesConnection.value) p.connectionId = Number(pickConnection.value);
  return p;
}
/** The variable a failed start names as missing (matched case-sensitively: it is upper case; the rest of the sentence is not). */
function missingVariable(msg: string): string | null {
  if (!/\b(not set|is required|missing)\b/i.test(msg)) return null;
  const names = msg.match(/\b[A-Z][A-Z0-9_]{2,}\b/g) ?? [];
  return names.find((n) => n.includes('_')) ?? names[0] ?? null;
}

async function saveAndDiscover() {
  if (busy.value) return;
  if (blockedBy.value) return;
  error.value = null;
  info.value = null;
  busy.value = 'Saving…';
  const server = name.value.trim();
  if (server !== savedName.value && existingNames.value.includes(server.toLowerCase())) {
    error.value = `A server called ${server} already exists; edit it instead.`;
    busy.value = null;
    return;
  }
  try {
    await api.call('integrations.mcpSave', saveParams());
    savedName.value = server;
    existingNames.value = [...existingNames.value, server.toLowerCase()];
    emit('saved');
    if (mode.value === 'remote' && signIn.value === 'oauth' && !remote.value) {
      busy.value = 'Finish signing in in your browser…';
      try { await api.call('integrations.mcpSignIn', { name: server }); }
      catch (e) { error.value = `Saved, but signing in didn't finish: ${errorText(e)}. Press Save and discover to try again.`; return; }
    }
    busy.value = 'Checking the server…';
    try { await api.call('integrations.mcpCheck', { name: server }); }
    catch (e) {
      const msg = errorText(e);
      if (mode.value === 'local') {
        const v = missingVariable(msg);
        if (v && !envRows.value.some((r) => r.name === v)) { addRow({ name: v, secret: true, needed: true }); error.value = `${msg} It is saved: fill in ${v} and save again.`; return; }
      }
      if (mode.value === 'remote' && msg.includes('The server signs me in')) {
        signIn.value = 'oauth';
        info.value = 'This server asks you to sign in. Choose “The server signs me in” and save again.';
        return;
      }
      error.value = `Saved, but it did not start: ${msg}`;
      return;
    }
    token.value = '';
    emit('discover', server);
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = null; }
}

async function loadNames() {
  try { existingNames.value = (await api.call<McpServerInfo[]>('integrations.mcpList', {})).map((s) => s.name.toLowerCase()); } catch { existingNames.value = []; }
}
onMounted(() => { void scan(); void loadConnections(); void loadNames(); });
useEscapeClose(() => emit('close'));
</script>

<template>
  <div class="overlay" data-test="mcp-add-dialog" role="dialog" aria-modal="true" aria-label="Add MCP server">
    <div class="box">
      <header class="row head">
        <h3 class="grow">Add MCP server</h3>
        <button data-test="mcp-add-close" @click="emit('close')">Close</button>
      </header>
      <p v-if="error" class="error" data-test="mcp-add-error">{{ error }}</p>
      <p v-if="info" class="warn small" data-test="mcp-add-info">{{ info }}</p>

      <section v-if="remote" class="muted small" data-test="mcp-import-remote">{{ remoteSentence }}</section>
      <LoadingNote v-else-if="!scanned" text="Looking for MCP servers configured on this computer…" />
      <section v-else-if="candidates.length || imported.length" class="importbox" data-test="mcp-import">
        <b>Import from this computer</b>
        <div v-for="c in candidates" :key="c.key" class="cand" :data-test="`cand-${c.key}`">
          <label class="check">
            <input v-model="ticked[c.key]" type="checkbox" :disabled="isAdded(c)" :data-test="`cand-tick-${c.key}`" />
            <b>{{ c.name }}</b> <span class="mono muted small">{{ what(c) }}</span>
          </label>
          <div class="small muted indent">
            <span>{{ sourcesText(c) }}</span>
            <span v-if="isAdded(c)" data-test="cand-added"> · already added as {{ alreadyName(c) }}</span>
            <span v-for="e in c.env.filter((x) => x.secret)" :key="e.name"> · {{ e.name }} (secret{{ e.shown ? ` ${e.shown}` : '' }})</span>
            <span v-if="c.tokenShown"> · token {{ c.tokenShown }}</span>
            <span v-if="c.note"> · {{ c.note }}</span>
          </div>
        </div>
        <div class="row"><button class="primary" :disabled="importing || tickedKeys.length === 0" data-test="import-selected" :aria-busy="importing" @click="importSelected"><BusyLabel :busy="importing" text="Importing…">Import selected</BusyLabel></button></div>
        <div v-if="imported.length" data-test="imported">
          <p class="small ok">Added:</p>
          <div v-for="n in imported" :key="n" class="row small">
            <b>{{ n }}</b> <button :data-test="`imported-discover-${n}`" @click="emit('discover', n)">Discover</button>
          </div>
        </div>
        <p v-for="n in importNotes" :key="n" class="small warn">{{ n }}</p>
      </section>

      <section class="form" data-test="mcp-form">
        <div class="row">
          <fieldset class="row modes"><legend class="small">Where it runs</legend>
          <label class="check"><input v-model="mode" type="radio" value="local" data-test="mode-local" /> On this computer</label>
          <label class="check"><input v-model="mode" type="radio" value="remote" data-test="mode-remote" /> Remote (URL)</label></fieldset>
          <span class="grow" />
          <button class="link" data-test="paste-toggle" @click="pasteOpen = !pasteOpen">Paste config</button>
        </div>
        <div v-if="pasteOpen" class="signin-box" data-test="paste-box">
          <textarea v-model="pasteText" aria-label="Pasted MCP config" class="mono" rows="5" placeholder="Paste an mcpServers config, a “claude mcp add” line, an npx command or a URL" data-test="paste-text"></textarea>
          <div class="row"><button :disabled="!pasteText.trim() || filling" data-test="paste-fill" :aria-busy="filling" @click="fill"><BusyLabel :busy="filling" text="Reading…">Fill the form</BusyLabel></button></div>
          <div v-if="drafts.length" data-test="paste-drafts">
            <p class="small">Which one?</p>
            <button v-for="(d, i) in drafts" :key="i" class="pick" :data-test="`paste-draft-${i}`" @click="apply(d)">{{ d.name || d.url || d.command }}</button>
          </div>
        </div>

        <label class="small fld">Name
          <input v-model="name" placeholder="e.g. linear" :disabled="savedName !== null" data-test="f-name" />
        </label>

        <template v-if="mode === 'remote'">
          <label class="small fld">Address
            <input v-model="url" class="mono" placeholder="https://mcp.example.com/mcp" data-test="f-url" />
          </label>
          <fieldset v-if="!remote" class="signin-box" data-test="f-signin"><legend class="small">Sign-in</legend>
            <label class="check small"><input v-model="signIn" type="radio" value="oauth" data-test="signin-oauth" /> The server signs me in</label>
            <label class="check small"><input v-model="signIn" type="radio" value="token" data-test="signin-token" /> A token</label>
            <div v-if="hasBearerChoice" class="indent">
              <input v-model="token" aria-label="Token" type="password" autocomplete="off" placeholder="paste it here" :disabled="usesConnection" data-test="f-token" />
              <div class="small muted">
                <template v-if="!otherHeader">Sent as <code class="mono">Authorization: Bearer</code>. <button class="link" data-test="other-header" @click="otherHeader = true">use another header</button></template>
                <label v-else class="fld">Header name
                  <input v-model="headerName" class="mono" placeholder="X-API-Key" data-test="f-header" />
                </label>
              </div>
            </div>
            <label class="check small"><input v-model="signIn" type="radio" value="none" data-test="signin-none" /> No sign-in</label>
            <label v-if="needsToken" class="small fld">or use Connection
              <select :value="pickConnection" data-test="f-connection" @change="onPick(($event.target as HTMLSelectElement).value)">
                <option value="">Paste a new one above</option>
                <option v-for="c in mcpConnections" :key="c.id" :value="String(c.id)">{{ c.name }}</option>
                <option value="new">New connection…</option>
              </select>
            </label>
          </fieldset>
          <p v-else class="muted small" data-test="f-signin-remote">{{ remoteSentence }}</p>
        </template>

        <template v-else>
          <label class="small fld">Command
            <input v-model="command" class="mono" placeholder="e.g. npx" data-test="f-command" />
          </label>
          <label class="small fld">Arguments, one per line
            <textarea v-model="argsText" class="mono" rows="3" placeholder="-y&#10;@some/mcp-server" data-test="f-args"></textarea>
          </label>
          <div class="small"><b>Environment variables</b></div>
          <div v-for="r in envRows" :key="r.id" class="row envrow" :class="{ needed: r.needed && r.value === '' }" :data-test="`env-row-${r.name}`">
            <input v-model="r.name" aria-label="Variable name" class="mono" placeholder="NAME" data-test="env-name" />
            <input v-model="r.value" :aria-label="`Value of ${r.name || 'the variable'}`" :type="r.secret ? 'password' : 'text'" autocomplete="off" :placeholder="r.secret ? 'paste it here' : 'value'"
              :disabled="r.secret && (remote || usesConnection)" data-test="env-value" />
            <label class="check small"><input v-model="r.secret" type="checkbox" data-test="env-secret" /> secret</label>
            <button class="link" data-test="env-remove" @click="removeRow(r.id)">remove</button>
          </div>
          <div class="row">
            <button class="link" data-test="env-add" @click="addRow()">+ Add a variable</button>
            <label v-if="hasSecretRow && !remote" class="small">or use Connection
              <select :value="pickConnection" data-test="f-connection" @change="onPick(($event.target as HTMLSelectElement).value)">
                <option value="">Paste a new one above</option>
                <option v-for="c in mcpConnections" :key="c.id" :value="String(c.id)">{{ c.name }}</option>
                <option value="new">New connection…</option>
              </select>
            </label>
          </div>
        </template>

        <div class="row">
          <span class="grow small muted" data-test="f-busy">{{ busy }}</span>
          <button class="primary" :disabled="!!busy || !!blockedBy" :title="blockedBy ?? ''" data-test="save-discover" :aria-busy="!!busy" @click="saveAndDiscover"><BusyLabel :busy="!!busy" text="Working…">Save and discover</BusyLabel></button>
        </div>
      </section>
      <ConnectionDialog v-if="newConnection" :api="api" secret-only @close="newConnection = false" @saved="connectionSaved" />
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: var(--backdrop); display: flex; align-items: flex-start; justify-content: center; padding: 28px 16px; overflow: auto; z-index: 50; }
.box { background: var(--surface); border-radius: 12px; padding: 14px 16px; width: min(760px, 100%); box-sizing: border-box; }
h3 { margin: 0; font-size: 16px; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin: 4px 0; }
.grow { flex: 1; min-width: 0; }
.head { align-items: flex-start; }
.check { display: flex; gap: 5px; align-items: center; }
.fld { display: block; margin: 6px 0; }
.fld input, .fld textarea, textarea, .envrow input[type=text], .envrow input[type=password], .indent input { font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; box-sizing: border-box; }
.fld input, .fld textarea, textarea { display: block; width: 100%; margin-top: 2px; }
.importbox, .signin-box { border: 1px solid var(--line-soft); border-radius: 8px; padding: 8px 10px; margin: 8px 0; }
.cand { margin: 6px 0; }
.indent { margin-left: 22px; }
.envrow.needed { background: var(--warn-soft); border-radius: 6px; padding: 2px 4px; }
.pick { display: block; margin: 3px 0; }
fieldset { border: 0; margin: 0; padding: 0; }
fieldset.signin-box { border: 1px solid var(--line-soft); padding: 8px 10px; }
.link { border: 0; background: transparent; color: var(--accent); }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); } .small { font-size: 12px; } .ok { color: var(--ok); } .warn { color: var(--warn); } .error { color: var(--bad); }
</style>
