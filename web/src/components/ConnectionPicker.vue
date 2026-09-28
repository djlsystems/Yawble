<script setup lang="ts">
import { computed, ref } from 'vue';
import { startConnection } from '../api/client';
import type { Connection, ConnectionProvider, ConnectionSlot } from '../api/types';
import { goTo } from '../lib/browserNavigation';
import {
  connectionsForSlot,
  goneRefusal,
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
 */
const props = defineProps<{
  slotName: string;
  spec: ConnectionSlot;
  connections: Connection[];
  providers: ConnectionProvider[];
}>();

const bound = defineModel<string>({ required: true });

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

async function reconnect() {
  if (!chosen.value || reconnecting.value) return;

  reconnecting.value = true;
  reconnectProblem.value = '';

  try {
    const flow = await startConnection({ reconnectId: chosen.value.id, scopes: missing.value });
    goTo(flow.authorizationUrl);
  } catch (cause) {
    reconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    reconnecting.value = false;
  }
}
</script>

<template>
  <div class="connection-picker" :data-connection-slot="slotName">
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

    <div v-if="choices.length === 0" class="text-caption os-text-muted q-mt-xs" data-no-connections>
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
        @click="reconnect"
      />
    </div>
    <div v-if="reconnectProblem" class="text-caption text-negative q-mt-xs" data-reconnect-problem>
      {{ reconnectProblem }}
    </div>
  </div>
</template>
