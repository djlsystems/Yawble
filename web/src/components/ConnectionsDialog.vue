<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import {
  ActionRefused,
  deleteConnectionProvider,
  disconnectConnection,
  getConnectionFlow,
  getConnectionNeeds,
  listConnectionProviders,
  listConnections,
  renameConnection,
  saveConnectionProvider,
  startConnection,
  startDeviceConnection,
  updateMailboxPassword,
} from '../api/client';
import type {
  Connection,
  ConnectionDeviceStart,
  ConnectionNeeds,
  ConnectionProvider,
  ConnectionProviderSave,
  ConnectionUse,
} from '../api/types';
import { appPasswordWarning } from '../lib/appPasswordShape';
import { currentOrigin, goTo } from '../lib/browserNavigation';
import { productCli } from '../presentation/product';
import ConnectDialog from './ConnectDialog.vue';
import DialogTabs from './DialogTabs.vue';
import {
  countdown,
  mailServerLabel,
  parseScopes,
  permissionsOf,
  providerHelp,
  providerName,
  redirectUriFor,
  redirectUriWarning,
  rememberReconnect,
  statusLabel,
  takeGuidedConnect,
  takeReconnect,
  usedByLabels,
  when,
  type CallbackOutcome,
} from '../lib/connections';

/**
 * ADMIN > CONNECTIONS: the OAuth accounts the Host holds for plugins, and each provider's client.
 *
 * Each connection shows its provider, account, granted scopes, when it was connected and last
 * refreshed, its status (`ok`, or `needs reconnect` with the provider's reason) and the members
 * using it, with Reconnect, Rename and Disconnect. Disconnect is refused by the Host while a member
 * uses it; the refusal names them. The granted scopes show as the Host words them, once each.
 *
 * RECONNECT first says, in a short dialog, what it will ask the provider for - the scopes already
 * granted and, when an installed plugin's slot needs more of that provider, that it adds those - and
 * only then leaves for the provider. The Host's callback comes back here, to this dialog, with a
 * message saying it worked or what failed. A MICROSOFT connection was made with a code - its app is a
 * public client with no redirect URI, so the browser sign-in would stop at Microsoft - and reconnects
 * with a code too: the Reconnect dialog shows it, reads the flow, and closes on the same message.
 *
 * THE DIALOG STAYS OPEN ON A ROUTE CHANGE (`no-route-dismiss`). Quasar closes a dialog whenever the
 * route changes; the Console takes the provider's answer off the address just after opening this
 * dialog with it, which closed it under the person and left them on the Kanban.
 *
 * ADD CONNECTION, at the top, is the guided way: `ConnectDialog` walks a person through the service,
 * setting its app up and signing in, with the scopes the plugins ask for. Everything below it as it
 * was before - the Providers tab and Connect an account - is under ADVANCED.
 *
 * CONNECT AN ACCOUNT picks a provider - or sets up its client first - and scopes, and sends the
 * browser to the provider's consent page. The provider returns to the Host's callback, which answers
 * a redirect back to the Console; `notice` is what it came back with. The dialog shows the exact
 * redirect URI to register for the address in use, and one line of help per built-in provider.
 *
 * A MAILBOX (kind `imap`) shows its servers and "set" for its app password instead of scopes, and
 * UPDATE PASSWORD instead of Reconnect: the Host logs in with the new password and only then stores
 * it, clearing needs-reconnect. The login's sentence shows on the tile; a refusal shows in the dialog.
 *
 * THE CLIENT SECRET IS WRITE-ONLY. No route answers it: the dialog says "set" or "not set", and its
 * input starts empty whatever is stored. An empty input on save keeps the stored secret.
 */
const props = defineProps<{ notice?: CallbackOutcome | null }>();

/** The connection a Reconnect from here left for, once the provider's answer is back. */
const reconnectedFrom = ref<string | null>(null);
const open = defineModel<boolean>({ required: true });

const connections = ref<Connection[]>([]);
const providers = ref<ConnectionProvider[]>([]);
const loading = ref(false);
const loaded = ref(false);
const error = ref('');

