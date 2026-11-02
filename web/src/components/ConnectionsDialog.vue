<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import { computed, ref, watch } from 'vue';
import {
  ActionRefused,
  disconnectConnection,
  getConnectionNeeds,
  listConnectionProviders,
  listConnections,
  listCredentialPresets,
  listMailboxPresets,
  renameConnection,
  startConnection,
  startDeviceConnection,
  testConnection,
  updateMailboxPassword,
} from '../api/client';
import type {
  Connection,
  ConnectionDeviceStart,
  ConnectionFlow,
  ConnectionNeeds,
  ConnectionProvider,
  ConnectionUse,
  CredentialPreset,
  MailboxPreset,
} from '../api/types';
import { appPasswordWarning } from '../lib/appPasswordShape';
import { goTo } from '../lib/browserNavigation';
import ConnectDialog from './ConnectDialog.vue';
import CredentialForm from './CredentialForm.vue';
import ConnectionTest from './ConnectionTest.vue';
import MailboxForm from './MailboxForm.vue';
import DirectoryField from './DirectoryField.vue';
import CodeSignIn from './CodeSignIn.vue';
import {
  connectionSignInHref,
  directoryRefusal,
  kindLabel,
  kindOf,
  mailServerLabel,
  permissionsOf,
  providerName,
  reconnectTenant,
  refusalWords,
  rememberReconnect,
  statusLabel,
  takeGuidedConnect,
  takeReconnect,
  usedByLabels,
  when,
  type CallbackOutcome,
} from '../lib/connections';

/**
 * ADMIN > CONNECTIONS: the accounts the Host holds for plugins, and Add connection.
 *
 * EVERY CONNECTION, ONE STORE: each shows its kind (Sign-in, Mailbox, Website, API key, Username and
 * password), its provider or service, account, what it holds (a sign-in's granted scopes as the Host
 * words them, once each; a mailbox's servers; a credential's fields, a masked one only as set), when
 * it was connected and last refreshed, its health (`ok`, or `needs reconnect` with the reason) and,
 * read only, the teams and members using it. Nothing here assigns it to a team.
 *
 * EDIT changes what is behind the name - a key, a password, a username, a mailbox's servers - and the
 * name and id stay, so every team and member using it gets the new value at its next run. A sign-in
 * changes account by Reconnect, which may sign in as another account under the same name.
 *
 * DELETE ASKS FIRST, listing each team and member using the connection and saying their runs stop
 * until another is chosen; confirmed, the Host deletes it and unbinds them in one transaction, and
 * each such member shows in Needs you.
 *
 * RECONNECT first says, in a short dialog, what it will ask the provider for - the scopes already
 * granted and, when an installed plugin's slot needs more of that provider, that it adds those - and
 * only then leaves for the provider. The Host's callback comes back here, to this dialog, with a
 * message saying it worked or what failed. A MICROSOFT connection was made with a code - its app is a
 * public client with no redirect URI, so the browser sign-in would stop at Microsoft - and reconnects
 * with a code too: the Reconnect dialog shows it through CodeSignIn, as Add connection does (Cancel
 * beside it ends the sign-in at the Host), reads the flow, and closes on the same message.
 *
 * A MICROSOFT CONNECTION'S OWN DIRECTORY (`tenant`) shows on its tile when it has one. Reconnect shows
 * it in DirectoryField to change or clear: left as it was, nothing is sent and the Host keeps it;
 * changed, the new one is sent; emptied, `""` clears it. Add connection's Microsoft sign-in offers the
 * same field. The Host's refusal of a directory is shown at the field.
 *
 * THE DIALOG STAYS OPEN ON A ROUTE CHANGE (`no-route-dismiss`). Quasar closes a dialog whenever the
 * route changes; the Console takes the provider's answer off the address just after opening this
 * dialog with it, which closed it under the person and left them on the Kanban.
 *
 * ADD CONNECTION, at the top, is the one way to add a connection of any kind: `ConnectDialog` starts
 * with the kind and walks a person through it. Every OAuth service's app - Microsoft's, Google's, a
 * custom service's addresses and client - is a step of its own there, before sign-in: saved, cleared
 * and signed in through on that step. There is no second door: no Advanced, no Providers tab.
 *
 * A SIGN-IN THAT LEAVES FOR THE PROVIDER (Reconnect of a web sign-in) comes back through the Host's
 * callback to the Console; `notice` is what it came back with.
 *
 * A MAILBOX (kind `imap`) shows its servers and "set" for its app password instead of scopes, and
 * UPDATE PASSWORD instead of Reconnect: the Host logs in with the new password and only then stores
 * it, clearing needs-reconnect. The login's sentence shows on the tile; a refusal shows in the dialog.
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

watch(open, async (showing) => {
  if (!showing) {
    // Closed: a code shown stops being read (CodeSignIn unmounts); it is left waiting at the Host.
    reconnecting.value = null;
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
    if (reconnectedFrom.value !== null) return `${from ? from.name : 'The connection'} was not reconnected: ${refusalWords(notice.reason)}`;
    return `The account was not connected: ${refusalWords(notice.reason)}`;
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

// --- Reconnect ------------------------------------------------------------------------------------

const reconnecting = ref<Connection | null>(null);
/** What installed plugins' slots ask of its provider; null until read, or when the read failed. */
const reconnectNeeds = ref<ConnectionNeeds | null>(null);
const reconnectReading = ref(false);
const reconnectProblem = ref('');
/** The connection's directory as shown to change or clear; sent only when it differs. */
const reconnectDirectory = ref('');
const reconnectDirectoryProblem = ref('');

