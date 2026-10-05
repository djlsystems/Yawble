<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { listConnections, startConnection } from '../api/client';
import type { Connection } from '../api/types';
import type { CallbackOutcome } from '../lib/connections';
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
 */
const props = defineProps<{
  connectionId: string;
  scopes: string[];
  /** The provider's name, for the link: "Open Google to reconnect". */
  providerName: string;
}>();

const emit = defineEmits<{
  reconnected: [connection: Connection, lacking: string[]];
}>();

const starting = ref(false);
const tabUrl = ref<string | null>(null);
const problem = ref('');
const lacking = ref<string[]>([]);
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
  lacking.value = [];
  try {
    const flow = await startConnection({ reconnectId: props.connectionId, scopes: props.scopes });
    stopWaiting();
    const tag = newSigninTag();
    tabUrl.value = signinTabAddress(tag);
    stopReturn = awaitProviderReturn(tag, flow.authorizationUrl, (outcome) => void returned(outcome));
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
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

  try {
    const id = outcome.id ?? props.connectionId;
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
    <div v-if="lacking.length > 0" class="text-caption text-negative q-mt-xs" data-reconnect-short>
      The sign-in came back without {{ lacking.length === 1 ? 'the scope' : 'the scopes' }} {{ quoted(lacking) }}: it was
      not granted at {{ providerName }}, so the connection still cannot be bound. Press Reconnect and leave
      {{ lacking.length === 1 ? 'it' : 'them' }} ticked.
    </div>
    <div v-if="problem" class="text-caption text-negative q-mt-xs" data-reconnect-problem>{{ problem }}</div>
  </span>
</template>
