<script setup lang="ts">
import { computed, ref } from 'vue';
import { startConnection } from '../api/client';
import type { Connection, ConnectionProvider, ConnectionSlot } from '../api/types';
import { goTo } from '../lib/browserNavigation';
import { refusedReconnect } from '../lib/slotBinding';
import SlotConnect from './SlotConnect.vue';
import {
  connectionsForSlot,
  goneRefusal,
  guidedProviders,
  missingScopes,
  providerName,
  scopeRefusal,
  slotSummary,
  statusLabel,
  unboundRefusal,
} from '../lib/connections';

/**
 * ONE CONNECTION SLOT'S PICKER, in a plugin member's settings - at hire and in Member settings. It
 * lists the connections of the providers the slot allows and stores the chosen connection's ID;
 * no token is anywhere near it.
 *
 * It says, before the save, what the Host would say: a required slot left unbound (the sentence the
 * member's runs will be blocked with - the Host still hires and saves, so the account can be
 * connected later), and a connection that lacks a scope the slot needs (refused at the save) -
 * with Reconnect, which asks the Host for that connection's consent page with the missing scopes
 * added and sends the browser there. The Host is still the check: it refuses the save in the same
 * words.
 *
 * CONNECT <PROVIDER> comes first while nothing suitable is bound, given the slot's `plugin`: Add
 * connection, started from this slot. When its sign-in completes, `bind` (where the member exists)
 * binds the new connection through the member's settings route and the opener shows it bound; with
 * no `bind` (a hire, an install) the connection is chosen here and the save binds it. A binding the
 * Host refuses is shown in its words, with Reconnect when only scopes are missing.
 */
const props = defineProps<{
  slotName: string;
  spec: ConnectionSlot;
  connections: Connection[];
  providers: ConnectionProvider[];
  /** The plugin declaring the slot: with it, Connect is offered. */
  plugin?: string | undefined;
  /** Binds a connection to this slot at the Host, throwing its refusal. */
  bind?: ((connectionId: string) => Promise<void>) | undefined;
}>();

const bound = defineModel<string>({ required: true });
const emit = defineEmits<{
  /** A sign-in from this slot made a connection: read the lists again. */
  connected: [connection: Connection];
  /** Add connection, opened from this slot, was closed. */
  closed: [];
}>();

const choices = computed(() => connectionsForSlot(props.spec, props.connections));

const options = computed(() =>
  choices.value.map((connection) => ({
    value: connection.id,
    label:
      `${connection.name} - ${connection.account} (${providerName(connection.provider, props.providers)})` +
      (connection.status === 'ok' ? '' : `, ${statusLabel(connection)}`),
  })),
);

const chosen = computed(() => props.connections.find((connection) => connection.id === bound.value) ?? null);

const missing = computed(() => (chosen.value ? missingScopes(props.spec, chosen.value) : []));

/** The sentence the Host would refuse the binding with, or block the member's runs with. */
const refusal = computed(() => {
  if (bound.value === '') return props.spec.required ? unboundRefusal(props.slotName) : '';
  if (!chosen.value) return props.connections.length > 0 ? goneRefusal(props.slotName) : '';
  if (missing.value.length > 0) return scopeRefusal(props.slotName, chosen.value, missing.value);
  return '';
});

const reconnecting = ref(false);
const reconnectProblem = ref('');

async function reconnect(asked?: { connectionId: string; scopes: string[] }) {
  const target = asked ?? (chosen.value ? { connectionId: chosen.value.id, scopes: missing.value } : null);
  if (!target || reconnecting.value) return;

  reconnecting.value = true;
  reconnectProblem.value = '';

  try {
    const flow = await startConnection({ reconnectId: target.connectionId, scopes: target.scopes });
    goTo(flow.authorizationUrl);
  } catch (cause) {
    reconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    reconnecting.value = false;
  }
}

// --- Connect, from this slot ----------------------------------------------------------------------

/**
 * Connect is offered while nothing suitable is bound - none, or one that no longer exists - to a
 * slot Add connection can sign in for. A custom provider's slot says where its connection is made.
 */
const offerConnect = computed(() => !chosen.value && !!props.plugin && guidedProviders(props.spec).length > 0);

const binding = ref(false);
const bindRefusal = ref('');
const bindReconnect = ref<{ connectionId: string; scopes: string[] } | null>(null);

async function connected(connection: Connection) {
  emit('connected', connection);
  bindRefusal.value = '';
  bindReconnect.value = null;

  if (!props.bind) {
    bound.value = connection.id;
    return;
  }

  binding.value = true;
  try {
    await props.bind(connection.id);
  } catch (cause) {
    bindRefusal.value = cause instanceof Error ? cause.message : String(cause);
    bindReconnect.value = refusedReconnect(cause);
  } finally {
    binding.value = false;
  }
}
</script>

<template>
  <div class="connection-picker" :data-connection-slot="slotName">
    <SlotConnect
      v-if="plugin"
      class="q-mb-xs"
      :plugin="plugin"
      :slot-name="slotName"
      :spec="spec"
      :providers="providers"
      :connections="connections"
      :offered="offerConnect"
      :busy="binding"
      @connected="connected"
      @closed="emit('closed')"
    >
      <div v-if="choices.length > 0" class="text-caption os-text-muted q-mt-xs">Or choose an existing connection:</div>
    </SlotConnect>

    <q-select
      :model-value="bound === '' ? null : bound"
      :options="options"
      emit-value
      map-options
      outlined
      dense
      clearable
      :label="`Connection for ${slotName}`"
      :hint="`${spec.description ? spec.description + ' ' : ''}${slotSummary(spec, providers)}${spec.required ? '' : ' Optional.'}`"
      @update:model-value="(value: string | null) => (bound = value ?? '')"
    >
      <template #no-option>
        <q-item>
          <q-item-section class="os-text-muted">
            No connection of that kind yet. A person connects one in Admin → Connections.
          </q-item-section>
        </q-item>
      </template>
    </q-select>

    <div v-if="choices.length === 0 && !offerConnect" class="text-caption os-text-muted q-mt-xs" data-no-connections>
      No connection of that kind yet. A person connects one in Admin → Connections.
    </div>

    <div v-if="refusal" class="text-caption text-negative q-mt-xs row items-center q-gutter-x-sm" data-binding-refusal>
      <span>{{ refusal }}</span>
      <q-btn
        v-if="missing.length > 0"
        flat
        dense
        no-caps
        size="sm"
        color="primary"
        label="Reconnect"
        :loading="reconnecting"
        @click="reconnect()"
      />
    </div>
    <div v-if="bindRefusal" class="text-caption text-negative q-mt-xs row items-center q-gutter-x-sm" data-bind-refused>
      <span>{{ bindRefusal }}</span>
      <q-btn
        v-if="bindReconnect"
        flat
        dense
        no-caps
        size="sm"
        color="primary"
        label="Reconnect"
        :loading="reconnecting"
        @click="reconnect(bindReconnect)"
      />
    </div>
    <div v-if="reconnectProblem" class="text-caption text-negative q-mt-xs" data-reconnect-problem>
      {{ reconnectProblem }}
    </div>
  </div>
</template>