watch(reconnectDirectory, () => (reconnectDirectoryProblem.value = ''));

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
  reconnectCode.value = null;
  reconnecting.value = connection;
  reconnectNeeds.value = null;
  reconnectProblem.value = '';
  reconnectDirectory.value = connection.tenant ?? '';
  reconnectDirectoryProblem.value = '';
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

/** The code the Host gave; CodeSignIn shows it, reads the flow until it ends, and offers Cancel. */
const reconnectCode = ref<ConnectionDeviceStart | null>(null);

watch(reconnecting, (connection) => {
  if (connection === null) reconnectCode.value = null;
});

async function startCode(connection: Connection, scopes: string[]) {
  leaving.value = true;
  reconnectProblem.value = '';
  reconnectDirectoryProblem.value = '';
  try {
    // A fresh object each time, so a second Show the code starts over.
    reconnectCode.value = {
      ...(await startDeviceConnection({
        reconnectId: connection.id,
        scopes,
        flow: 'device',
        ...reconnectTenant(connection, reconnectDirectory.value),
      })),
    };
  } catch (cause) {
    const text = cause instanceof Error ? cause.message : String(cause);
    if (directoryRefusal(text)) reconnectDirectoryProblem.value = text;
    else reconnectProblem.value = text;
  } finally {
    leaving.value = false;
  }
}

/** The code ended: the dialog closes on the same message, done or refused, and the list is read again. */
async function codeEnded(read: ConnectionFlow) {
  const connection = reconnecting.value;
  if (!connection) return;
  const made = read.connection ?? connection;
  codeNotice.value = read.state === 'done'
    ? { refused: false, text: `Reconnected ${made.name} (${made.account}).` }
    : { refused: true, text: `${connection.name} was not reconnected: ${refusalWords(read.sentence)}` };
  reconnecting.value = null;
  await load();
}

// --- Update password, for a mailbox -------------------------------------------------------------

const isMailbox = (connection: Connection) => connection.kind === 'imap' || connection.providerKind === 'imap';

/** A website, an API key or a username and password: edited, never reconnected. */
const isCredential = (connection: Connection) => ['website', 'apikey', 'login'].includes(kindOf(connection));

// --- Edit: what is behind a name ------------------------------------------------------------------

const editing = ref<Connection | null>(null);
const credentialPresets = ref<CredentialPreset[]>([]);
const mailboxPresets = ref<MailboxPreset[]>([]);
const credentialForm = ref<InstanceType<typeof CredentialForm> | null>(null);
const mailboxEditForm = ref<InstanceType<typeof MailboxForm> | null>(null);

async function startEdit(connection: Connection) {
  editing.value = connection;
  try {
    if (isCredential(connection) && credentialPresets.value.length === 0) credentialPresets.value = await listCredentialPresets();
    if (isMailbox(connection) && mailboxPresets.value.length === 0) mailboxPresets.value = await listMailboxPresets();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  }
}

/** The edit screen's Test connection: what is saved, its health shown on its tile too. */
async function editTested(connection: Connection) {
  if (editing.value?.id === connection.id) editing.value = connection;
  await load();
}

// --- Test connection, from a tile ---------------------------------------------------------------

const testingRow = ref<string | null>(null);

/** A tile's Test connection: an ok answer is said on the tile; a refusal is its health, which the tile shows. */
async function testRow(connection: Connection) {
  if (testingRow.value) return;
  testingRow.value = connection.id;
  try {
    const answer = await testConnection(connection.id);
    const others = Object.fromEntries(Object.entries(rowNotice.value).filter(([id]) => id !== connection.id));
    rowNotice.value = answer.health === 'ok' ? { ...others, [connection.id]: answer.sentence } : others;
    await load();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    testingRow.value = null;
  }
}

async function edited(connection: Connection, sentence?: string) {
  editing.value = null;
  rowNotice.value = {
    ...rowNotice.value,
    [connection.id]: sentence ?? 'Saved. Every team and member using it gets the new value at its next run.',
  };
  await load();
}

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

