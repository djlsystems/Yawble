<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import { listConnections, startConnection, startDeviceConnection } from '../api/client';
import type { Connection, ConnectionDeviceStart, ConnectionFlow } from '../api/types';
import CodeSignIn from './CodeSignIn.vue';
import DirectoryField from './DirectoryField.vue';
import { type CallbackOutcome, directoryRefusal, UsernameIncorrectHint } from '../lib/connections';
import { awaitProviderReturn, newSigninTag, signinTabAddress } from '../lib/providerReturn';

/**
 * RECONNECT, IN ANOTHER TAB: one connection, asked again at its provider with the scopes a slot
 * still needs from it. Used where leaving the page would lose what the person has filled in - the
 * solution wizard. The Host asks the connection's old scopes plus these, and keeps its ID.
 *
 * Reconnect asks the Host for the consent page and shows a link opening it in another tab (the
 * provider-return tag, as Connect from a slot does); this tab waits. Back from the provider, the
 * connection is read again: `reconnected` names it and the asked scopes it still lacks - none when
 * the sign-in granted them all. A person who unticked a scope at the provider is told so here, in
 * words, and can press Reconnect again.
 *
 * WITH A CODE (`withCode`, a Microsoft connection): Microsoft has no consent page to send a tab to, so
 * Reconnect asks the Host for a code (`flow: "device"`, the connection's ID, the scopes) and shows it
 * here, in place, through CodeSignIn. Beside Reconnect is the same optional DirectoryField (with
 * UsernameIncorrectHint under it) as Add connection: what is typed is sent as `tenant`; left empty,
 * none is sent and the Host keeps the connection's own directory. The Host's refusal of it is shown at
 * the field. Once approved the connection is read again as above; a refused or expired code stays
 * shown with the Host's sentence, and Reconnect starts over. Cancel beside the code ends the sign-in at
 * the Host and takes the code away.
 */
const props = defineProps<{
  connectionId: string;
  scopes: string[];
  /** The provider's name, for the link: "Open Google to reconnect". */
  providerName: string;
  /** Sign in again with a code, shown here: a Microsoft connection. */
  withCode?: boolean | undefined;
}>();

const emit = defineEmits<{
  reconnected: [connection: Connection, lacking: string[]];
}>();

const starting = ref(false);
const tabUrl = ref<string | null>(null);
const problem = ref('');
const lacking = ref<string[]>([]);
/** The code shown for a reconnect with one; null when none is. */
const code = ref<ConnectionDeviceStart | null>(null);
/** The code shown ended (refused or expired): the directory may be changed and Reconnect pressed again. */
const codeOver = ref(false);
/** A Microsoft connection's directory, optional: sent as `tenant` only when typed. */
const directory = ref('');
const directoryProblem = ref('');

watch(directory, () => (directoryProblem.value = ''));
let stopReturn: (() => void) | null = null;

function stopWaiting() {
  stopReturn?.();
  stopReturn = null;
}

onBeforeUnmount(stopWaiting);

async function reconnect() {
  if (starting.value) return;
  starting.value = true;
  problem.value = '';
  directoryProblem.value = '';
  lacking.value = [];
  try {
    if (props.withCode) {
      const tenant = directory.value.trim();
      // A fresh object each time, so pressing Reconnect again starts the code over.
      code.value = {
        ...(await startDeviceConnection({
          reconnectId: props.connectionId,
          scopes: props.scopes,
          flow: 'device',
          ...(tenant ? { tenant } : {}),
        })),
      };
      codeOver.value = false;
      return;
    }
    const flow = await startConnection({ reconnectId: props.connectionId, scopes: props.scopes });
    stopWaiting();
    const tag = newSigninTag();
    tabUrl.value = signinTabAddress(tag);
    stopReturn = awaitProviderReturn(tag, flow.authorizationUrl, (outcome) => void returned(outcome));
  } catch (cause) {
    const text = cause instanceof Error ? cause.message : String(cause);
    if (props.withCode && directoryRefusal(text)) directoryProblem.value = text;
    else problem.value = text;
  } finally {
    starting.value = false;
  }
}

async function returned(outcome: CallbackOutcome) {
  stopWaiting();
  tabUrl.value = null;

  if (outcome.outcome === 'refused') {
    problem.value = outcome.reason;
    return;
  }
  await readAgain(outcome.id);
}

/** The code ended: approved, the connection is read again; refused or expired, CodeSignIn says so. */
async function codeEnded(read: ConnectionFlow) {
  if (read.state !== 'done') {
    codeOver.value = true;
    return;
  }
  code.value = null;
  await readAgain(read.connection?.id);
}

async function readAgain(returnedId: string | null | undefined) {
  try {
    const id = returnedId ?? props.connectionId;
    const connection = (await listConnections()).find((candidate) => candidate.id === id);
    if (!connection) {
      problem.value = 'The sign-in came back, but the connection could not be found. Choose it again.';
      return;
    }
    const granted = new Set(connection.scopes);
    lacking.value = props.scopes.filter((scope) => !granted.has(scope));
    emit('reconnected', connection, lacking.value);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  }
}

const quoted = (scopes: string[]) => scopes.map((scope) => `\`${scope}\``).join(', ');
</script>

<template>
  <span class="slot-reconnect" data-slot-reconnect>
    <q-btn flat dense no-caps size="sm" color="primary" label="Reconnect" :loading="starting" @click="reconnect" />
    <div v-if="tabUrl" class="text-caption q-mt-xs" data-reconnect-tab>
      <a :href="tabUrl" target="_blank" rel="noopener noreferrer" data-reconnect-link>Open {{ providerName }} to reconnect</a>
      in another tab, and leave {{ scopes.length === 1 ? 'the scope' : 'every scope' }} it asks for ticked. This waits here.
    </div>
    <div v-if="withCode && (!code || codeOver)" class="q-mt-xs" data-reconnect-directory>
      <DirectoryField v-model="directory" :error="directoryProblem" />
      <div class="text-caption os-text-muted" data-reconnect-directory-hint>{{ UsernameIncorrectHint }}</div>
    </div>
    <div v-if="code" class="q-mt-xs" data-reconnect-code>
      <CodeSignIn
        :code="code"
        :provider-name="providerName"
        @ended="codeEnded"
        @cancelled="code = null"
        @problem="(text: string) => (problem = text)"
      />
    </div>
    <div v-if="lacking.length > 0" class="text-caption text-negative q-mt-xs" data-reconnect-short>
      The sign-in came back without {{ lacking.length === 1 ? 'the scope' : 'the scopes' }} {{ quoted(lacking) }}: it was
      not granted at {{ providerName }}, so the connection still cannot be bound. Press Reconnect and leave
      {{ lacking.length === 1 ? 'it' : 'them' }} ticked.
    </div>
    <div v-if="problem" class="text-caption text-negative q-mt-xs" data-reconnect-problem>{{ problem }}</div>
  </span>
</template>
