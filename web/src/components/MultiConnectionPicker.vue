<script setup lang="ts">
import { computed } from 'vue';
import type { Connection, ConnectionProvider, ConnectionSlot } from '../api/types';
import { providerName, statusLabel, withoutBindElsewhere } from '../lib/connections';
import ConnectionPicker from './ConnectionPicker.vue';

/**
 * A SLOT THAT TAKES SEVERAL CONNECTIONS (`multiple`), in a plugin member's settings: every connection
 * bound to it is listed, each with its own Remove, and one more is added below with the same picker a
 * one-connection slot has (`ConnectionPicker`, with New connection... and Connect). Choosing one adds it
 * to the list; it never replaces another. A connection leaves the list only by its Remove, which the
 * person sees, and the dialog's Save then sends every connection still listed - and, to the Host, which
 * ones were removed. A one-connection slot is never this: it stays one `ConnectionPicker`.
 */
const props = defineProps<{
  slotName: string;
  spec: ConnectionSlot;
  connections: Connection[];
  providers: ConnectionProvider[];
  plugin?: string | undefined;
  /** Binds one more connection to this slot at the Host (a sign-in made from it), throwing its refusal. */
  bind?: ((connectionId: string) => Promise<void>) | undefined;
  assigned?: readonly string[] | undefined;
}>();

/** The connection ids bound to the slot, in order. */
const bound = defineModel<string[]>({ required: true });

/** A listed connection as the picker names it; an id alone for one this person cannot read. */
function nameOf(id: string): string {
  const connection = props.connections.find((candidate) => candidate.id === id);
  if (!connection) return `A connection that no longer exists (${id})`;
  return (
    `${connection.name} - ${connection.account} (${providerName(connection.provider, props.providers)})` +
    (connection.status === 'ok' ? '' : `, ${statusLabel(connection)}`)
  );
}

/** Only connections not yet listed are offered to add. */
const addable = computed(() => props.connections.filter((connection) => !bound.value.includes(connection.id)));

function add(id: string) {
  if (id !== '' && !bound.value.includes(id)) bound.value = [...bound.value, id];
}

function remove(id: string) {
  bound.value = bound.value.filter((candidate) => candidate !== id);
}

/** A sign-in from this slot bound a connection at the Host: it joins the list. */
async function bindOne(connectionId: string) {
  await props.bind!(connectionId);
  add(connectionId);
}

/** The slot as this control shows it: the plugin's words, less any telling the person to bind elsewhere. */
const shown = computed<ConnectionSlot>(() => {
  const description = withoutBindElsewhere(props.spec.description);
  return { ...props.spec, description: description === '' ? null : description };
});

const help = computed(
  () =>
    `Every connection listed here is bound to ${props.slotName}. Add another with the picker below; `
    + 'Remove takes one off, and Save keeps every one still listed.',
);
</script>

<template>
  <div class="multi-connection-picker" :data-connection-slot="slotName" data-connection-multiple>
    <div class="text-caption text-weight-medium" data-multiple-label>Connections for {{ slotName }}</div>
    <!-- An optional slot's description is the picker's own line below; a required one's is here. -->
    <div v-if="shown.required && shown.description" class="text-caption os-text-muted q-mb-xs" data-slot-description>{{ shown.description }}</div>

    <q-list v-if="bound.length > 0" dense bordered separator class="rounded-borders q-mb-xs" data-bound-connections>
      <q-item v-for="id in bound" :key="id" data-bound-connection :data-connection-id="id">
        <q-item-section>{{ nameOf(id) }}</q-item-section>
        <q-item-section side>
          <q-btn
            flat
            dense
            no-caps
            size="sm"
            color="negative"
            label="Remove"
            :aria-label="`Remove ${nameOf(id)} from ${slotName}`"
            data-remove-connection
            @click="remove(id)"
          />
        </q-item-section>
      </q-item>
    </q-list>
    <div v-else class="text-caption os-text-muted q-mb-xs" data-no-bound-connections>
      No connection is bound to {{ slotName }} yet.
    </div>
    <div class="text-caption os-text-muted q-mb-xs" data-multiple-help>{{ help }}</div>

    <ConnectionPicker
      :key="bound.join(',')"
      model-value=""
      :slot-name="slotName"
      :spec="{ ...shown, required: shown.required && bound.length === 0 }"
      :connections="addable"
      :assigned="assigned"
      :providers="providers"
      :plugin="plugin"
      :bind="bind ? bindOne : undefined"
      :no-preselect="bound.length > 0"
      :label="`Add a connection to ${slotName}`"
      :hint="help"
      data-add-connection
      @update:model-value="add"
    />
  </div>
</template>
