<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import BusyLabel from './BusyLabel.vue';
import { useEscapeClose } from '../escapeClose';
import type { Connection, YawbleApi, OAuthDiscoverResult, OAuthSignInResult } from '../../../shared/api';
import { errorText, resolveApi } from '../api';

/**
 * A Connection: a name and the sign-in values (secret by default) that APIs use. The app never receives a stored secret: when editing,
 * a secret box is empty with the masked value as its placeholder, and leaving it empty keeps what is stored. Replace clears it for typing.
 */
const props = defineProps<{
  api?: YawbleApi; connection?: Connection | null; startKind?: 'secret' | 'website' | 'oauth';
  /** true: only an API key or token can be made here (no kind choice) */ secretOnly?: boolean;
  /** a new website login's site, prefilled (Add website's address) */ startSite?: string;
}>();
const emit = defineEmits<{ (e: 'close'): void; (e: 'saved', c: Connection): void }>();
useEscapeClose(() => emit('close'));
const api = resolveApi(props.api);

interface FieldRow { key: string; label: string; secret: boolean; value: string; existing: boolean; shown: string | null; replacing: boolean; auto?: boolean }

const editing = props.connection ?? null;
const name = ref(editing?.name ?? '');
/** 'secret' = an API key or token; 'website' = a login to a website, signed in once in the app's own browser session (no secret here). */
const kind = ref<'secret' | 'website' | 'oauth'>(editing ? (editing.kind === 'website' ? 'website' : editing.kind === 'oauth' ? 'oauth' : 'secret') : (props.startKind ?? 'secret'));
const site = ref(editing?.kind === 'website' ? (editing.fields.find((f) => f.key === 'site')?.shown ?? '') : (props.startSite ?? ''));
const remote = !!api.remote;
/** The saved website login, once saved: Sign in and Done need its id. */
const savedLogin = ref<Connection | null>(editing?.kind === 'website' ? editing : null);
const signingIn = ref(false);
// the window was closed by hand (or the check ran): the status changed, so there is nothing to press Done for
let off: (() => void) | null = null;
onMounted(() => { off = api.onEvent?.((e) => { if (e.event === 'connections.changed') signingIn.value = false; }) ?? null; });
onBeforeUnmount(() => off?.());
const fields = ref<FieldRow[]>(editing
  ? editing.fields.map((f) => ({ key: f.key, label: f.label, secret: f.secret, value: f.secret ? '' : (f.shown ?? ''), existing: true, shown: f.shown, replacing: false }))
  : [{ key: 'token', label: 'API key or token', secret: true, value: '', existing: false, shown: null, replacing: true }]);
const error = ref<string | null>(null);
const busy = ref(false);
/** What is running while busy (find, save, oauth, signin, done): that button shows the spinner. */
const doing = ref<string | null>(null);
watch(busy, (b) => { if (!b) doing.value = null; });

function slug(label: string) { return label.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, ''); }
function addField() {
  const used = new Set(fields.value.map((f) => f.key));
  const base = 'field';
  let key = base + (fields.value.length + 1);
  for (let n = fields.value.length + 1; used.has(key); n++) key = base + n;
  fields.value = [...fields.value, { key, label: '', secret: true, value: '', existing: false, shown: null, replacing: true, auto: true }];
}
function removeField(i: number) { fields.value = fields.value.filter((_, j) => j !== i); }
function replace(f: FieldRow) { f.replacing = true; f.value = ''; }

