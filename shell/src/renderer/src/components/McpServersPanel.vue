<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { Connection, McpServerInfo, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import IntegrationDialog from './IntegrationDialog.vue';
import McpAddDialog from './McpAddDialog.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * MCP servers: local servers the person runs on this computer, their tools offered to the teams chosen. Edit opens the same
 * dialog as a CLI (teams, and the checklist of tools). Nothing decided means it asks first.
 * The app keeps no key or token for a server: it reads its own sign-in, as it would anywhere else.
 */
const props = defineProps<{ api?: YawbleApi; teams: TeamSummary[] }>();
const emit = defineEmits<{ (e: 'count', n: number): void }>();
const api = resolveApi(props.api);
const remote = computed(() => !!api.remote);

const servers = ref<McpServerInfo[]>([]);
const error = ref<string | null>(null);
const busy = ref<string | null>(null);
/** The method running for `busy` (its button shows the spinner). */
const busyMethod = ref<string | null>(null);
const doing = (name: string, method: string) => busy.value === name && busyMethod.value === method;
const editing = ref<string | null>(null);
const adding = ref(false);

const loaded = ref(false); // the first answer arrived (or failed)
async function load() {
  try { servers.value = await api.call<McpServerInfo[]>('integrations.mcpList', {}); error.value = null; }
  catch (e) { error.value = errorText(e); }
  finally { loaded.value = true; }
}
async function call(name: string, method: string, params: Record<string, unknown> = {}) {
  if (busy.value !== null) return false;
  busy.value = name;
  busyMethod.value = method;
  error.value = null;
  try { await api.call(method, { name, ...params }); await load(); return true; }
  catch (e) { error.value = errorText(e); await load(); return false; }
  finally { busy.value = null; busyMethod.value = null; }
}
function discoverNew(name: string) { adding.value = false; editing.value = name; void load(); }
async function signInAgain(s: McpServerInfo) {
  if (busy.value !== null) return;
  busy.value = s.name;
  busyMethod.value = 'integrations.mcpSignIn';
  error.value = null;
  try { await api.call('integrations.mcpSignIn', { name: s.name }); await load(); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = null; busyMethod.value = null; }
}
/** What a remote server's card says about its sign-in. */
function signInState(s: McpServerInfo): { text: string; cls: string; again: boolean } {
  const type = s.auth?.type ?? 'none';
  if (type === 'none') return { text: 'no sign-in', cls: 'muted', again: false };
  if (s.connection?.status === 'needs_reconnect') return { text: 'needs you to sign in again', cls: 'warn', again: type === 'oauth' };
  return { text: type === 'oauth' ? 'signed in' : 'uses a saved token', cls: 'ok', again: false };
}
async function remove(s: McpServerInfo) {
  if (busy.value !== null) return;
  if (!window.confirm(`Remove ${s.name}? Agents can no longer use it.`)) return;
  let removeConnection = false;
  if (s.connection && !remote.value) {
    try {
      const all = await api.call<Connection[]>('integrations.connections', {});
      const c = all.find((x) => x.id === s.connection!.id);
      const others = (c?.usedBy ?? []).filter((n) => n.toLowerCase() !== `mcp: ${s.name}`.toLowerCase());
      if (c && others.length === 0) removeConnection = window.confirm(`Also delete the connection '${c.name}'?`);
    } catch { /* the connection stays */ }
  }
  await call(s.name, 'integrations.mcpRemove', removeConnection && !remote.value ? { removeConnection: true } : {});
}
const offered = (s: McpServerInfo) => {
  if (s.all) return 'every team';
  const names = s.teams.map((id) => props.teams.find((t) => t.id === id)?.name ?? `team ${id}`);
  return names.length ? names.join(', ') : null;
};
const countsText = (s: McpServerInfo) => {
  const c = s.counts;
  if (!c) return null;
  const n = c.allowed + c.ask + c.off + c.undecided;
  return n === 0 ? 'no tools listed yet' : `${n} actions: ${c.allowed} allowed, ${c.ask} ask first`;
};
function closed() { editing.value = null; void load(); }

watch(servers, (v) => emit('count', v.length));
onMounted(load);
</script>

<template>
  <section class="mcp" data-test="mcp-servers">
    <p class="muted small">
      Servers you run on this computer (for example with npx) or reach by address. Their tools are offered to the teams you choose; anything you have not
      allowed asks you first. Agents never receive a key or token.
    </p>
    <LoadingNote v-if="!loaded" text="Loading MCP servers…" />
    <p v-if="error" class="error" data-test="mcp-error">{{ error }}</p>
    <div v-for="s in servers" :key="s.name" class="srv" :data-test="`mcp-${s.name}`">
      <header class="row">
        <div class="grow">
          <b>{{ s.name }}</b>
          <span v-if="s.transport === 'http'" class="mono muted small" data-test="mcp-url">{{ s.url }}</span>
          <span v-else class="mono muted small">{{ s.command }} {{ s.args.join(' ') }}</span>
          <div v-if="s.transport === 'http'" class="small" data-test="mcp-signin">
            <span :class="signInState(s).cls">{{ signInState(s).text }}</span>
            <button v-if="signInState(s).again && !remote" class="link" :disabled="busy === s.name" data-test="mcp-signin-again" @click="signInAgain(s)"><BusyLabel :busy="doing(s.name, 'integrations.mcpSignIn')" text="Signing in…">Sign in again</BusyLabel></button>
          </div>
          <div v-if="s.sources?.length" class="small muted" data-test="mcp-sources">imported from {{ s.sources.join(', ') }}</div>
          <div class="small" :class="s.status?.startsWith('running') ? 'ok' : 'warn'">{{ s.status ?? 'not checked yet' }}</div>
          <div v-if="countsText(s)" class="small muted" data-test="counts">{{ countsText(s) }}</div>
          <div class="small" data-test="offered">
            <template v-if="offered(s)">Offered to {{ offered(s) }}</template>
            <span v-else class="muted">not offered to any team</span>
          </div>
        </div>
        <button v-if="!(remote && s.transport === 'http')" :disabled="busy === s.name" :data-test="`mcp-check-${s.name}`" @click="call(s.name, 'integrations.mcpCheck')"><BusyLabel :busy="doing(s.name, 'integrations.mcpCheck')" text="Checking…">Check</BusyLabel></button>
        <button :data-test="`mcp-edit-${s.name}`" @click="editing = s.name">Edit</button>
        <button class="link danger" :disabled="busy === s.name" :data-test="`mcp-remove-${s.name}`" @click="remove(s)"><BusyLabel :busy="doing(s.name, 'integrations.mcpRemove')" text="Removing…">Remove</BusyLabel></button>
      </header>
    </div>
    <div class="row"><button data-test="mcp-add-open" @click="adding = true">+ Add MCP server</button></div>
    <McpAddDialog v-if="adding" :api="api" @close="adding = false; load()" @saved="load" @discover="discoverNew" />
    <IntegrationDialog v-if="editing" :api="api" kind="mcp" :name="editing" :teams="teams" :initial-scope="null" @close="closed" @saved="load" />
  </section>
</template>

<style scoped>
.srv { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; margin: 8px 0; background: #fff; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.ok { color: var(--ok); } .warn { color: var(--warn); }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); font-weight: 400; }
.small { font-size: 12px; }
.link { border: 0; background: transparent; color: var(--accent); }
.link.danger { color: var(--bad); }
.error { color: var(--bad); }
</style>