// --- Delete ---------------------------------------------------------------------------------------

const disconnecting = ref<Connection | null>(null);
const disconnectProblem = ref('');
/** Who uses it, listed before the person confirms: from the list, then from the Host's refusal. */
const disconnectUsers = ref<string[]>([]);
const disconnectBusy = ref(false);

function startDisconnect(connection: Connection) {
  disconnecting.value = connection;
  disconnectProblem.value = '';
  disconnectUsers.value = usedByLabels(connection);
}

async function disconnect() {
  const connection = disconnecting.value;
  if (!connection || disconnectBusy.value) return;

  disconnectBusy.value = true;
  disconnectProblem.value = '';
  try {
    // CONFIRMED FOR THE USERS LISTED: with none listed, a member bound meanwhile refuses it, and is listed.
    await disconnectConnection(connection.id, disconnectUsers.value.length > 0);
    disconnecting.value = null;
    await load();
  } catch (cause) {
    // A 409: someone bound it since the list was read. The Host's sentence names them, and `usedBy` lists them.
    disconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
    const usedBy = cause instanceof ActionRefused ? cause.body.usedBy : undefined;
    if (Array.isArray(usedBy)) disconnectUsers.value = usedByLabels({ usedBy: usedBy as ConnectionUse[] });
  } finally {
    disconnectBusy.value = false;
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
        <div class="text-negative q-mt-sm" data-connections-team-note>
          Note: a connection added here is not available to any team yet. To let a team use it, open
          that team's Team settings, then Connections, and add it there.
        </div>
      </q-card-section>

      <q-card-section class="row items-center q-gutter-x-sm q-py-sm">
        <q-btn color="primary" no-caps icon="add" label="Add connection" :disable="!loaded" @click="addConnection" />
      </q-card-section>

      <q-card-section v-if="noticeText || error" class="q-py-none">
        <q-banner
          v-if="noticeText"
          dense
          :class="noticeRefused ? 'os-bg-tint-error text-negative q-mb-md' : 'os-bg-tint-ok text-positive q-mb-md'"
          style="white-space: pre-line"
          data-connections-notice
        >
          {{ noticeText }}
        </q-banner>
        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-md" data-connections-error>
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>
      </q-card-section>

      <q-separator />

      <div class="connections-body q-pa-md" data-connections-panel="connections">
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
              <q-badge outline color="grey-7" :label="kindLabel(connection)" data-connection-kind />
              <q-space />
              <q-badge
                :color="connection.status === 'ok' ? 'positive' : 'negative'"
                :label="statusLabel(connection)"
                data-connection-status
              />
            </div>
            <div class="conn-tile-line">
              <span class="os-text-muted" data-connection-provider>{{ nameOf(connection.provider) }}</span>
              <template v-if="connection.account !== ''">
                · <span class="mono" data-connection-account>{{ connection.account }}</span>
              </template>
            </div>
            <div v-if="connection.tenant" class="conn-tile-line os-text-muted" data-connection-directory>
              Directory <span class="mono">{{ connection.tenant }}</span>
            </div>

            <div v-if="connection.status !== 'ok'" class="conn-tile-line text-negative" data-connection-reason>
              <template v-if="isMailbox(connection)">
                {{ connection.statusReason ?? 'The server refused the login.' }} Update its password to clear this.
              </template>
              <template v-else-if="isCredential(connection)">
                {{ connection.statusReason ?? 'Its test was refused.' }} Edit it, then test it again, to clear this.
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
              <template v-else-if="isCredential(connection)">
                <template v-for="field in connection.fields ?? []" :key="field.name">
                  <dt>{{ field.label }}</dt>
                  <dd :class="{ mono: !field.masked }" :data-connection-field="field.name">
                    {{ field.masked ? (field.set ? 'set' : 'not set') : field.value ?? 'not set' }}
                  </dd>
                </template>
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
              <span v-if="connection.testable" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="network_check"
                  :loading="testingRow === connection.id"
                  :disable="testingRow !== null"
                  :aria-label="`Test connection ${connection.name}`"
                  data-connection-test-row
                  @click="testRow(connection)"
                />
                <q-tooltip>Test connection: try what is saved, now</q-tooltip>
              </span>
              <span v-if="kindOf(connection) === 'website'" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="language"
                  :href="connectionSignInHref(connection)"
                  target="_blank"
                  rel="noopener"
                  :aria-label="`Sign in ${connection.name}`"
                  data-connection-sign-in
                />
                <q-tooltip>Sign in: open the site in the platform's browser and sign in yourself; the session is kept on this connection</q-tooltip>
              </span>
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
              <span v-if="isMailbox(connection) || isCredential(connection)" class="row-btn-wrap">
                <q-btn
                  dense
                  flat
                  round
                  icon="tune"
                  :aria-label="`Edit ${connection.name}`"
                  @click="startEdit(connection)"
                />
                <q-tooltip>Edit: a new value for every team and member using it</q-tooltip>
              </span>
              <span v-if="!isMailbox(connection) && !isCredential(connection)" class="row-btn-wrap">
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
                  :aria-label="`Delete ${connection.name}`"
                  @click="startDisconnect(connection)"
                />
                <q-tooltip>Delete</q-tooltip>
              </span>
            </div>
          </div>
        </div>
      </div>
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
          autocomplete="new-password"
          spellcheck="false"
          @keydown.enter.prevent="updatePassword"
        >
          <template #append><FieldHelp text="Stored encrypted and never shown again." /></template>
        </q-input>
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
        <DirectoryField
          v-if="reconnectWithCode && !reconnectCode"
          v-model="reconnectDirectory"
          :error="reconnectDirectoryProblem"
        />
        <template v-if="reconnectCode">
          <div data-reconnect-code>
            <CodeSignIn
              :code="reconnectCode"
              :provider-name="nameOf(reconnecting.provider)"
              @ended="codeEnded"
              @cancelled="reconnectCode = null"
              @problem="(text: string) => (reconnectProblem = text)"
            />
          </div>
          <div class="text-caption os-text-muted">This closes by itself once you have signed in.</div>
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

  <!-- EDIT: what is behind the name -->
  <q-dialog :model-value="editing !== null" @update:model-value="(value: boolean) => { if (!value) editing = null; }">
    <q-card class="os-dialog-md" data-edit-dialog>
      <q-card-section>
        <div class="os-dialog-title">Edit {{ editing?.name }}</div>
        <div class="text-caption os-text-muted">
          The name stays. Every team and member using it gets what is saved here at its next run.
        </div>
      </q-card-section>
      <q-card-section v-if="editing && isCredential(editing)">
        <CredentialForm
          ref="credentialForm"
          :presets="credentialPresets"
          :kind="kindOf(editing) as 'website' | 'apikey' | 'login'"
          :connection="editing"
          @saved="(saved) => edited(saved)"
        />
      </q-card-section>
      <q-card-section v-else-if="editing && isMailbox(editing) && mailboxPresets.length > 0">
        <MailboxForm
          ref="mailboxEditForm"
          :presets="mailboxPresets"
          :preset="editing.preset ?? 'other'"
          :chooser="false"
          :connection="editing"
          @saved="(saved, sentence) => edited(saved, sentence)"
        />
      </q-card-section>
      <q-card-section v-if="editing?.testable" class="q-pt-none" data-edit-test>
        <div class="text-caption os-text-muted q-mb-xs">Test connection tries what is saved, not what is typed above.</div>
        <ConnectionTest :connection="editing" @tested="editTested" />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="editing = null" />
        <q-btn
          color="primary"
          no-caps
          label="Save"
          data-edit-save
          :loading="(credentialForm?.busy ?? false) || (mailboxEditForm?.busy ?? false)"
          :disable="!((credentialForm?.ready ?? false) || (mailboxEditForm?.ready ?? false))"
          @click="credentialForm ? credentialForm.save() : mailboxEditForm?.save()"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- DELETE: asks first, naming who uses it -->
  <q-dialog :model-value="disconnecting !== null" @update:model-value="(value: boolean) => { if (!value) disconnecting = null; }">
    <q-card class="os-dialog-sm" data-disconnect-dialog>
      <q-card-section>
        <div class="os-dialog-title">Delete {{ disconnecting?.name }}?</div>
        <div class="text-caption os-text-muted">
          The Host deletes what it holds for it{{ disconnecting && providers.find((p) => p.id === disconnecting!.provider)?.revokes ? ' and revokes the grant at the provider' : '' }}.
        </div>
      </q-card-section>
      <q-card-section v-if="disconnectUsers.length > 0" data-disconnect-users>
        <div>Used by:</div>
        <ul class="q-my-xs">
          <li v-for="label in disconnectUsers" :key="label">{{ label }}</li>
        </ul>
        <div>Their runs stop until another connection is chosen for them.</div>
      </q-card-section>
      <q-card-section v-if="disconnectProblem" class="text-negative" data-disconnect-refusal>
        <div>{{ disconnectProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="disconnecting = null" />
        <q-btn
          color="negative"
          no-caps
          :label="disconnectUsers.length > 0 ? 'Delete and unbind' : 'Delete'"
          data-disconnect-confirm
          :loading="disconnectBusy"
          @click="disconnect"
        />
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
  font-size: 0.75rem;
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
  font-size: 0.75rem;
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
</style>