async function saveWebsite() {
  error.value = null;
  if (!site.value.trim()) { error.value = 'Give the address of the site.'; return; }
  if (busy.value) return;
  busy.value = true; doing.value = 'save';
  try {
    const c = await api.call<Connection>('integrations.connectionSave', {
      kind: 'website', site: site.value.trim(),
      ...(name.value.trim() ? { name: name.value.trim() } : {}),
      ...(savedLogin.value ? { id: savedLogin.value.id } : {}),
    });
    savedLogin.value = c;
    name.value = c.name;
    emit('saved', c);
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}
async function signIn() {
  if (!savedLogin.value) return;
  error.value = null;
  if (busy.value) return;
  busy.value = true; doing.value = 'signin';
  try { await api.call('integrations.websiteSignIn', { connectionId: savedLogin.value.id }); signingIn.value = true; }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}
async function signInDone() {
  if (!savedLogin.value) return;
  error.value = null;
  if (busy.value) return;
  busy.value = true; doing.value = 'done';
  try { await api.call('integrations.websiteSignInDone', { connectionId: savedLogin.value.id }); signingIn.value = false; }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}

// ---- OAuth sign-in: the person's own app, signed in through the browser. Remote viewers cannot do it (the core refuses).
const field = (key: string) => editing?.kind === 'oauth' ? (editing.fields.find((f) => f.key === key)?.shown ?? '') : '';
const provider = ref(field('provider'));
const found = ref<boolean | null>(null);
const issuerHost = ref('');
const offered = ref<string[]>([]);
const scopeFilter = ref('');
const ticked = ref(new Set<string>());
const advanced = ref(editing?.kind === 'oauth');
const authorizeUrl = ref(field('authorize_endpoint'));
const tokenUrl = ref(field('token_endpoint'));
const scopesText = ref(field('granted_scopes'));
const clientId = ref(field('client_id'));
const clientSecret = ref('');
const hasSecret = editing?.kind === 'oauth' && !!editing.fields.find((f) => f.key === 'client_secret')?.set;
/** An MCP server's sign-in (bound to its resource): signed in again from the server, never here; its name and secret may still be saved. */
const serverBound = editing?.kind === 'oauth' && editing.fields.some((f) => f.key === 'resource' && !f.secret && !!f.shown);
const oauthProgress = ref(false);
const oauthResult = ref<string | null>(null);
const redirect = 'http://127.0.0.1/callback';
const visibleScopes = computed(() => {
  const f = scopeFilter.value.trim().toLowerCase();
  return f ? offered.value.filter((s) => s.toLowerCase().includes(f)) : offered.value;
});
function hostOf(u: string) { try { return new URL(u).host; } catch { return u; } }
async function find() {
  error.value = null;
  if (!provider.value.trim()) { error.value = 'Give the provider address.'; return; }
  if (busy.value) return;
  busy.value = true; doing.value = 'find';
  try {
    const r = await api.call<OAuthDiscoverResult>('integrations.oauthDiscover', { provider: provider.value.trim() });
    if (r.found) {
      found.value = true; issuerHost.value = hostOf(r.issuer); offered.value = r.scopes;
      authorizeUrl.value = r.authorizeUrl; tokenUrl.value = r.tokenUrl; ticked.value = new Set();
    } else { found.value = false; offered.value = []; advanced.value = true; }
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}
function tick(s: string, on: boolean) { const n = new Set(ticked.value); if (on) n.add(s); else n.delete(s); ticked.value = n; }
async function copyRedirect() { try { await navigator.clipboard.writeText(redirect); } catch { /* the address is on screen */ } }
async function oauthSignIn() {
  error.value = null; oauthResult.value = null;
  if (!clientId.value.trim()) { error.value = "Give your app's client ID."; return; }
  const scopes = [...new Set([...ticked.value, ...scopesText.value.split(/[\s,]+/).filter(Boolean)])];
  if (busy.value) return;
  busy.value = true; doing.value = 'oauth'; oauthProgress.value = true;
  try {
    const r = await api.call<OAuthSignInResult>('integrations.oauthSignIn', {
      ...(editing ? { connectionId: editing.id } : {}),
      ...(name.value.trim() ? { name: name.value.trim() } : {}),
      ...(provider.value.trim() ? { provider: provider.value.trim() } : {}),
      authorizeUrl: authorizeUrl.value.trim(), tokenUrl: tokenUrl.value.trim(), clientId: clientId.value.trim(),
      ...(clientSecret.value ? { clientSecret: clientSecret.value } : {}), scopes,
    });
    clientSecret.value = '';
    let granted: number | null = null;
    try {
      const c = (await api.call<Connection[]>('integrations.connections', {})).find((x) => x.id === r.connection.id);
      if (c) { granted = (c.fields.find((f) => f.key === 'granted_scopes')?.shown ?? '').split(/[\s,]+/).filter(Boolean).length; emit('saved', c); }
    } catch { /* the card follows connections.changed */ }
    oauthResult.value = `${r.connection.status === 'works' ? 'works' : r.connection.status === 'needs_reconnect' ? 'needs reconnect' : 'untested'}${granted !== null ? `, ${granted} ${granted === 1 ? 'scope' : 'scopes'} granted` : ''}`;
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; oauthProgress.value = false; }
}

/** Saves an existing sign-in's name (and a newly typed client secret; empty keeps the stored one) without signing in again. */
async function saveOAuth() {
  if (!editing) return;
  error.value = null;
  if (!name.value.trim()) { error.value = 'Give the connection a name.'; return; }
  if (busy.value) return;
  busy.value = true; doing.value = 'save';
  try {
    const c = await api.call<Connection>('integrations.connectionSave', {
      id: editing.id, name: name.value.trim(), secrets: clientSecret.value ? { client_secret: clientSecret.value } : {},
    });
    clientSecret.value = '';
    emit('saved', c);
    emit('close');
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}

async function save() {
  if (kind.value === 'website') { await saveWebsite(); return; }
  error.value = null;
  if (!name.value.trim()) { error.value = 'Give the connection a name.'; return; }
  const used = new Set<string>();
  const out = fields.value.map((f) => {
    let key = f.auto ? (slug(f.label) || f.key) : f.key;
    while (used.has(key)) key += '_';
    used.add(key);
    return { f, key };
  });
  const secrets: Record<string, string> = {};
  for (const { f, key } of out) if (f.secret && f.value.length > 0) secrets[key] = f.value;
  const params = {
    ...(editing ? { id: editing.id } : {}),
    name: name.value.trim(),
    fields: out.map(({ f, key }) => ({ key, label: f.label.trim() || key, secret: f.secret, ...(f.secret ? {} : { value: f.value }) })),
    secrets,
  };
  if (busy.value) return;
  busy.value = true; doing.value = 'save';
  try {
    const c = await api.call<Connection>('integrations.connectionSave', params);
    emit('saved', c);
    emit('close');
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; }
}
</script>

<template>
  <div class="overlay" data-test="connection-dialog" role="dialog" aria-modal="true" aria-label="Connection">
    <div class="box">
      <h3>{{ editing ? 'Edit connection' : 'Add connection' }}</h3>
      <p class="muted small">A sign-in that APIs can share. It is kept on this computer; agents never see it.</p>
      <p v-if="error" class="error" data-test="connection-error">{{ error }}</p>
      <div v-if="!editing && !secretOnly" class="row" data-test="connection-kind">
        <label class="check small"><input v-model="kind" type="radio" value="secret" :disabled="!!savedLogin" data-test="connection-kind-secret" /> API key or token</label>
        <label class="check small"><input v-model="kind" type="radio" value="website" :disabled="!!savedLogin" data-test="connection-kind-website" /> Website login</label>
        <label v-if="!remote" class="check small"><input v-model="kind" type="radio" value="oauth" data-test="connection-kind-oauth" /> OAuth sign-in</label>
      </div>
      <label class="small block">Name
        <input v-model="name" :placeholder="kind === 'website' ? 'optional, e.g. Intranet login' : 'e.g. Stripe key'" data-test="connection-name" />
      </label>
      <template v-if="kind === 'oauth'">
        <p v-if="remote" class="muted small" data-test="oauth-remote">Sign in on the computer itself.</p>
        <template v-else>
          <div class="row">
            <label class="small grow">Provider address
              <input v-model="provider" class="mono" placeholder="https://accounts.example.com" data-test="oauth-provider" />
            </label>
            <button :disabled="busy" data-test="oauth-find" @click="find"><BusyLabel :busy="doing === 'find'" text="Finding…">Find</BusyLabel></button>
          </div>
          <p v-if="found === true" class="small" data-test="oauth-found">Found the sign-in at {{ issuerHost }}</p>
          <p v-if="found === false" class="small warn" data-test="oauth-notfound">This provider does not describe itself</p>
          <div v-if="found === true && offered.length" data-test="oauth-scopes">
            <b class="small">Scopes</b>
            <input v-if="offered.length > 30" v-model="scopeFilter" placeholder="filter" data-test="oauth-scope-filter" />
            <div class="scopes">
              <label v-for="s in visibleScopes" :key="s" class="check small"><input type="checkbox" :checked="ticked.has(s)" :data-test="`oauth-scope-${s}`" @change="tick(s, ($event.target as HTMLInputElement).checked)" /> {{ s }}</label>
            </div>
          </div>
          <details :open="advanced" data-test="oauth-advanced" @toggle="advanced = ($event.target as HTMLDetailsElement).open">
            <summary class="small">Advanced</summary>
            <label class="small block">Sign-in page address
              <input v-model="authorizeUrl" class="mono" data-test="oauth-authorize" />
            </label>
            <label class="small block">Token address
              <input v-model="tokenUrl" class="mono" data-test="oauth-token" />
            </label>
            <label class="small block">Scopes (separated by spaces)
              <input v-model="scopesText" class="mono" data-test="oauth-scopes-text" />
            </label>
          </details>
          <label class="small block">Your app's client ID
            <input v-model="clientId" class="mono" autocomplete="off" data-test="oauth-client-id" />
          </label>
          <label class="small block">Client secret (if your app has one)
            <input v-model="clientSecret" type="password" autocomplete="off" :placeholder="hasSecret ? 'saved; leave empty to keep it' : ''" data-test="oauth-client-secret" />
          </label>
          <p class="small" data-test="oauth-redirect">Register this redirect address in your app: <code class="mono">{{ redirect }}</code> (any port)
            <button data-test="oauth-redirect-copy" @click="copyRedirect">Copy</button></p>
          <p v-if="serverBound" class="muted small" data-test="oauth-mcp-note">This is an MCP server's sign-in: sign in again from Settings → Integrations → MCP servers (Sign in).</p>
          <p v-if="oauthProgress" class="small" data-test="oauth-progress">Finish signing in in your browser…</p>
          <p v-if="oauthResult" class="small ok" data-test="oauth-result">{{ oauthResult }}</p>
        </template>
        <div class="row">
          <span class="grow" />
          <button data-test="connection-cancel" @click="emit('close')">{{ oauthResult ? 'Close' : 'Cancel' }}</button>
          <button v-if="editing && !remote" :disabled="busy" data-test="oauth-save" @click="saveOAuth"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
          <button v-if="!remote && !serverBound" class="primary" :disabled="busy" data-test="oauth-signin" @click="oauthSignIn"><BusyLabel :busy="doing === 'oauth'" text="Signing in…">Sign in</BusyLabel></button>
        </div>
      </template>
      <template v-else-if="kind === 'website'">
        <label class="small block">Site
          <input v-model="site" class="mono" placeholder="https://intranet.example.com" data-test="connection-site" />
        </label>
        <p class="muted small">You sign in once in a window of this app, which keeps the session on this computer. Agents use the site, never your password.</p>
        <p v-if="signingIn && !remote" class="small" data-test="connection-signin-note">A sign-in window is open. Sign in there, then press Done.</p>
        <div v-if="savedLogin && !remote" class="row">
          <button v-if="!signingIn" :disabled="busy" data-test="connection-signin" @click="signIn"><BusyLabel :busy="doing === 'signin'" text="Opening…">Sign in</BusyLabel></button>
          <button v-else class="primary" :disabled="busy" data-test="connection-signin-done" @click="signInDone"><BusyLabel :busy="doing === 'done'" text="Checking…">Done</BusyLabel></button>
        </div>
        <p v-else-if="savedLogin && remote" class="muted small" data-test="connection-signin-remote">Sign in on the computer itself.</p>
        <div class="row">
          <span class="grow" />
          <button data-test="connection-cancel" @click="emit('close')">{{ savedLogin ? 'Close' : 'Cancel' }}</button>
          <button class="primary" :disabled="busy" data-test="connection-save" @click="save"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
        </div>
      </template>
      <template v-else>
      <div v-for="(f, i) in fields" :key="i" class="field row" :data-test="`connection-field-${i}`">
        <input v-if="!f.existing" v-model="f.label" class="label" placeholder="what it is, e.g. Account id" :data-test="`connection-field-label-${i}`" />
        <b v-else class="label">{{ f.label }}</b>
        <input v-if="f.secret" v-model="f.value" type="password" autocomplete="off" class="grow" :disabled="f.existing && !f.replacing"
          :placeholder="f.existing && !f.replacing ? (f.shown ?? 'not set') : 'paste it here'" :data-test="`connection-field-value-${i}`" />
        <input v-else v-model="f.value" class="grow" placeholder="value" :data-test="`connection-field-value-${i}`" />
        <button v-if="f.secret && f.existing && !f.replacing" :data-test="`connection-field-replace-${i}`" @click="replace(f)">Replace</button>
        <label v-if="!f.existing" class="check small"><input v-model="f.secret" type="checkbox" :data-test="`connection-field-secret-${i}`" /> secret</label>
        <button v-if="fields.length > 1 && !f.existing" class="link danger" :aria-label="`Remove field ${i + 1}`" @click="removeField(i)">Remove</button>
      </div>
      <div class="row">
        <button data-test="connection-add-field" @click="addField">+ Add field</button>
        <span class="grow" />
        <button data-test="connection-cancel" @click="emit('close')">Cancel</button>
        <button class="primary" :disabled="busy" data-test="connection-save" @click="save"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
      </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: rgba(0, 0, 0, 0.35); display: flex; align-items: flex-start; justify-content: center; padding: 40px 16px; overflow: auto; z-index: 60; }
.box { background: #fff; border-radius: 12px; padding: 14px 16px; width: min(560px, 100%); box-sizing: border-box; }
h3 { margin: 0 0 4px; font-size: 16px; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin: 6px 0; }
.block { display: block; }
.grow { flex: 1; min-width: 0; }
.label { min-width: 120px; }
input { font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; }
.block input { display: block; width: 100%; box-sizing: border-box; margin-top: 2px; }
.check { display: flex; gap: 4px; align-items: center; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.link { border: 0; background: transparent; color: var(--accent); }
.link.danger { color: var(--bad); }
.muted { color: var(--muted); } .small { font-size: 12px; } .error { color: var(--bad); } .warn { color: var(--warn); } .ok { color: var(--ok); }
.scopes { max-height: 160px; overflow: auto; display: grid; gap: 2px; margin: 4px 0; }
</style>
