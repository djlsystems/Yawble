<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ApiRow, Connection, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import IntegrationDialog from './IntegrationDialog.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * APIs: web services added by their address (and websites, which agents use in a browser window on this computer). The app reads the service's OpenAPI description (Discover) and lists its actions, which the
 * person allows, sets to ask first, or turns off. The sign-in is a Connection kept on this computer; agents never see it.
 */
const props = defineProps<{ api?: YawbleApi; teams: TeamSummary[] }>();
const emit = defineEmits<{ (e: 'count', n: number): void }>();
const api = resolveApi(props.api);
const remote = computed(() => !!api.remote);

const apis = ref<ApiRow[]>([]);
const error = ref<string | null>(null);
const busy = ref<string | null>(null);
/** The API's name being edited; '' = a new one. */
const editing = ref<string | null>(null);
/** The dialog is for a Website integration (Add website, or editing a website's card). */
const editingWebsite = ref(false);

const loaded = ref(false); // the first answer arrived (or failed)
async function load() {
  try { apis.value = await api.call<ApiRow[]>('integrations.apiList', {}); error.value = null; }
  catch (e) { error.value = errorText(e); }
  finally { loaded.value = true; }
}

const offered = (a: ApiRow) => {
  if (a.all) return 'every team';
  const names = a.teams.map((id) => props.teams.find((t) => t.id === id)?.name ?? `team ${id}`);
  return names.length ? names.join(', ') : null;
};
const countsText = (a: ApiRow) => {
  const c = a.counts;
  if (!c) return null;
  const n = c.allowed + c.ask + c.off + c.undecided;
  return n === 0 ? 'no actions listed yet' : `${n} actions: ${c.allowed} allowed, ${c.ask} ask first`;
};
function signInText(a: ApiRow) {
  if (a.connection) {
    const s = a.connection.status;
    return { name: a.connection.name, status: s === 'works' ? 'works' : s === 'needs_reconnect' ? 'needs reconnect' : 'untested', cls: s === 'works' ? 'ok' : s === 'needs_reconnect' ? 'bad' : 'muted' };
  }
  return null;
}
/** Why the sign-in is not "works" (needs reconnect, or untested with a reason, e.g. a check the window could not run). */
function reconnectReason(a: ApiRow) {
  if (!a.connection || a.connection.status === 'works') return null;
  return a.connection.statusReason ?? reasons.value.get(a.connection.id) ?? null;
}
const reasons = ref(new Map<number, string | null>());

async function remove(a: ApiRow) {
  if (busy.value) return;
  if (!window.confirm(`Remove ${a.name}? Agents can no longer use it.`)) return;
  let removeConnection = false;
  if (a.connection && !remote.value) {
    try {
      const all = await api.call<Connection[]>('integrations.connections', {});
      const c = all.find((x) => x.id === a.connection!.id);
      const others = (c?.usedBy ?? []).filter((n) => n !== a.name);
      if (c && others.length === 0) removeConnection = window.confirm(`Also delete the connection '${c.name}'?`);
    } catch { /* the connection stays; the removal itself still asks nothing more */ }
  }
  busy.value = a.name;
  error.value = null;
  try { await api.call('integrations.apiRemove', { name: a.name, removeConnection }); await load(); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = null; }
}
function closed() { editing.value = null; editingWebsite.value = false; void load(); }
function edit(a: ApiRow) { editingWebsite.value = a.kind === 'website'; editing.value = a.name; }
function add(website: boolean) { editingWebsite.value = website; editing.value = ''; }

async function loadReasons() {
  if (remote.value) return;
  try { for (const c of await api.call<Connection[]>('integrations.connections', {})) reasons.value.set(c.id, c.statusReason); }
  catch { /* the reason is only extra detail */ }
}

watch(apis, (v) => emit('count', v.length));
let off: (() => void) | null = null;
onMounted(async () => {
  // a connection saved, removed or marked "needs reconnect" elsewhere: the cards follow
  off = api.onEvent?.((e) => { if (e.event === 'connections.changed') { void load(); void loadReasons(); } }) ?? null;
  await load();
  await loadReasons();
});
onBeforeUnmount(() => off?.());
</script>

<template>
  <section class="apis" data-test="apis">
    <p class="muted small">
      Web services you add by their address, and websites agents use in a browser window on this computer. The app reads the service's description and lists what it can do; you choose what agents may
      do, and anything you have not decided asks you first. The sign-in stays on this computer: agents never see it.
    </p>
    <LoadingNote v-if="!loaded" text="Loading APIs…" />
    <p v-if="error" class="error" data-test="apis-error">{{ error }}</p>
    <div v-for="a in apis" :key="a.name" class="card" :data-test="`api-${a.name}`">
      <header class="row">
        <div class="grow">
          <b>{{ a.name }}</b> <span v-if="a.kind === 'website'" class="muted small" data-test="api-kind">Website</span> <span class="mono muted small" data-test="api-url">{{ a.baseUrl || 'service address not known yet' }}</span>
          <div class="small" data-test="api-signin">
            <template v-if="signInText(a)">
              Signs in with <b>{{ signInText(a)!.name }}</b>: <span :class="signInText(a)!.cls">{{ signInText(a)!.status }}</span>
              <span v-if="reconnectReason(a)" class="muted"> ({{ reconnectReason(a) }})</span>
            </template>
            <span v-else-if="a.kind === 'website'" class="muted">public site, no login</span>
            <span v-else-if="a.auth" class="warn">needs a connection</span>
            <span v-else class="muted">no sign-in set</span>
          </div>
          <div v-if="a.oauth && a.oauth.missingScopes.length" class="small warn" data-test="api-missing-scopes">
            Sign in again to allow the new actions (missing access: {{ a.oauth.missingScopes.join(', ') }})
          </div>
          <div v-if="a.status" class="small muted" data-test="api-status">{{ a.status }}</div>
          <div v-if="countsText(a)" class="small muted" data-test="counts">{{ countsText(a) }}</div>
          <div class="small" data-test="offered">
            <template v-if="offered(a)">Offered to {{ offered(a) }}</template>
            <span v-else class="muted">not offered to any team</span>
          </div>
        </div>
        <button :data-test="`api-edit-${a.name}`" @click="edit(a)">Edit</button>
        <button v-if="!remote" class="link danger" :disabled="busy === a.name" :data-test="`api-remove-${a.name}`" @click="remove(a)"><BusyLabel :busy="busy === a.name" text="Removing…">Remove</BusyLabel></button>
      </header>
    </div>
    <div v-if="!remote" class="row">
      <button data-test="api-add" @click="add(false)">+ Add API</button>
      <button data-test="website-add" @click="add(true)">+ Add website</button>
    </div>
    <p v-else class="muted small" data-test="apis-remote">APIs are added, removed and discovered on the computer itself.</p>
    <IntegrationDialog v-if="editing !== null" :key="(editingWebsite ? 'w:' : 'a:') + editing" :api="api" kind="api" :website="editingWebsite" :name="editing" :teams="teams" :initial-scope="null" @close="closed" @saved="load" />
  </section>
</template>

<style scoped>
.card { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; margin: 8px 0; background: #fff; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.ok { color: var(--ok); } .warn { color: var(--warn); } .bad { color: var(--bad); }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); font-weight: 400; } .small { font-size: 12px; }
.link { border: 0; background: transparent; color: var(--accent); }
.link.danger { color: var(--bad); }
.error { color: var(--bad); }
</style>
