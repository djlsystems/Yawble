<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useEscapeClose } from '../escapeClose';
import type { ApiRow, Connection, IntegrationInfo, McpServerInfo, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import ConnectionDialog from './ConnectionDialog.vue';
import DiscoverChecklist from './DiscoverChecklist.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/**
 * One integration (a CLI, an MCP server or an API) in one place: where it is, whether it is signed in, which teams it is offered
 * to, and the checklist of what its agents may run (Discover, scope, Other rules, Save live in the checklist).
 * An API (kind "api", name "" for a new one) also has its addresses, a Discover that saves them first, its sign-in, and a Save.
 */
const props = defineProps<{ api?: YawbleApi; kind: 'cli' | 'mcp' | 'api'; name: string; teams: TeamSummary[]; initialScope?: number | null; website?: boolean }>();
const emit = defineEmits<{ (e: 'close'): void; (e: 'saved'): void }>();
useEscapeClose(() => emit('close'));
const api = resolveApi(props.api);

const cliInfo = ref<IntegrationInfo | null>(null);
const mcpInfo = ref<McpServerInfo | null>(null);
const error = ref<string | null>(null);
const busy = ref(false);
/** What is running while busy (save, discover, oauth, signin, signin-save, replace:<var>, offer, login): that button shows the spinner. */
const doing = ref<string | null>(null);
watch(busy, (b) => { if (!b) doing.value = null; });
/** The first load (the row, its sign-in state) has answered. */
const loaded = ref(false);
const note = ref<string | null>(null);
const checklist = ref<InstanceType<typeof DiscoverChecklist> | null>(null);

const isMcp = computed(() => props.kind === 'mcp');
const isApi = computed(() => props.kind === 'api');
/** A Website integration: an API-shaped row (kind 'website') whose sign-in is a website login Connection and whose six actions are fixed. */
const isWebsite = computed(() => isApi.value && !!props.website);
/** What the login dropdown offers: website logins only. */
const websiteLogins = computed(() => connections.value.filter((c) => c.kind === 'website'));
/** What a web API's dropdown offers: never a website login (it signs in a website, not a web API). */
const apiConnections = computed(() => connections.value.filter((c) => c.kind !== 'website'));

// ---- API: the row, the address form, and the sign-in (an API is saved by its first Discover, and again by Save)
const apiInfo = ref<ApiRow | null>(null);
/** The API's name once it exists (props.name, or what the first save gave it). */
const apiName = ref(props.name);
const form = ref({ name: props.name, baseUrl: '', docsUrl: '' });
const connections = ref<Connection[]>([]);
/** The sign-in choice: '' = paste a key below, or a Connection's id. */
const pick = ref('');
const secretValues = ref<Record<string, string>>({});
const newConnection = ref(false);
const remoteSentence = 'Connections are managed on the computer itself.';
const mcpRemoteSentence = 'Sign-ins and imports are managed on the computer itself.';

// ---- MCP: a remote server's sign-in (done in the browser), and a local server's environment variables (secrets only masked)
const isRemoteServer = computed(() => mcpInfo.value?.transport === 'http');
const mcpSignInText = computed(() => {
  const m = mcpInfo.value;
  if (!m || m.transport !== 'http') return null;
  const type = m.auth?.type ?? 'none';
  if (type === 'none') return 'no sign-in';
  if (m.connection?.status === 'needs_reconnect') return 'needs you to sign in again';
  return type === 'oauth' ? 'signed in' : 'uses a saved token';
});
// ---- MCP, existing server, this computer only: replace a token or a secret value, change how a remote server signs in
const replaceToken = ref('');
const replaceValues = ref<Record<string, string>>({});
const editAuth = ref<'oauth' | 'token' | 'none' | null>(null);
const editOtherHeader = ref(false);
const editHeader = ref('');
const needsNewToken = computed(() => mcpInfo.value?.connection?.status === 'needs_reconnect' && ['bearer', 'header'].includes(mcpInfo.value?.auth?.type ?? ''));
const currentAuth = computed<'oauth' | 'token' | 'none'>(() => { const t = mcpInfo.value?.auth?.type ?? 'none'; return t === 'oauth' ? 'oauth' : t === 'none' ? 'none' : 'token'; });
function syncEdit() {
  const m = mcpInfo.value;
  if (!m || m.transport !== 'http') return;
  if (editAuth.value === null) {
    editAuth.value = currentAuth.value;
    editOtherHeader.value = m.auth?.type === 'header';
    editHeader.value = m.auth?.header ?? '';
  }
}
/** What mcpSave needs to keep a server as it is (a local one needs its command; its args are left as stored, the list shows them masked; the transport is never sent). */
function keepParams(): Record<string, unknown> {
  const m = mcpInfo.value!;
  return m.transport === 'http' ? { name: props.name } : { name: props.name, command: m.command };
}
async function saveSecret(params: Record<string, unknown>, what = 'signin-save') {
  if (busy.value) return;
  error.value = null;
  busy.value = true;
  doing.value = what;
  try {
    await api.call('integrations.mcpSave', { ...keepParams(), ...params });
    replaceToken.value = '';
    replaceValues.value = {};
    await load();
    emit('saved');
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}
const replaceValue = (varName: string) => saveSecret({ secrets: { [varName]: replaceValues.value[varName] ?? '' } }, `replace:${varName}`);
async function saveSignIn() {
  const choice = editAuth.value ?? currentAuth.value;
  const same = choice === currentAuth.value && (choice !== 'token' || (editOtherHeader.value ? mcpInfo.value?.auth?.type === 'header' && (mcpInfo.value.auth.header ?? '') === editHeader.value.trim() : mcpInfo.value?.auth?.type === 'bearer'));
  if (choice === 'token' && !replaceToken.value) { error.value = 'Paste the token first.'; return; }
  // the browser sign-in: nothing is saved first; the core sets the server's sign-in to oauth only when it succeeds
  if (choice === 'oauth' && !same) { await signInAgain(); return; }
  const params: Record<string, unknown> = {};
  if (!same) params.auth = choice === 'oauth' ? { type: 'oauth' } : choice === 'none' ? { type: 'none' }
    : (editOtherHeader.value && editHeader.value.trim() ? { type: 'header', header: editHeader.value.trim() } : { type: 'bearer' });
  if (choice === 'token') params.secrets = { token: replaceToken.value };
  await saveSecret(params);
}
async function signInAgain() {
  if (busy.value) return;
  error.value = null;
  busy.value = true;
  doing.value = 'signin';
  note.value = 'Finish signing in in your browser…';
  try { await api.call('integrations.mcpSignIn', { name: props.name }); await load(); emit('saved'); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = false; note.value = null; }
}

const all = computed(() => (isApi.value ? apiInfo.value?.all : isMcp.value ? mcpInfo.value?.all : cliInfo.value?.all) ?? false);
const teamIds = computed(() => (isApi.value ? apiInfo.value?.teams : isMcp.value ? mcpInfo.value?.teams : cliInfo.value?.teams) ?? []);
const status = computed(() => (isApi.value ? apiInfo.value?.status : isMcp.value ? mcpInfo.value?.status : cliInfo.value?.status) ?? null);
const login = computed(() => (isMcp.value || isApi.value ? null : cliInfo.value?.login ?? null));
const remote = computed(() => !!api.remote);
const header = computed(() => (isApi.value
  ? apiInfo.value?.baseUrl || (isWebsite.value ? 'a website' : 'a web service')
  : isMcp.value
    ? (mcpInfo.value?.transport === 'http' ? (mcpInfo.value.url ?? '') : [mcpInfo.value?.command, ...(mcpInfo.value?.args ?? [])].filter(Boolean).join(' '))
    : cliInfo.value?.path ?? 'not installed on this computer'));
const ready = computed(() => (isApi.value ? apiInfo.value !== null : isMcp.value ? mcpInfo.value !== null : !!cliInfo.value?.path));
const title = computed(() => (isApi.value ? (apiName.value || (isWebsite.value ? 'Add website' : 'Add API')) : props.name));
const signIn = computed(() => apiInfo.value?.signIn ?? null);
const signInTitle = computed(() => {
  const s = signIn.value;
  if (!s) return '';
  const who = apiInfo.value?.title || apiName.value;
  return s.fields.length === 1 && s.fields[0]!.secret ? `Paste your ${who} API key` : `Sign in to ${who}`;
});
const pasted = computed(() => Object.values(secretValues.value).some((v) => v.length > 0));

// ---- An API that signs in with OAuth 2: the person's own app, signed in through the browser. Remote viewers see the status only.
const oauth = computed(() => (!isWebsite.value ? apiInfo.value?.oauth ?? null : null));
function hostOfUrl(u: string | null | undefined) { try { return u ? new URL(u).host : ''; } catch { return u ?? ''; } }
const oauthHost = computed(() => hostOfUrl(oauth.value?.authorizeUrl) || hostOfUrl(oauth.value?.openIdConnectUrl) || 'the provider');
/** The spec gave no usable address: the person gives the sign-in page and token addresses. */
const oauthNeedsAddresses = computed(() => !!oauth.value && !oauth.value.authorizeUrl && !oauth.value.openIdConnectUrl);
const oauthClientId = ref('');
const oauthClientSecret = ref('');
const oauthAuthorize = ref('');
const oauthToken = ref('');
const oauthProgress = ref(false);
const oauthDone = ref<string | null>(null);
/** The API's own OAuth Connection (made for it alone): signing in again goes into it; otherwise Sign in makes a new Connection. */
const ownOAuth = computed(() => {
  const id = apiInfo.value?.connection?.id;
  const c = id !== undefined ? connections.value.find((x) => x.id === id) : undefined;
  return c && c.kind === 'oauth' && c.usedBy.every((n) => n.toLowerCase() === apiName.value.toLowerCase()) ? c : null;
});
const storedField = (key: string) => ownOAuth.value?.fields.find((f) => f.key === key);
// Sign in again: the app's client ID is the saved one, so a re-sign-in is one click
watch(ownOAuth, (c) => { if (c && !oauthClientId.value) oauthClientId.value = storedField('client_id')?.shown ?? ''; }, { immediate: true });
async function oauthSignIn() {
  if (remote.value || !apiName.value) return;
  error.value = null; oauthDone.value = null;
  if (!oauthClientId.value.trim()) { error.value = "Give your app's client ID."; return; }
  if (busy.value) return;
  busy.value = true; doing.value = 'oauth'; oauthProgress.value = true;
  try {
    await api.call('integrations.oauthSignIn', {
      api: apiName.value, clientId: oauthClientId.value.trim(),
      ...(oauthClientSecret.value ? { clientSecret: oauthClientSecret.value } : {}),
      ...(ownOAuth.value ? { connectionId: ownOAuth.value.id } : {}),
      ...(oauthNeedsAddresses.value ? { authorizeUrl: oauthAuthorize.value.trim(), tokenUrl: oauthToken.value.trim() } : {}),
    });
    oauthClientSecret.value = '';
    oauthDone.value = 'Signed in.';
    await loadConnections();
    await loadApi();
    pick.value = apiInfo.value?.connection ? String(apiInfo.value.connection.id) : pick.value;
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; oauthProgress.value = false; }
}

// ---- I3: a spec that names no sign-in. The person says how it signs in (a fallback, not the normal path), saved as the API's auth.
type AuthChoice = 'none' | 'bearer' | 'header' | 'query';
/** Shown when the API exists and its spec named no sign-in (no auth), or the person chose one here before. */
const askAuth = computed(() => !isWebsite.value && !!apiInfo.value && (!apiInfo.value.auth || !!apiInfo.value.auth.chosen));
const authChoice = ref<AuthChoice>('none');
const headerName = ref('X-API-Key');
const queryName = ref('api_key');
function syncAuth(row: ApiRow | null) {
  const a = row?.auth;
  if (!a || !a.chosen) { authChoice.value = 'none'; return; }
  if (a.type === 'header' || a.type === 'query' || a.type === 'bearer') authChoice.value = a.type;
  if (a.type === 'header' && a.name) headerName.value = a.name;
  if (a.type === 'query' && a.name) queryName.value = a.name;
}
async function saveAuth() {
  if (remote.value || !apiName.value) return;
  const c = authChoice.value;
  const name = c === 'header' ? headerName.value.trim() : c === 'query' ? queryName.value.trim() : '';
  if ((c === 'header' || c === 'query') && !name) { error.value = c === 'header' ? 'say which header carries the key' : 'say which query parameter carries the key'; return; }
  error.value = null;
  try {
    await api.call('integrations.apiSave', { name: apiName.value, auth: c === 'none' ? null : { type: c, ...(name ? { name } : {}) } });
    await loadApi();
  } catch (e) { error.value = errorText(e); }
}

function applyApi(row: ApiRow | null) {
  apiInfo.value = row;
  syncAuth(row);
  if (!row) return;
  if (!form.value.baseUrl) form.value.baseUrl = row.baseUrl;
  if (!form.value.docsUrl) form.value.docsUrl = row.docsUrl ?? '';
  if (pick.value === '' && row.connection) pick.value = String(row.connection.id);
}
async function loadApi() {
  const row = (await api.call<ApiRow[]>('integrations.apiList', {})).find((a) => a.name.toLowerCase() === apiName.value.toLowerCase()) ?? null;
  applyApi(row);
}
async function loadConnections() {
  if (remote.value) return;
  try { connections.value = await api.call<Connection[]>('integrations.connections', {}); } catch { connections.value = []; }
}

/** Saves the addresses (the first time this creates the API), then reads its description. */
async function discover() {
  if (remote.value || busy.value) return;
  error.value = null;
  busy.value = true;
  doing.value = 'discover';
  try {
    const name = apiName.value || form.value.name.trim();
    let before = new Set<string>();
    if (!apiName.value) {
      try { before = new Set((await api.call<ApiRow[]>('integrations.apiList', {})).map((a) => a.name.toLowerCase())); } catch { /* then Cancel keeps it, to be safe */ before = new Set(['*']); }
    }
    if (!apiName.value && name && before.has(name.toLowerCase())) {
      error.value = `an API named ${name} already exists; edit it instead`;
      return;
    }
    const r = await api.call<{ ok: boolean; name: string }>('integrations.apiSave', {
      ...(name ? { name } : {}),
      ...(isWebsite.value ? { kind: 'website', url: form.value.baseUrl.trim(), connectionId: pick.value !== '' ? Number(pick.value) : null } : {
        baseUrl: form.value.baseUrl.trim(),
        ...(form.value.docsUrl.trim() ? { docsUrl: form.value.docsUrl.trim() } : {}),
      }),
      // Add API never edits an existing one, also when the name comes from the service's host (the core refuses it)
      ...(!apiName.value ? { create: true } : {}),
    });
    // Adding an address that is already an API only updates it: Cancel must not remove that one.
    if (!apiName.value && (before.has('*') || before.has(r.name.toLowerCase()))) kept = true;
    apiName.value = r.name;
    await loadApi();
    await nextTick();
    await checklist.value?.start();
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}

function connectionSaved(c: Connection) {
  connections.value = [...connections.value.filter((x) => x.id !== c.id), c];
  pick.value = String(c.id);
}
function onPick(v: string) {
  if (v === 'new') { newConnection.value = true; return; }
  pick.value = v;
}
function uniqueConnectionName() {
  const base = `${apiInfo.value?.title || apiName.value} key`;
  const taken = new Set(connections.value.map((c) => c.name.toLowerCase()));
  let n = base;
  for (let i = 2; taken.has(n.toLowerCase()); i++) n = `${base} ${i}`;
  return n;
}

/** Saves the sign-in (a pasted key becomes a Connection), the API's addresses, and the checklist's changes. */
async function saveAll() {
  if (remote.value || busy.value) return;
  error.value = null;
  busy.value = true;
  doing.value = 'save';
  try {
    let connectionId: number | undefined;
    if (pick.value !== '') connectionId = Number(pick.value);
    else if (!isWebsite.value && pasted.value && signIn.value) {
      const secrets: Record<string, string> = {};
      const fields = signIn.value.fields.map((f) => {
        const v = secretValues.value[f.key] ?? '';
        if (f.secret) { if (v) secrets[f.key] = v; return { key: f.key, label: f.label, secret: true }; }
        return { key: f.key, label: f.label, secret: false, value: v };
      });
      const c = await api.call<Connection>('integrations.connectionSave', { name: uniqueConnectionName(), fields, secrets, reuseIdentical: true });
      connectionId = c.id;
      secretValues.value = {};
      connections.value = [...connections.value.filter((x) => x.id !== c.id), c];
      pick.value = String(c.id);
    }
    secretValues.value = {}; // a pasted key ignored because a saved connection was picked is not kept
    await api.call('integrations.apiSave', isWebsite.value
      ? { kind: 'website', name: apiName.value, url: form.value.baseUrl.trim(), connectionId: connectionId ?? null }
      : {
        name: apiName.value,
        baseUrl: form.value.baseUrl.trim(),
        ...(form.value.docsUrl.trim() ? { docsUrl: form.value.docsUrl.trim() } : {}),
        ...(connectionId !== undefined ? { connectionId } : {}),
      });
    // a failed rules save keeps the dialog open (the checklist shows why), so the choices are not lost
    if ((checklist.value?.dirtyCount ?? 0) > 0 && !(await checklist.value?.save())) return;
    await loadApi();
    kept = true;
    emit('saved');
    emit('close');
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}

async function load() {
  try {
    if (isApi.value) { if (apiName.value) await loadApi(); await loadConnections(); }
    else if (isMcp.value) mcpInfo.value = (await api.call<McpServerInfo[]>('integrations.mcpList', {})).find((s) => s.name === props.name) ?? null;
    else cliInfo.value = (await api.call<{ integrations: IntegrationInfo[] }>('integrations.list', {})).integrations.find((i) => i.name === props.name) ?? null;
  } catch (e) { error.value = errorText(e); } finally { loaded.value = true; }
}

async function offer(params: Record<string, unknown>) {
  if (busy.value) return;
  busy.value = true;
  doing.value = 'offer';
  error.value = null;
  try {
    await api.call(isApi.value ? 'integrations.apiEnable' : isMcp.value ? 'integrations.mcpEnable' : 'integrations.enable', { name: isApi.value ? apiName.value : props.name, ...params });
    await load();
    emit('saved');
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}
const setAll = (on: boolean) => offer({ all: on });
const setTeam = (teamId: number, on: boolean) => offer({ teamId, on });

async function openLogin() {
  if (busy.value) return;
  error.value = null;
  note.value = null;
  busy.value = true;
  doing.value = 'login';
  try {
    const r = await api.call<{ ok: boolean; command: string }>('integrations.openLogin', { name: props.name });
    note.value = `A terminal opened. Sign in there${r?.command ? ` (${r.command})` : ''}, then press Discover / Re-discover.`;
  } catch (e) { error.value = errorText(e); } finally { busy.value = false; }
}

/** A discover ran its CLI's status command too: show the new sign-in state here and on the card. */
async function discovered() {
  await load();
  emit('saved');
}

/** True once Save has finished: the API is kept. */
let kept = false;
async function close() {
  const dirty = checklist.value?.dirtyCount ?? 0;
  if (dirty > 0 && !window.confirm('You have unsaved changes. Close and lose them?')) return;
  // Cancel removes an API this session created (a new one, not Edit); its connection, if any, is never removed.
  if (isApi.value && !props.name && apiName.value && !kept && !remote.value) {
    try { await api.call('integrations.apiRemove', { name: apiName.value, removeConnection: false }); } catch (e) { error.value = errorText(e); return; }
  }
  emit('close');
}

onMounted(async () => { await load(); syncEdit(); });
</script>

<template>
  <div class="overlay" data-test="integration-dialog" role="dialog" aria-modal="true" :aria-label="title">
    <div class="box">
      <header class="row head">
        <div class="grow">
          <h3>{{ title }}</h3>
          <div class="mono muted small" data-test="dialog-header">{{ header }}</div>
        </div>
        <button data-test="dialog-close" @click="close">{{ isApi ? 'Cancel' : 'Close' }}</button>
        <button v-if="isApi" class="primary" :disabled="busy || remote || !apiName" data-test="api-save" :aria-busy="doing === 'save'" @click="saveAll"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
      </header>
      <p v-if="error" class="error" data-test="dialog-error">{{ error }}</p>
      <LoadingNote v-if="!loaded && !error && (!isApi || apiName)" :text="isApi ? 'Loading the API…' : isMcp ? 'Loading the MCP server…' : 'Loading the CLI…'" />

      <section v-if="isApi" class="apiform" data-test="api-form">
        <label class="small fld">{{ isWebsite ? 'Site address' : 'Service URL' }}
          <input v-model="form.baseUrl" class="mono" :placeholder="isWebsite ? 'https://intranet.example.com' : 'https://api.example.com'" data-test="api-base-url" />
        </label>
        <label v-if="!isWebsite" class="small fld">Documentation URL
          <input v-model="form.docsUrl" class="mono" placeholder="https://example.com/docs or the spec's address" data-test="api-docs-url" />
        </label>
        <label class="small fld">Name
          <input v-model="form.name" :disabled="!!apiName" :placeholder="isWebsite ? 'the site host' : 'from the spec'" data-test="api-name" />
        </label>
        <div class="row">
          <button :disabled="busy || remote || (!form.baseUrl.trim() && !form.docsUrl.trim())" data-test="api-discover" :aria-busy="doing === 'discover'" @click="discover"><BusyLabel :busy="doing === 'discover'" text="Discovering…">Discover</BusyLabel></button>
          <span v-if="remote" class="muted small" data-test="api-remote">{{ remoteSentence }}</span>
          <span v-else-if="apiInfo?.status" class="small" :class="apiInfo.status.startsWith('not working') ? 'warn' : 'ok'" data-test="api-result">{{ apiInfo.status }}</span>
        </div>

        <div v-if="askAuth && !remote" class="signin-box" data-test="api-auth-choice">
          <b class="small">How does this API sign in?</b>
          <p class="muted small">Its description does not say. If you are not sure, leave it on None.</p>
          <label class="check small"><input v-model="authChoice" type="radio" value="none" data-test="api-auth-none" @change="saveAuth" /> None</label>
          <label class="check small"><input v-model="authChoice" type="radio" value="bearer" data-test="api-auth-bearer" @change="saveAuth" /> A token (sent as Authorization: Bearer)</label>
          <label class="check small"><input v-model="authChoice" type="radio" value="header" data-test="api-auth-header" @change="saveAuth" /> An API key in a header</label>
          <label v-if="authChoice === 'header'" class="small fld indent">Header name
            <input v-model="headerName" class="mono" data-test="api-auth-header-name" @change="saveAuth" />
          </label>
          <label class="check small"><input v-model="authChoice" type="radio" value="query" data-test="api-auth-query" @change="saveAuth" /> An API key in the address (a query parameter)</label>
          <label v-if="authChoice === 'query'" class="small fld indent">Parameter name
            <input v-model="queryName" class="mono" data-test="api-auth-query-name" @change="saveAuth" />
          </label>
        </div>

        <div v-if="oauth && !remote" class="signin-box" data-test="api-oauth">
          <b class="small" data-test="api-oauth-title">Sign in with {{ oauthHost }}</b>
          <p v-if="oauth.missingScopes.length" class="small warn" data-test="api-oauth-missing">Sign in again to allow the new actions</p>
          <div v-if="oauth.neededScopes.length" class="small" data-test="api-oauth-scopes">
            The actions you ticked need:
            <span v-for="s in oauth.neededScopes" :key="s" class="chip mono" :data-test="`api-oauth-scope-${s}`">{{ s }}</span>
          </div>
          <template v-if="oauthNeedsAddresses">
            <label class="small fld">Sign-in page address <input v-model="oauthAuthorize" class="mono" data-test="api-oauth-authorize" /></label>
            <label class="small fld">Token address <input v-model="oauthToken" class="mono" data-test="api-oauth-token" /></label>
          </template>
          <label class="small fld">Your app's client ID <input v-model="oauthClientId" class="mono" autocomplete="off" data-test="api-oauth-client-id" /></label>
          <label class="small fld">Client secret (if your app has one)
            <input v-model="oauthClientSecret" type="password" autocomplete="off" :placeholder="storedField('client_secret')?.set ? 'saved; leave empty to keep it' : ''" data-test="api-oauth-client-secret" />
          </label>
          <p class="muted small">Register this redirect address in your app: <code class="mono">http://127.0.0.1/callback</code> (any port)</p>
          <div class="row">
            <button v-if="oauth.missingScopes.length" class="primary" :disabled="busy || !apiName" data-test="api-oauth-signin" @click="oauthSignIn"><BusyLabel :busy="doing === 'oauth'" text="Signing in…">Sign in again</BusyLabel></button>
            <button v-else :disabled="busy || !apiName" data-test="api-oauth-signin" @click="oauthSignIn"><BusyLabel :busy="doing === 'oauth'" text="Signing in…">Sign in</BusyLabel></button>
            <span v-if="oauthProgress" class="small" data-test="api-oauth-progress">Finish signing in in your browser…</span>
            <span v-else-if="oauthDone" class="small ok" data-test="api-oauth-done">{{ oauthDone }}</span>
          </div>
        </div>
        <p v-else-if="oauth && remote" class="muted small" data-test="api-oauth-remote">
          {{ apiInfo?.connection ? `Signed in with ${apiInfo.connection.name}: ${apiInfo.connection.status === 'works' ? 'works' : apiInfo.connection.status === 'needs_reconnect' ? 'needs reconnect' : 'untested'}. ` : '' }}{{ remoteSentence }}
        </p>
        <div v-if="signIn && !oauth && !remote && !isWebsite" class="signin-box" data-test="api-signin">
          <b class="small">{{ signInTitle }}</b>
          <p v-if="signIn.description" class="muted small" data-test="api-signin-description">{{ signIn.description }}</p>
          <div v-if="pick === ''" class="secrets">
            <label v-for="f in signIn.fields" :key="f.key" class="small fld">{{ f.label }}
              <input v-model="secretValues[f.key]" :type="f.secret ? 'password' : 'text'" autocomplete="off" :placeholder="f.secret ? 'paste it here' : ''" :data-test="`api-secret-${f.key}`" />
            </label>
          </div>
          <label class="small">Or use a saved connection
            <select :value="pick" data-test="api-connection" @change="onPick(($event.target as HTMLSelectElement).value)">
              <option value="">Paste a new one above</option>
              <option v-for="c in apiConnections" :key="c.id" :value="String(c.id)">{{ c.name }}</option>
              <option value="new">New connection…</option>
            </select>
          </label>
        </div>
        <div v-if="isWebsite && !remote" class="signin-box" data-test="website-login">
          <b class="small">Website login</b>
          <p class="muted small">Agents use the site signed in as this login. Pages outside the site are never opened.</p>
          <label class="small">Login
            <select :value="pick" data-test="website-login-pick" @change="onPick(($event.target as HTMLSelectElement).value)">
              <option value="">None (public site)</option>
              <option v-for="c in websiteLogins" :key="c.id" :value="String(c.id)">{{ c.name }}</option>
              <option value="new">New website login…</option>
            </select>
          </label>
        </div>
        <p v-else-if="isWebsite" class="muted small" data-test="website-login-remote">{{ remoteSentence }}</p>
        <p v-else-if="signIn && !oauth && remote" class="muted small" data-test="api-signin-remote">{{ remoteSentence }}</p>
      </section>

      <div v-if="!isApi || apiName">
        <div class="small signin" data-test="dialog-status">
          <template v-if="!isApi && loaded">
            <span v-if="status" :class="status.startsWith('signed in') || status.startsWith('running') ? 'ok' : 'warn'">{{ status }}</span>
            <span v-else class="muted">sign-in not checked yet</span>
          </template>
          <template v-if="login">
            <button v-if="!remote" data-test="dialog-login" :disabled="busy" :aria-busy="doing === 'login'" @click="openLogin"><BusyLabel :busy="doing === 'login'" text="Opening…">Login via terminal</BusyLabel></button>
            <span v-else data-test="dialog-login-remote">Sign in on the computer: <code class="mono">{{ login }}</code></span>
          </template>
          <span v-if="note" class="muted" data-test="dialog-login-note">{{ note }}</span>
        </div>

        <div v-if="isMcp && isRemoteServer" class="small signin" data-test="mcp-remote-signin">
          Sign-in: <span :class="mcpSignInText === 'needs you to sign in again' ? 'warn' : 'muted'">{{ mcpSignInText }}</span>
          <button v-if="!remote && mcpInfo?.auth?.type === 'oauth'" :disabled="busy" data-test="mcp-signin-again" @click="signInAgain"><BusyLabel :busy="doing === 'signin'" text="Signing in…">Sign in again</BusyLabel></button>
          <span v-else-if="remote" class="muted" data-test="mcp-signin-remote">{{ mcpRemoteSentence }}</span>
        </div>
        <div v-if="isMcp && !isRemoteServer && mcpInfo?.env?.length" class="small signin" data-test="mcp-env">
          <b>Environment</b>
          <span v-for="v in mcpInfo.env" :key="v.name" class="mono" :data-test="`mcp-env-${v.name}`">{{ v.name }}={{ v.secret ? (v.set ? (v.shown ?? 'set') : 'not set') : v.value }}</span>
        </div>
        <div v-if="isMcp && mcpInfo && !remote" class="small" data-test="mcp-secrets">
          <fieldset v-if="isRemoteServer" class="signin-box">
            <legend>How it signs in</legend>
            <label class="check"><input v-model="editAuth" type="radio" value="oauth" data-test="edit-signin-oauth" /> The server signs me in</label>
            <label class="check"><input v-model="editAuth" type="radio" value="token" data-test="edit-signin-token" /> A token</label>
            <div v-if="editAuth === 'token'" class="indent" :class="{ needed: needsNewToken }" data-test="replace-token-box">
              <span v-if="needsNewToken" class="warn" data-test="needs-new-token">needs a new token</span>
              <input v-model="replaceToken" type="password" autocomplete="off" aria-label="Replace token" placeholder="Replace token" data-test="replace-token" />
              <button v-if="!editOtherHeader" class="link" @click="editOtherHeader = true">use another header</button>
              <input v-else v-model="editHeader" class="mono" aria-label="Header name" placeholder="X-API-Key" data-test="edit-header" />
            </div>
            <label class="check"><input v-model="editAuth" type="radio" value="none" data-test="edit-signin-none" /> No sign-in</label>
            <button :disabled="busy" data-test="save-signin" @click="saveSignIn"><BusyLabel :busy="doing === 'signin-save' || doing === 'signin'" text="Saving…">Save sign-in</BusyLabel></button>
          </fieldset>
          <div v-for="v in mcpInfo.env.filter((e) => e.secret)" :key="v.name" class="row" :data-test="`replace-row-${v.name}`">
            <span class="mono">{{ v.name }}</span>
            <input v-model="replaceValues[v.name]" type="password" autocomplete="off" :aria-label="`Replace value of ${v.name}`" placeholder="Replace value" :data-test="`replace-value-${v.name}`" />
            <button :disabled="busy || !replaceValues[v.name]" :data-test="`replace-save-${v.name}`" @click="replaceValue(v.name)"><BusyLabel :busy="doing === `replace:${v.name}`" text="Saving…">Replace value</BusyLabel></button>
          </div>
        </div>

        <div class="row teams" data-test="dialog-offer">
          <b class="small">Offer to</b>
          <label class="check"><input type="checkbox" :checked="all" :disabled="busy || !ready" data-test="dialog-all"
            @change="setAll(($event.target as HTMLInputElement).checked)" /> Every team</label>
          <label v-for="t in teams" :key="t.id" class="check"><input type="checkbox" :checked="all || teamIds.includes(t.id)" :disabled="busy || all || !ready"
            :data-test="`dialog-team-${t.id}`" @change="setTeam(t.id, ($event.target as HTMLInputElement).checked)" /> {{ t.name }}</label>
        </div>

        <DiscoverChecklist ref="checklist" :api="api" :kind="kind" :cli="isApi ? apiName : name" :teams="teams" :team-ids="teamIds" :all="all"
          :initial-scope="initialScope ?? null" :auto-start="!isApi" @discovered="discovered" />
      </div>
      <ConnectionDialog v-if="newConnection" :api="api" :start-kind="isWebsite ? 'website' : undefined" :secret-only="!isWebsite"
        :start-site="isWebsite ? form.baseUrl.trim() || undefined : undefined" @close="newConnection = false" @saved="connectionSaved" />
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: rgba(0, 0, 0, 0.35); display: flex; align-items: flex-start; justify-content: center; padding: 28px 16px; overflow: auto; z-index: 50; }
.box { background: #fff; border-radius: 12px; padding: 14px 16px; width: min(900px, 100%); box-sizing: border-box; }
h3 { margin: 0; font-size: 16px; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 0; }
.head { align-items: flex-start; }
.signin { margin: 6px 0; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
.teams { gap: 14px; font-size: 13px; margin: 6px 0; }
.check { display: flex; gap: 5px; align-items: center; }
.apiform { margin: 8px 0; }
.fld { display: block; margin: 4px 0; }
.fld input { display: block; width: 100%; box-sizing: border-box; font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; margin-top: 2px; }
.signin-box { border: 1px solid var(--line-soft); border-radius: 8px; padding: 8px 10px; margin: 8px 0; }
.indent { margin-left: 22px; }
.needed { background: #fff6e0; border-radius: 6px; padding: 2px 4px; }
fieldset { border: 0; margin: 0; padding: 0; }
fieldset.signin-box { border: 1px solid var(--line-soft); padding: 8px 10px; }
.link { border: 0; background: transparent; color: var(--accent); }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); } .small { font-size: 12px; } .ok { color: var(--ok); } .warn { color: var(--warn); } .error { color: var(--bad); }
.chip { display: inline-block; margin: 0 4px 2px 0; padding: 0 6px; border: 1px solid var(--line); border-radius: 9px; }
</style>
