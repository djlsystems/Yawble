<script setup lang="ts">
import { computed, ref } from 'vue';
import { startConnection } from '../api/client';
import type { Connection, ConnectionProvider, ConnectionSlot } from '../api/types';
import { goTo } from '../lib/browserNavigation';
import { refusedReconnect } from '../lib/slotBinding';
import SlotConnect from './SlotConnect.vue';
import SlotReconnect from './SlotReconnect.vue';
import {
  connectionsForSlot,
  goneRefusal,
  guidedProviders,
  missingScopes,
  providerName,
  scopeRefusal,
  slotHint,
  statusLabel,
} from '../lib/connections';

/**
 * ONE CONNECTION SLOT'S PICKER, in a plugin member's settings - at hire and in Member settings. It
 * lists the connections of the providers the slot allows and stores the chosen connection's ID;
 * no token is anywhere near it.
 *
 * It says, before the save, what the Host would say of a connection that lacks a scope the slot
 * needs (refused at the save) -
 * with Reconnect, which asks the Host for that connection's consent page with the missing scopes
 * added and sends the browser there. The Host is still the check: it refuses the save in the same
 * words.
 *
 * CONNECT <PROVIDER> comes first while nothing suitable is bound, given the slot's `plugin`: Add
 * connection, started from this slot. When its sign-in completes, `bind` (where the member exists)
 * binds the new connection through the member's settings route and the opener shows it bound; with
 * no `bind` (a hire, an install) the connection is chosen here and the save binds it. A binding the
 * Host refuses is shown in its words, with Reconnect when only scopes are missing.
 *
 * With `reconnectInTab` (the solution wizard, whose answers a trip away would lose) Reconnect opens
 * the provider in another tab and waits here; the reconnected connection comes back as `connected`
 * and stays the binding, and a sign-in that granted fewer scopes than asked says so.
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
  /** The picker's label, when the slot's name alone does not say whose it is. */
  label?: string | undefined;
  /** The line under the picker, in place of the slot's own description and summary. */
  hint?: string | undefined;
  /** The plugin's name, for a slot whose plugin is not installed yet. */
  pluginName?: string | undefined;
  /** Reconnect in another tab, leaving this page as it is. */
  reconnectInTab?: boolean | undefined;
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

/**
 * The sentence the Host would refuse the binding with. A required slot left unbound is no refusal
 * here: the picker and Connect are right above, and the Host still hires and saves, so it says only
 * what is left to do.
 */
const unbound = computed(() => bound.value === '' && props.spec.required);

const refusal = computed(() => {
  if (bound.value === '') return '';
  if (!chosen.value) return props.connections.length > 0 ? goneRefusal(props.slotName) : '';
  if (missing.value.length > 0) return scopeRefusal(props.slotName, chosen.value, missing.value);
  return '';
});

/** The name of a connection's provider, for Reconnect's link. */
function providerOf(connectionId: string): string {
  const connection = props.connections.find((candidate) => candidate.id === connectionId);
  return connection ? providerName(connection.provider, props.providers) : 'the provider';
}

/** Reconnected in another tab: the lists are read again, and the binding stays the same connection. */
function reconnectedHere(connection: Connection) {
  emit('connected', connection);
  if (bindReconnect.value?.connectionId === connection.id && missingScopes(props.spec, connection).length === 0) {
    bindRefusal.value = '';
    bindReconnect.value = null;
  }
}

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
      :plugin-name="pluginName"
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
      :label="label ?? `Connection for ${slotName}`"
      :hint="hint ?? slotHint(spec, providers)"
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

    <div v-if="unbound" class="text-caption os-text-muted q-mt-xs" data-slot-unbound>
      Required: the member's runs are blocked until a connection is chosen here.
    </div>

    <div v-if="refusal" class="text-caption text-negative q-mt-xs row items-center q-gutter-x-sm" data-binding-refusal>
      <span>{{ refusal }}</span>
      <SlotReconnect
        v-if="missing.length > 0 && chosen && reconnectInTab"
        :connection-id="chosen.id"
        :scopes="missing"
        :provider-name="providerOf(chosen.id)"
        @reconnected="reconnectedHere"
      />
      <q-btn
        v-else-if="missing.length > 0"
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
      <SlotReconnect
        v-if="bindReconnect && reconnectInTab"
        :connection-id="bindReconnect.connectionId"
        :scopes="bindReconnect.scopes"
        :provider-name="providerOf(bindReconnect.connectionId)"
        @reconnected="reconnectedHere"
      />
      <q-btn
        v-else-if="bindReconnect"
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
