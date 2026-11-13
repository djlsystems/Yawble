<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { IntegrationInfo, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import IntegrationDialog from './IntegrationDialog.vue';
import ApisPanel from './ApisPanel.vue';
import ConnectionsPanel from './ConnectionsPanel.vue';
import McpServersPanel from './McpServersPanel.vue';
import TasksPanel from './TasksPanel.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * Integrations: the person's own CLIs and MCP servers (two sub-tabs), offered to the agents of the teams they choose and run on
 * this computer under rules. Each card's Edit opens one dialog: sign-in, which teams, and the checklist of what may run.
 * Host tasks stay below the tabs.
 */
const props = defineProps<{ api?: YawbleApi; teams: TeamSummary[]; initialTab?: 'cli' | 'api' | 'mcp' | 'connections' | null }>();
const api = resolveApi(props.api);

const list = ref<IntegrationInfo[]>([]);
const error = ref<string | null>(null);
const busy = ref<string | null>(null);
/** The method running for `busy` (its button shows the spinner). */
const busyMethod = ref<string | null>(null);
const doing = (name: string, method: string) => busy.value === name && busyMethod.value === method;
const tab = ref<'cli' | 'api' | 'mcp' | 'connections'>(props.initialTab ?? 'cli');
const mcpCount = ref(0);
const apiCount = ref(0);
const connectionCount = ref(0);
const editing = ref<string | null>(null);
const newCli = ref('');
const note = ref<string | null>(null);
const remote = computed(() => !!api.remote);
const loaded = ref(false); // the first integrations.list answered (or failed)

async function load() {
  try {
    list.value = (await api.call<{ integrations: IntegrationInfo[] }>('integrations.list', {})).integrations;
    error.value = null;
  } catch (e) { error.value = errorText(e); }
  finally { loaded.value = true; }
}
/** Exactly the CLIs the person added or configured (they have an integration row); the rest of the catalog is reached through "+ Add CLI". */
const shown = computed(() => list.value.filter((i) => i.configured));

async function call(name: string, method: string, params: Record<string, unknown>) {
  if (busy.value !== null) return false;
  busy.value = name;
  busyMethod.value = method;
  error.value = null;
  try { await api.call(method, { name, ...params }); await load(); return true; }
  catch (e) { error.value = errorText(e); return false; }
  finally { busy.value = null; busyMethod.value = null; }
}

async function login(i: IntegrationInfo) {
  if (busy.value !== null) return;
  error.value = null;
  note.value = null;
  busy.value = i.name;
  busyMethod.value = 'integrations.openLogin';
  try {
    await api.call('integrations.openLogin', { name: i.name });
    note.value = `A terminal opened for ${i.label}. Sign in there, then open Edit and press Discover / Re-discover.`;
  } catch (e) { error.value = errorText(e); } finally { busy.value = null; busyMethod.value = null; }
}
async function remove(i: IntegrationInfo) {
  if (!window.confirm(`Remove ${i.label}? Agents can no longer use it.`)) return;
  await call(i.name, 'integrations.remove', {});
}
async function addCli() {
  const name = newCli.value.trim();
  if (!name) return;
  if (await call(name, 'integrations.detect', {})) { newCli.value = ''; editing.value = name; }
}
function offered(i: IntegrationInfo) {
  if (i.all) return 'every team';
  const names = i.teams.map((id) => props.teams.find((t) => t.id === id)?.name ?? `team ${id}`);
  return names.length ? names.join(', ') : null;
}
const countsText = (i: IntegrationInfo) => {
  const c = i.counts;
  if (!c) return null;
  const n = c.allowed + c.ask + c.off + c.undecided;
  return n === 0 ? 'no actions listed yet' : `${n} actions: ${c.allowed} allowed, ${c.ask} ask first`;
};
function closed() { editing.value = null; void load(); }

onMounted(load);
</script>

<template>
  <section class="integrations" data-test="integrations">
    <p class="muted intro">
      Your own command-line tools, web APIs and MCP servers, offered to your teams' agents. They run on this computer with your sign-in (made in your own
      terminal), never inside an agent's sandbox, so the agents only ever see the output. You choose what each may run; anything you have not
      decided asks you first, and anything that would reveal or change your sign-in is never allowed. Requests waiting for you show on the
      team's page and in the tray.
    </p>
    <div class="tabs" role="tablist">
      <button class="pill" :class="{ on: tab === 'cli' }" role="tab" data-test="tab-cli" @click="tab = 'cli'">CLIs ({{ shown.length }})</button>
      <button class="pill" :class="{ on: tab === 'api' }" role="tab" data-test="tab-api" @click="tab = 'api'">APIs ({{ apiCount }})</button>
      <button class="pill" :class="{ on: tab === 'mcp' }" role="tab" data-test="tab-mcp" @click="tab = 'mcp'">MCP servers ({{ mcpCount }})</button>
      <button class="pill" :class="{ on: tab === 'connections' }" role="tab" data-test="tab-connections" @click="tab = 'connections'">{{ remote ? 'Connections' : `Connections (${connectionCount})` }}</button>
    </div>

    <div v-show="tab === 'cli'" data-test="cli-tab">
      <LoadingNote v-if="!loaded" text="Loading integrations…" />
      <p v-if="error" class="error" data-test="integrations-error">{{ error }}</p>
      <p v-if="note" class="muted small" data-test="integrations-note">{{ note }}</p>
      <div v-for="i in shown" :key="i.name" class="cli" :class="{ on: i.enabled }" :data-test="`integration-${i.name}`">
        <header class="row">
          <div class="grow">
            <b>{{ i.label }}</b> <span class="muted mono">{{ i.name }}</span>
            <div class="small">
              <template v-if="i.path"><span class="mono muted">{{ i.path }}</span></template>
              <span v-else class="warn">not installed on this computer</span>
              <template v-if="i.status"> &middot; <span :class="i.status.startsWith('signed in') ? 'ok' : 'warn'">{{ i.status }}</span></template>
            </div>
            <div v-if="countsText(i)" class="small muted" data-test="counts">{{ countsText(i) }}</div>
            <div class="small" data-test="offered">
              <template v-if="offered(i)">Offered to {{ offered(i) }}</template>
              <span v-else class="muted">not offered to any team</span>
            </div>
          </div>
          <button v-if="i.login && !remote" :disabled="busy === i.name" :data-test="`integration-login-${i.name}`" @click="login(i)"><BusyLabel :busy="doing(i.name, 'integrations.openLogin')" text="Opening…">Login via terminal</BusyLabel></button>
          <button :data-test="`integration-edit-${i.name}`" @click="editing = i.name">Edit</button>
          <button class="link danger" :disabled="busy === i.name" :data-test="`integration-remove-${i.name}`" @click="remove(i)"><BusyLabel :busy="doing(i.name, 'integrations.remove')" text="Removing…">Remove</BusyLabel></button>
        </header>
      </div>
      <div class="row add-cli">
        <input v-model="newCli" class="mono" placeholder="a command name, e.g. vercel" data-test="integration-new" @keydown.enter="addCli" />
        <button :disabled="!newCli.trim() || busy !== null" data-test="integration-add" :aria-busy="busyMethod === 'integrations.detect'" @click="addCli"><BusyLabel :busy="busyMethod === 'integrations.detect'" text="Adding…">+ Add CLI</BusyLabel></button>
        <span class="muted small">It is looked for on this computer, then you choose what it may run.</span>
      </div>
    </div>

    <ApisPanel v-show="tab === 'api'" :api="api" :teams="teams" @count="apiCount = $event" />
    <McpServersPanel v-show="tab === 'mcp'" :api="api" :teams="teams" @count="mcpCount = $event" />
    <ConnectionsPanel v-show="tab === 'connections'" :api="api" @count="connectionCount = $event" />
    <IntegrationDialog v-if="editing" :api="api" kind="cli" :name="editing" :teams="teams" :initial-scope="null" @close="closed" @saved="load" />
    <TasksPanel :api="api" :teams="teams" />
  </section>
</template>

<style scoped>
.intro { font-size: 13px; margin: 0 0 12px; }
.tabs { display: flex; gap: 6px; margin-bottom: 10px; }
.pill { border: 1px solid var(--line); border-radius: 14px; padding: 3px 12px; background: #fff; }
.pill.on { background: var(--accent); color: #fff; border-color: var(--accent); }
.cli { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; margin-bottom: 10px; background: #fff; }
.cli.on { border-left: 4px solid var(--ok); }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.add-cli input { font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; min-width: 260px; }
.ok { color: var(--ok); } .warn { color: var(--warn); }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); font-weight: 400; }
.small { font-size: 12px; }
.link { border: 0; background: transparent; color: var(--accent); }
.link.danger { color: var(--bad); }
.add-cli { margin-top: 6px; }
.error { color: var(--bad); }
</style>