async function load() {
  loading.value = true;
  error.value = '';

  try {
    [connections.value, providers.value] = await Promise.all([listConnections(), listConnectionProviders()]);
    loaded.value = true;
    return true;
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
  return false;
}

/** Which tab shows: the accounts, or the providers they are connected through. */
const tab = ref<'connections' | 'providers'>('connections');

/** Today's Providers tab and Connect form, out of the way of the guided Add connection. */
const advanced = ref(false);

watch(advanced, (showing) => {
  if (!showing) tab.value = 'connections';
});

watch(open, async (showing) => {
  if (!showing) {
    stopCode();
    return;
  }
  codeNotice.value = null;
  reconnectedFrom.value = props.notice ? takeReconnect() : null;
  if (!(await load())) return;

  // Back from a sign-in that Add connection began: it opens again at its last step.
  const notice = props.notice;
  if (notice && takeGuidedConnect() !== null) {
    guidedReturned.value = notice.outcome === 'refused' ? { refused: notice.reason } : { id: notice.id };
    guidedOpen.value = true;
  }
});

// --- Add connection, the guided way ----------------------------------------------------------------

const guidedOpen = ref(false);
const guidedReturned = ref<{ id: string | null } | { refused: string } | null>(null);

function addConnection() {
  guidedReturned.value = null;
  guidedOpen.value = true;
}

/** How many of the accounts go through a provider, for its tile. */
function connectionsOf(provider: ConnectionProvider) {
  const count = connections.value.filter((connection) => connection.provider === provider.id).length;
  return count === 0 ? 'None' : count === 1 ? '1 account' : `${count} accounts`;
}

/** The redirect URI for the address the person is using now. */
const redirectUri = computed(() => redirectUriFor(currentOrigin()));
const redirectWarning = computed(() => redirectUriWarning(currentOrigin(), productCli));

const nameOf = (id: string) => providerName(id, providers.value);

/** How a Reconnect with a code ended, said here as the provider's answer would be. */
const codeNotice = ref<{ refused: boolean; text: string } | null>(null);

const noticeRefused = computed(() => (codeNotice.value ? codeNotice.value.refused : props.notice?.outcome === 'refused'));

const noticeText = computed(() => {
  if (codeNotice.value) return codeNotice.value.text;
  const notice = props.notice;
  if (!notice) return '';
  if (notice.outcome === 'refused') {
    const from = connections.value.find((candidate) => candidate.id === reconnectedFrom.value);
    if (reconnectedFrom.value !== null) return `${from ? from.name : 'The connection'} was not reconnected: ${notice.reason}`;
    return `The account was not connected: ${notice.reason}`;
  }

  const connection = notice.id ? connections.value.find((candidate) => candidate.id === notice.id) : undefined;
  const which = connection ? ` ${connection.name} (${connection.account})` : '';
  return notice.outcome === 'connected' ? `Connected${which}.` : `Reconnected${which}.`;
});

// --- Leaving for the provider's consent page ----------------------------------------------------

const leaving = ref(false);

async function sendToProvider(request: Parameters<typeof startConnection>[0], problem: { value: string }) {
  leaving.value = true;
  problem.value = '';

  try {
    const flow = await startConnection(request);
    goTo(flow.authorizationUrl);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    leaving.value = false;
  }
}

// --- Connect an account ---------------------------------------------------------------------------

const connectOpen = ref(false);
const connectProvider = ref<string | null>(null);
const connectScopes = ref('');
const connectName = ref('');
const connectProblem = ref('');

const chosenProvider = computed(() => providers.value.find((provider) => provider.id === connectProvider.value) ?? null);

const providerOptions = computed(() =>
  providers.value.map((provider) => ({
    value: provider.id,
    label: provider.configured ? provider.name : `${provider.name} (client not set up)`,
  })),
);

function startConnect() {
  connectProvider.value = providers.value.find((provider) => provider.configured)?.id ?? providers.value[0]?.id ?? null;
  connectScopes.value = '';
  connectName.value = '';
  connectProblem.value = '';
  connectOpen.value = true;
}

function connect() {
  const provider = chosenProvider.value;
  if (!provider || !provider.configured || leaving.value) return;

  void sendToProvider(
    {
      provider: provider.id,
      scopes: parseScopes(connectScopes.value),
      name: connectName.value.trim() || null,
    },
    connectProblem,
  );
}

// --- Reconnect ------------------------------------------------------------------------------------

const reconnecting = ref<Connection | null>(null);
/** What installed plugins' slots ask of its provider; null until read, or when the read failed. */
const reconnectNeeds = ref<ConnectionNeeds | null>(null);
const reconnectReading = ref(false);
const reconnectProblem = ref('');

/** What the slots ask that this connection was not granted: Reconnect adds those. */
const reconnectAdds = computed(() => {
  const connection = reconnecting.value;
  if (!connection || !reconnectNeeds.value) return [];
  const granted = new Set(connection.scopes);
  return reconnectNeeds.value.scopes.filter((line) => !granted.has(line.scope));
});

/** The added scopes in words, once each; one the Host has no words for reads as itself. */
const reconnectAddWords = computed(() => [...new Set(reconnectAdds.value.map((line) => line.words ?? line.scope))]);
const reconnectAddPlugins = computed(() => [...new Set(reconnectAdds.value.flatMap((line) => line.plugins))]);

/** Microsoft's connections are made with a code, and reconnect with one: no redirect to come back by. */
const reconnectWithCode = computed(() => reconnecting.value?.providerKind === 'microsoft');

async function reconnect(connection: Connection) {
  stopCode();
  reconnectCode.value = null;
  reconnecting.value = connection;
  reconnectNeeds.value = null;
  reconnectProblem.value = '';
  reconnectReading.value = true;
  try {
    reconnectNeeds.value = await getConnectionNeeds(connection.provider);
  } catch {
    // Nothing known to add: Reconnect still asks again for what was granted.
  } finally {
    reconnectReading.value = false;
  }
}

async function continueReconnect() {
  const connection = reconnecting.value;
  if (!connection || leaving.value || reconnectReading.value) return;

  // The Host asks for the old granted scopes and these together; none added is the same account again.
  const scopes = reconnectAdds.value.map((line) => line.scope);
  if (reconnectWithCode.value) {
    await startCode(connection, scopes);
    return;
  }
  rememberReconnect(connection.id);
  await sendToProvider({ reconnectId: connection.id, scopes }, reconnectProblem);
  // Not sent: no answer will come back for it.
  if (reconnectProblem.value !== '') takeReconnect();
}

// --- Reconnect with a code ------------------------------------------------------------------------

/** How often the flow is read while it waits. The Host does the waiting at the provider. */
const codeReadEvery = 3000;

const reconnectCode = ref<ConnectionDeviceStart | null>(null);
const codeNow = ref(Date.now());
const codeLeft = computed(() => (reconnectCode.value ? countdown(reconnectCode.value.expiresAt, codeNow.value) : ''));
let codeTimer: ReturnType<typeof setTimeout> | null = null;
let codeClock: ReturnType<typeof setInterval> | null = null;

/** Stops reading; a code still shown is left to expire at the Host. */
function stopCode() {
  if (codeTimer !== null) clearTimeout(codeTimer);
  if (codeClock !== null) clearInterval(codeClock);
  codeTimer = null;
  codeClock = null;
}

onBeforeUnmount(stopCode);

watch(reconnecting, (connection) => {
  if (connection === null) {
    stopCode();
    reconnectCode.value = null;
  }
});

async function startCode(connection: Connection, scopes: string[]) {
  leaving.value = true;
  reconnectProblem.value = '';
  try {
    reconnectCode.value = await startDeviceConnection({ reconnectId: connection.id, scopes, flow: 'device' });
    codeNow.value = Date.now();
    codeClock = setInterval(() => (codeNow.value = Date.now()), 1000);
    codeTimer = setTimeout(() => void readCode(connection), codeReadEvery);
  } catch (cause) {
    reconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    leaving.value = false;
  }
}

async function readCode(connection: Connection) {
  const code = reconnectCode.value;
  codeTimer = null;
  if (!code || reconnecting.value !== connection) return;

  let read;
  try {
    read = await getConnectionFlow(code.flowId);
  } catch (cause) {
    // A read that failed is not an ending: say so and read again.
    reconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
    if (reconnectCode.value === code) codeTimer = setTimeout(() => void readCode(connection), codeReadEvery);
    return;
  }
  // Cancelled while the read was out: this answer is for a code no longer shown.
  if (reconnectCode.value !== code || reconnecting.value !== connection) return;

  reconnectProblem.value = '';
  if (read.state === 'waiting') {
    codeTimer = setTimeout(() => void readCode(connection), codeReadEvery);
    return;
  }

  const made = read.connection ?? connection;
  codeNotice.value = read.state === 'done'
    ? { refused: false, text: `Reconnected ${made.name} (${made.account}).` }
    : { refused: true, text: `${connection.name} was not reconnected: ${read.sentence}` };
  reconnecting.value = null;
  await load();
}

// --- Update password, for a mailbox -------------------------------------------------------------

const isMailbox = (connection: Connection) => connection.kind === 'imap' || connection.providerKind === 'imap';

/** The login's sentence after a password update, on the mailbox's tile. */
const rowNotice = ref<Record<string, string>>({});

const passwordFor = ref<Connection | null>(null);
/** WRITE-ONLY: starts empty every time and is cleared once sent. */
const newPassword = ref('');
const passwordProblem = ref('');
const passwordBusy = ref(false);
/** A plain warning when the new password is not shaped like the provider's app password; never a refusal. */
const passwordWarning = computed(() => appPasswordWarning(passwordFor.value?.preset, newPassword.value));

function startPasswordUpdate(connection: Connection) {
  passwordFor.value = connection;
  newPassword.value = '';
  passwordProblem.value = '';
}

async function updatePassword() {
  const connection = passwordFor.value;
  if (!connection || newPassword.value === '' || passwordBusy.value) return;

  passwordBusy.value = true;
  passwordProblem.value = '';
  try {
    const answer = await updateMailboxPassword(connection.id, newPassword.value);
    newPassword.value = '';
    passwordFor.value = null;
    rowNotice.value = { ...rowNotice.value, [connection.id]: answer.sentence };
    await load();
  } catch (cause) {
    // The login's sentence: refused or unreachable. Nothing changed at the Host.
    passwordProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    passwordBusy.value = false;
  }
}

// --- Rename ---------------------------------------------------------------------------------------

const renaming = ref<Connection | null>(null);
const newName = ref('');
const renameProblem = ref('');
const renameBusy = ref(false);

function startRename(connection: Connection) {
  renaming.value = connection;
  newName.value = connection.name;
  renameProblem.value = '';
}

async function rename() {
  const connection = renaming.value;
  const name = newName.value.trim();
  if (!connection || name === '' || renameBusy.value) return;

  renameBusy.value = true;
  renameProblem.value = '';
  try {
    await renameConnection(connection.id, name);
    renaming.value = null;
    await load();
  } catch (cause) {
    renameProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    renameBusy.value = false;
  }
}

// --- Disconnect -----------------------------------------------------------------------------------

const disconnecting = ref<Connection | null>(null);
const disconnectProblem = ref('');
const disconnectUsers = ref<string[]>([]);
const disconnectBusy = ref(false);

function startDisconnect(connection: Connection) {
  disconnecting.value = connection;
  disconnectProblem.value = '';
  disconnectUsers.value = [];
}

async function disconnect() {
  const connection = disconnecting.value;
  if (!connection || disconnectBusy.value) return;

  disconnectBusy.value = true;
  disconnectProblem.value = '';
  disconnectUsers.value = [];
  try {
    await disconnectConnection(connection.id);
    disconnecting.value = null;
    await load();
  } catch (cause) {
    // A 409 while members use it: the Host's sentence names them, and `usedBy` lists them.
    disconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
    const usedBy = cause instanceof ActionRefused ? cause.body.usedBy : undefined;
    if (Array.isArray(usedBy)) disconnectUsers.value = usedByLabels({ usedBy: usedBy as ConnectionUse[] });
  } finally {
    disconnectBusy.value = false;
  }
}

// --- A provider's client --------------------------------------------------------------------------

const clientOpen = ref(false);
/** The provider being set up, or null for a new custom provider. */
const clientFor = ref<ConnectionProvider | null>(null);
const clientId = ref('');
const clientSecret = ref('');
const clearSecret = ref(false);
const tenant = ref('');
const customId = ref('');
const customName = ref('');
const authorizeUrl = ref('');
const tokenUrl = ref('');
const userinfoUrl = ref('');
const defaultScopes = ref('');
const clientProblem = ref('');
const clientBusy = ref(false);

const isCustom = computed(() => clientFor.value === null || clientFor.value.kind === 'custom');

function setUpClient(provider: ConnectionProvider | null) {
  clientFor.value = provider;
  clientId.value = provider?.clientId ?? '';
  // NEVER FILLED: the Host does not send the secret, and a person replaces it by typing a new one.
  clientSecret.value = '';
  clearSecret.value = false;
  tenant.value = provider?.tenant ?? '';
  customId.value = provider?.id ?? 'custom-';
  customName.value = provider?.kind === 'custom' ? provider.name : '';
  authorizeUrl.value = provider?.authorizeUrl ?? '';
  tokenUrl.value = provider?.tokenUrl ?? '';
  userinfoUrl.value = provider?.userinfoUrl ?? '';
  defaultScopes.value = provider?.kind === 'custom' ? provider.defaultScopes.join(' ') : '';
  clientProblem.value = '';
  clientOpen.value = true;
}

async function saveClient() {
  if (clientBusy.value) return;

  const id = clientFor.value?.id ?? customId.value.trim();
  const body: ConnectionProviderSave = { clientId: clientId.value.trim() };

  // Left empty, the stored secret is kept; "clear" sends "" (a public client, PKCE only).
  if (clearSecret.value) body.clientSecret = '';
  else if (clientSecret.value !== '') body.clientSecret = clientSecret.value;

  if (clientFor.value?.kind === 'microsoft' && tenant.value.trim() !== '') body.tenant = tenant.value.trim();

  if (isCustom.value) {
    body.name = customName.value.trim() || id;
    body.authorizeUrl = authorizeUrl.value.trim();
    body.tokenUrl = tokenUrl.value.trim();
    if (userinfoUrl.value.trim() !== '') body.userinfoUrl = userinfoUrl.value.trim();
    body.defaultScopes = parseScopes(defaultScopes.value);
  }

  clientBusy.value = true;
  clientProblem.value = '';
  try {
    const saved = await saveConnectionProvider(id, body);
    clientSecret.value = '';
    clientOpen.value = false;
    await load();
    // Set up from Connect an account: go back to it with this provider chosen.
    if (connectOpen.value) connectProvider.value = saved.id;
  } catch (cause) {
    clientProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    clientBusy.value = false;
  }
}

async function removeProvider(provider: ConnectionProvider) {
  error.value = '';
  try {
    await deleteConnectionProvider(provider.id);
    await load();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  }
}
</script>

<template>
  <q-dialog v-model="open" no-route-dismiss>
    <q-card class="connections-card os-dialog-xl" data-connections-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Connections</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        Accounts that plugin members act on. For an account at an OAuth service the Host keeps its
        refresh token and hands a plugin only a fresh access token on each run; for a mailbox it keeps
        the app password encrypted and hands it to a bound plugin's run alone. A person binds a
        connection to a member in the member's settings.
      </q-card-section>

      <q-card-section class="row items-center q-gutter-x-sm q-py-sm">
        <q-btn color="primary" no-caps icon="add" label="Add connection" :disable="!loaded" @click="addConnection" />
        <q-space />
        <q-btn
          flat
          dense
          no-caps
          icon="tune"
          label="Advanced"
          :color="advanced ? 'primary' : undefined"
          :aria-pressed="advanced ? 'true' : 'false'"
          data-connections-advanced
          @click="advanced = !advanced"
        />
      </q-card-section>

      <q-card-section v-if="noticeText || error" class="q-py-none">
        <q-banner
          v-if="noticeText"
          dense
          :class="noticeRefused ? 'os-bg-tint-error text-negative q-mb-md' : 'os-bg-tint-ok text-positive q-mb-md'"
          data-connections-notice
        >
          {{ noticeText }}
        </q-banner>
        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-md" data-connections-error>
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>
      </q-card-section>

      <!-- TWO TABS: the accounts, and the providers whose clients they are connected through. -->
      <DialogTabs v-model="tab" class="q-px-md">
        <q-tab name="connections" :label="`Connections (${connections.length})`" data-connections-tab="connections" />
        <q-tab v-if="advanced" name="providers" :label="`Providers (${providers.length})`" data-connections-tab="providers" />
      </DialogTabs>
      <q-separator />

      <q-tab-panels v-model="tab" class="connections-body">
        <q-tab-panel name="connections" data-connections-panel="connections">
          <div v-if="advanced" class="row items-center q-mb-sm">
            <q-space />
            <q-btn flat dense no-caps icon="add" label="Connect an account…" :disable="!loaded" @click="startConnect" />
          </div>

          <div v-if="loading && !loaded" class="os-text-muted">Reading the connections…</div>
          <div v-else-if="loaded && connections.length === 0" class="os-text-muted q-pa-lg text-center" data-no-connections>
            No account is connected yet. Connect one to bind it to a plugin member.
          </div>

          <div v-if="connections.length > 0" class="conn-tiles">
            <div
              v-for="connection in connections"
              :key="connection.id"
              class="conn-tile"
              :data-connection="connection.id"
            >
              <div class="conn-tile-head">
                <q-icon name="link" size="18px" aria-hidden="true" />
                <span class="text-weight-medium" data-connection-name>{{ connection.name }}</span>
                <q-space />
                <q-badge
                  :color="connection.status === 'ok' ? 'positive' : 'negative'"
                  :label="statusLabel(connection)"
                  data-connection-status
                />
              </div>
              <div class="conn-tile-line">
                <span class="os-text-muted" data-connection-provider>{{ nameOf(connection.provider) }}</span>
                · <span class="mono" data-connection-account>{{ connection.account }}</span>
              </div>

              <div v-if="connection.status !== 'ok'" class="conn-tile-line text-negative" data-connection-reason>
                <template v-if="isMailbox(connection)">
                  {{ connection.statusReason ?? 'The server refused the login.' }} Update its password to clear this.
                </template>
                <template v-else>
                  {{ connection.statusReason ?? 'The provider gave no reason.' }} Reconnect it to clear this.
                </template>
              </div>
              <div v-else-if="rowNotice[connection.id]" class="conn-tile-line text-positive" data-row-notice>
                {{ rowNotice[connection.id] }}
              </div>

              <dl class="conn-facts q-mt-xs">
                <template v-if="isMailbox(connection)">
                  <dt>Servers</dt>
                  <dd data-connection-servers>
                    <div class="mono">IMAP {{ mailServerLabel(connection.imap) }}</div>
                    <div class="mono">SMTP {{ mailServerLabel(connection.smtp) }}</div>
                  </dd>
                  <dt>Username</dt>
                  <dd class="mono" data-connection-username>{{ connection.username ?? connection.account }}</dd>
                  <dt>App password</dt>
                  <dd data-connection-password>{{ connection.passwordSet ? 'set' : 'not set' }}</dd>
                </template>
                <template v-else>
                  <dt>Allows</dt>
                  <dd data-connection-scopes>
                    <template v-if="permissionsOf(connection).length === 0">Nothing beyond signing in</template>
                    <div v-for="permission in permissionsOf(connection)" :key="permission" class="conn-scope" data-connection-permission>
                      {{ permission }}
                    </div>
                  </dd>
                </template>
                <dt>Connected</dt>
                <dd data-connection-connected>{{ when(connection.connectedAt) }}</dd>
                <dt>{{ isMailbox(connection) ? 'Last login' : 'Last refreshed' }}</dt>
                <dd data-connection-refreshed>{{ when(connection.refreshedAt) }}</dd>
                <dt>Used by</dt>
                <dd data-connection-used-by>
                  <template v-if="connection.usedBy.length === 0">No member</template>
                  <div v-for="label in usedByLabels(connection)" :key="label">{{ label }}</div>
                </dd>
              </dl>

              <div class="conn-tile-actions">
                <span v-if="isMailbox(connection)" class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    icon="key"
                    :aria-label="`Update password ${connection.name}`"
                    @click="startPasswordUpdate(connection)"
                  />
                  <q-tooltip>Update password: a new app password, tried before it is kept</q-tooltip>
                </span>
                <span v-else class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    icon="sync"
                    :disable="leaving"
                    :aria-label="`Reconnect ${connection.name}`"
                    @click="reconnect(connection)"
                  />
                  <q-tooltip>Reconnect: agree again at the provider</q-tooltip>
                </span>
                <span class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    icon="edit"
                    :aria-label="`Rename ${connection.name}`"
                    @click="startRename(connection)"
                  />
                  <q-tooltip>Rename</q-tooltip>
                </span>
                <span class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    color="negative"
                    icon="link_off"
                    :aria-label="`Disconnect ${connection.name}`"
                    @click="startDisconnect(connection)"
                  />
                  <q-tooltip>Disconnect</q-tooltip>
                </span>
              </div>
            </div>
          </div>
        </q-tab-panel>

        <q-tab-panel name="providers" data-connections-panel="providers">
          <div class="row items-center q-mb-sm">
            <q-space />
            <q-btn flat dense no-caps icon="add" label="Add a custom provider…" @click="setUpClient(null)" />
          </div>

          <div class="conn-tiles">
            <div v-for="provider in providers" :key="provider.id" class="conn-tile" :data-provider="provider.id">
              <div class="conn-tile-head">
                <q-icon name="key" size="18px" aria-hidden="true" />
                <span class="text-weight-medium">{{ provider.name }}</span>
                <q-badge
                  :outline="provider.kind === 'custom'"
                  :color="provider.kind === 'custom' ? 'primary' : 'grey-7'"
                  :label="provider.kind === 'custom' ? 'Custom' : 'Built-in'"
                />
                <q-space />
                <q-badge
                  :color="provider.configured ? 'positive' : 'grey-7'"
                  :outline="!provider.configured"
                >
                  <span data-client-state>{{ provider.configured ? 'Client set up' : 'Client not set up' }}</span>
                </q-badge>
              </div>
              <div class="conn-tile-line mono os-text-muted">{{ provider.id }}</div>

              <dl class="conn-facts q-mt-xs">
                <dt>Client ID</dt>
                <dd class="mono" data-client-id>{{ provider.clientId ?? 'not set' }}</dd>
                <dt>Client secret</dt>
                <dd data-client-secret>{{ provider.clientSecretSet ? 'set' : 'not set' }}</dd>
                <dt>Connections</dt>
                <dd data-provider-connections>{{ connectionsOf(provider) }}</dd>
              </dl>

              <div class="conn-tile-actions">
                <span class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    icon="settings"
                    :aria-label="`Set up client… ${provider.name}`"
                    @click="setUpClient(provider)"
                  />
                  <q-tooltip>Set up its client</q-tooltip>
                </span>
                <span v-if="provider.kind === 'custom'" class="row-btn-wrap">
                  <q-btn
                    dense
                    flat
                    round
                    color="negative"
                    icon="delete"
                    :aria-label="`Remove ${provider.name}`"
                    @click="removeProvider(provider)"
                  />
                  <q-tooltip>Remove this provider</q-tooltip>
                </span>
              </div>
            </div>
          </div>
        </q-tab-panel>
      </q-tab-panels>
    </q-card>
  </q-dialog>

  <ConnectDialog
    v-model="guidedOpen"
    :providers="providers"
    :connections="connections"
    :returned="guidedReturned"
    @changed="load"
  />

  <!-- UPDATE PASSWORD, for a mailbox. The password is write-only: the field starts empty every time. -->
  <q-dialog :model-value="passwordFor !== null" @update:model-value="(showing) => { if (!showing) passwordFor = null; }">
    <q-card v-if="passwordFor" class="os-dialog-sm" data-password-dialog>
      <q-card-section>
        <div class="os-dialog-title">Update password</div>
        <div class="text-caption os-text-muted">
          A new app password for {{ passwordFor.name }} ({{ passwordFor.account }}). The Host logs in
          with it first and keeps it only if that works.
        </div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-input
          v-model="newPassword"
          outlined
          dense
          autofocus
          type="password"
          label="New app password"
          hint="Stored encrypted and never shown again."
          autocomplete="new-password"
          spellcheck="false"
          @keydown.enter.prevent="updatePassword"
        />
        <div v-if="passwordWarning" class="text-caption text-warning" role="status" data-app-password-warning>{{ passwordWarning }}</div>
        <div v-if="passwordProblem" class="text-negative" data-password-problem>{{ passwordProblem }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="passwordFor = null" />
        <q-btn
          color="primary"
          no-caps
          label="Update password"
          :loading="passwordBusy"
          :disable="newPassword === '' || passwordBusy"
          @click="updatePassword"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- RECONNECT: what it will ask for, before leaving for the provider. -->
  <q-dialog :model-value="reconnecting !== null" @update:model-value="(value: boolean) => { if (!value) reconnecting = null; }">
    <q-card v-if="reconnecting" class="os-dialog-sm" data-reconnect-dialog>
      <q-card-section>
        <div class="os-dialog-title">Reconnect {{ reconnecting.name }}</div>
      </q-card-section>
      <q-card-section class="q-pt-none q-gutter-y-sm">
        <div data-reconnect-asks>
          <template v-if="reconnectWithCode">
            This shows a code to enter at {{ nameOf(reconnecting.provider) }}, signing in again as
            <span class="mono">{{ reconnecting.account }}</span>.
          </template>
          <template v-else>
            This sends you to {{ nameOf(reconnecting.provider) }} to agree again as
            <span class="mono">{{ reconnecting.account }}</span>.
          </template>
          <template v-if="permissionsOf(reconnecting).length > 0">It asks for what it already has:</template>
          <template v-else>It asks for nothing beyond signing in.</template>
        </div>
        <ul v-if="permissionsOf(reconnecting).length > 0" class="q-my-none" data-reconnect-kept>
          <li v-for="permission in permissionsOf(reconnecting)" :key="permission">{{ permission }}</li>
        </ul>
        <div v-if="reconnectReading" class="os-text-muted">Reading what the installed plugins need…</div>
        <template v-else-if="reconnectAddWords.length > 0">
          <div data-reconnect-adds-intro>
            It also adds what {{ reconnectAddPlugins.join(', ') }} {{ reconnectAddPlugins.length === 1 ? 'needs' : 'need' }}:
          </div>
          <ul class="q-my-none" data-reconnect-adds>
            <li v-for="words in reconnectAddWords" :key="words">{{ words }}</li>
          </ul>
        </template>
        <template v-if="reconnectCode">
          <div data-reconnect-code>
            Open <a :href="reconnectCode.verificationUri" target="_blank" rel="noopener noreferrer" data-device-link>{{ reconnectCode.verificationUri }}</a>
            and enter
            <span class="mono text-weight-bold" data-device-code>{{ reconnectCode.userCode }}</span>
          </div>
          <div class="text-caption os-text-muted">
            The code expires in {{ codeLeft }}. This closes by itself once you have signed in.
          </div>
        </template>
        <div v-else-if="reconnectWithCode" class="os-text-muted">This closes by itself once you have signed in.</div>
        <div v-else class="os-text-muted">You come back here when it is done.</div>
        <q-banner v-if="reconnectProblem" dense class="os-bg-tint-error text-negative" data-reconnect-problem>
          {{ reconnectProblem }}
        </q-banner>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" v-close-popup />
        <q-btn
          v-if="!reconnectCode"
          color="primary"
          no-caps
          :label="reconnectWithCode ? 'Show the code' : `Continue to ${nameOf(reconnecting.provider)}`"
          :loading="leaving"
          :disable="reconnectReading"
          data-reconnect-continue
          @click="continueReconnect"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- CONNECT AN ACCOUNT -->
  <q-dialog v-model="connectOpen">
    <q-card class="os-dialog-md" data-connect-dialog>
      <q-card-section>
        <div class="os-dialog-title">Connect an account</div>
        <div class="text-caption os-text-muted">
          The browser goes to the provider's consent page and comes back here when you have agreed.
        </div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-select
          v-model="connectProvider"
          :options="providerOptions"
          emit-value
          map-options
          outlined
          dense
          label="Provider"
        />

        <div v-if="chosenProvider && !chosenProvider.configured" class="row items-center q-gutter-x-sm" data-needs-client>
          <span class="text-negative">Its client is not set up yet. Set it up first.</span>
          <q-btn flat dense no-caps color="primary" label="Set up client…" @click="setUpClient(chosenProvider)" />
        </div>

        <q-input
          v-model="connectScopes"
          outlined
          dense
          autogrow
          label="Scopes"
          :hint="chosenProvider
            ? `Added to ${chosenProvider.name}'s own: ${chosenProvider.defaultScopes.join(' ') || 'none'}. Separate with spaces or commas; a plugin's slot names the scopes it needs.`
            : 'Separate with spaces or commas.'"
          spellcheck="false"
          autocomplete="off"
        />
        <q-input
          v-model="connectName"
          outlined
          dense
          label="Name"
          hint="Optional. Defaults to the account's name."
          autocomplete="off"
        />

        <div class="connect-redirect" data-redirect-uri-block>
          <div class="text-caption os-text-muted">Redirect URI to register at the provider for this address:</div>
          <div class="mono" data-redirect-uri>{{ redirectUri }}</div>
          <div v-if="redirectWarning" class="text-caption text-warning q-mt-xs" data-redirect-warning>{{ redirectWarning }}</div>
          <div v-if="chosenProvider" class="text-caption os-text-muted q-mt-xs" data-provider-help>
            {{ providerHelp(chosenProvider) }}
          </div>
          <div class="text-caption os-text-muted q-mt-xs">
            If the provider will not accept this address, connect from the operator's computer with
            <span class="mono">{{ productCli }} connect</span>.
          </div>
        </div>

        <div v-if="connectProblem" class="text-negative" data-connect-problem>{{ connectProblem }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" />
        <q-btn
          color="primary"
          no-caps
          label="Connect"
          :loading="leaving"
          :disable="!chosenProvider || !chosenProvider.configured || leaving"
          @click="connect"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- A PROVIDER'S CLIENT. The secret is write-only: never filled, and "set" is all that is shown. -->
  <q-dialog v-model="clientOpen">
    <q-card class="os-dialog-md" data-client-dialog>
      <q-card-section>
        <div class="os-dialog-title">{{ clientFor ? `${clientFor.name} client` : 'Add a custom provider' }}</div>
        <div class="text-caption os-text-muted">
          The client ID and secret you registered at the provider. The Host stores the secret
          encrypted and never shows it again.
        </div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <template v-if="isCustom">
          <q-input
            v-if="!clientFor"
            v-model="customId"
            outlined
            dense
            label="Provider id"
            hint="custom- then lowercase letters, digits and hyphens."
            spellcheck="false"
            autocomplete="off"
          />
          <q-input v-model="customName" outlined dense label="Display name" autocomplete="off" />
          <q-input v-model="authorizeUrl" outlined dense label="Authorization URL" spellcheck="false" autocomplete="off" />
          <q-input v-model="tokenUrl" outlined dense label="Token URL" spellcheck="false" autocomplete="off" />
          <q-input
            v-model="userinfoUrl"
            outlined
            dense
            label="Userinfo URL"
            hint="Optional: where the account's name is read."
            spellcheck="false"
            autocomplete="off"
          />
          <q-input
            v-model="defaultScopes"
            outlined
            dense
            label="Default scopes"
            hint="Asked on every connection of this provider."
            spellcheck="false"
            autocomplete="off"
          />
        </template>

        <q-input v-model="clientId" outlined dense label="Client ID" spellcheck="false" autocomplete="off" />
        <q-input
          v-model="clientSecret"
          outlined
          dense
          type="password"
          label="Client secret"
          :hint="clientFor?.clientSecretSet
            ? 'Set. Leave empty to keep it; type a new one to replace it.'
            : 'Not set.'"
          :disable="clearSecret"
          autocomplete="new-password"
          spellcheck="false"
          data-client-secret-input
        >
          <template v-if="clientFor?.clientSecretSet" #append>
            <q-badge outline color="positive" label="set" data-secret-set />
          </template>
        </q-input>
        <q-checkbox
          v-if="clientFor?.clientSecretSet"
          v-model="clearSecret"
          dense
          label="Clear the secret (a public client, PKCE only)"
        />
        <q-input
          v-if="clientFor?.kind === 'microsoft'"
          v-model="tenant"
          outlined
          dense
          label="Tenant"
          hint="Optional. Leave empty for common: work, school and personal accounts."
          autocomplete="off"
        />

        <div data-redirect-uri-block>
          <div class="text-caption os-text-muted">Redirect URI to register at the provider for this address:</div>
          <div class="mono">{{ redirectUri }}</div>
          <div v-if="redirectWarning" class="text-caption text-warning q-mt-xs" data-redirect-warning>{{ redirectWarning }}</div>
          <div v-if="clientFor" class="text-caption os-text-muted q-mt-xs">{{ providerHelp(clientFor) }}</div>
        </div>

        <div v-if="clientProblem" class="text-negative" data-client-problem>{{ clientProblem }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" />
        <q-btn
          color="primary"
          no-caps
          label="Save client"
          :loading="clientBusy"
          :disable="clientBusy || clientId.trim() === ''"
          @click="saveClient"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- RENAME -->
  <q-dialog :model-value="renaming !== null" @update:model-value="(value: boolean) => { if (!value) renaming = null; }">
    <q-card class="os-dialog-sm" data-rename-dialog>
      <q-card-section>
        <div class="os-dialog-title">Rename connection</div>
      </q-card-section>
      <q-card-section>
        <q-input v-model="newName" outlined dense label="Name" maxlength="80" autocomplete="off" @keydown.enter.prevent="rename" />
        <div v-if="renameProblem" class="text-negative q-mt-sm">{{ renameProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="renaming = null" />
        <q-btn color="primary" no-caps label="Rename" :loading="renameBusy" :disable="newName.trim() === ''" @click="rename" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- DISCONNECT -->
  <q-dialog :model-value="disconnecting !== null" @update:model-value="(value: boolean) => { if (!value) disconnecting = null; }">
    <q-card class="os-dialog-sm" data-disconnect-dialog>
      <q-card-section>
        <div class="os-dialog-title">Disconnect {{ disconnecting?.name }}?</div>
        <div class="text-caption os-text-muted">
          The Host deletes its tokens{{ disconnecting && providers.find((p) => p.id === disconnecting!.provider)?.revokes ? ' and revokes the grant at the provider' : '' }}.
          A member that uses it must be unbound first.
        </div>
      </q-card-section>
      <q-card-section v-if="disconnectProblem" class="text-negative" data-disconnect-refusal>
        <div>{{ disconnectProblem }}</div>
        <ul v-if="disconnectUsers.length > 0" class="q-my-xs">
          <li v-for="label in disconnectUsers" :key="label">{{ label }}</li>
        </ul>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="disconnecting = null" />
        <q-btn color="negative" no-caps label="Disconnect" :loading="disconnectBusy" @click="disconnect" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.connections-body {
  max-height: min(64vh, 44rem);
  overflow-y: auto;
}

/* WRAPPED TILES, as the Agents and Plugins screens lay theirs out: as many columns as fit. */
.conn-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(20rem, 1fr));
  gap: 12px;
}

.conn-tile {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 12px 14px 8px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 6px;
  min-width: 0;
}

.conn-tile-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.conn-tile-line {
  font-size: 12px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.conn-tile-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 2px;
}

.conn-facts {
  font-size: 12px;
  line-height: 1.45;
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 16px;
  row-gap: 2px;
  margin: 0;
}

/* One scope a line, broken only where a URL has to be: a scope split across lines reads as two. */
.conn-scope {
  overflow-wrap: anywhere;
}

.conn-facts dt {
  color: var(--os-ink-muted);
  font-weight: 500;
}

.conn-facts dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
}

/* The tooltip lives on this WRAPPER because a disabled q-btn swallows pointer events. */
.row-btn-wrap {
  display: inline-flex;
}

.connect-redirect {
  word-break: break-all;
}
</style>
