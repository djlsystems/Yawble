<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { Connection, ConnectionProvider, ConnectionSlot } from '../api/types';
import { guidedProviders, providerName } from '../lib/connections';
import ConnectDialog from './ConnectDialog.vue';

/**
 * CONNECT <PROVIDER>, FOR ONE SLOT: the button and the Add connection it opens, started from that
 * slot - its providers (a choice of them when it takes more than one) and the scopes of that one
 * slot. `connected` names the connection the sign-in made; binding it is the opener's.
 *
 * Shown only for a slot that takes Google or Microsoft: those are the services Add connection signs
 * in to. A custom provider's account is still connected in Admin → Connections. The dialog stays
 * mounted while the button is not `offered`, so binding the new connection - which ends the offer -
 * does not close it under the person.
 */
const props = withDefaults(
  defineProps<{
    plugin: string;
    slotName: string;
    spec: ConnectionSlot;
    /** The plugin's name, for a slot whose plugin is not installed yet. */
    pluginName?: string | undefined;
    providers: ConnectionProvider[];
    connections: Connection[];
    /** Whether the button shows: nothing suitable is bound. */
    offered?: boolean;
    busy?: boolean;
  }>(),
  { offered: true, busy: false },
);

const emit = defineEmits<{
  connected: [connection: Connection];
  /** The dialog was closed. */
  closed: [];
}>();

const connectable = computed(() => guidedProviders(props.spec));
const label = computed(() => `Connect ${connectable.value.map((id) => providerName(id, props.providers)).join(' or ')}`);
const need = computed(() => ({
  plugin: props.plugin,
  slot: props.slotName,
  providers: connectable.value,
  scopes: props.spec.scopes,
  pluginName: props.pluginName,
}));

const open = ref(false);
watch(open, (showing, was) => {
  if (was && !showing) emit('closed');
});
</script>

<template>
  <div v-if="connectable.length > 0">
    <div v-if="offered" data-slot-connect>
      <q-btn unelevated dense no-caps color="primary" icon="add" :label="label" :loading="busy" @click="open = true" />
      <slot />
    </div>
    <ConnectDialog
      v-model="open"
      :providers="providers"
      :connections="connections"
      :need="need"
      @connected="(connection: Connection) => emit('connected', connection)"
    />
  </div>
</template>
