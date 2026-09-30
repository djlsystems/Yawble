<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { listConnectionProviders, listConnections } from '../api/client';
import type { Connection, ConnectionProvider } from '../api/types';
import {
  defaultLabel,
  defaultValue,
  fieldBoundsHint,
  hasSlots,
  isDefault,
  missingRequired,
  outOfRange,
  setByPerson,
  settingsBody,
  type PluginFieldValues,
  type PluginSettingsShape,
} from '../lib/pluginSettings';
import ChipListInput from './ChipListInput.vue';
import ConnectionPicker from './ConnectionPicker.vue';

/**
 * A PLUGIN MEMBER'S SETTINGS, GENERATED FROM ITS MANIFEST. The one editor Add member (a hire) and
 * Member settings (an edit after hire) both show, so the two cannot drift on what a field looks like
 * or what is saved.
 *
 * NO FREE-FORM JSON. One input per `config` field, typed - text for a string, a number box for a
 * number, a toggle for a bool, a dropdown for a field with `enum`, chips for a list (`ChipListInput`:
 * chosen from the `enum` when there is one, else added through its dialog; × removes) - so the shape saved is always one the
 * plugin accepts. Each field says its default, whether it is required and whether only a person may
 * set it, and can be put back to its default. A number field with manifest bounds says them in its
 * hint and shows a value outside them as out of range - typed, or already stored - and the dialogs
 * hold Save until it is fixed, as they do for a missing required field (`outOfRange`). Secrets are KEY NAMES, the logical key set with
 * `secret set`, never a value: no route carries one. Connection slots are one picker each
 * (`ConnectionPicker`), storing a connection's id, never a token.
 *
 * The "as JSON" view is read-only, and it is exactly `settingsBody` - what will be stored - for
 * checking, never for editing.
 */
const props = defineProps<{ shape: PluginSettingsShape }>();

const config = defineModel<PluginFieldValues>('config', { required: true });
const secrets = defineModel<Record<string, string>>('secrets', { required: true });
/** Slot -> the chosen connection's id. Only a plugin that declares slots has any. */
const connections = defineModel<Record<string, string>>('connections', { default: () => ({}) });

const missing = computed(() => missingRequired(props.shape, config.value, secrets.value));
/** Number fields outside their bounds, with the sentence shown under each - typed or stored. */
const bounds = computed(() => outOfRange(props.shape, config.value));

const body = computed(() => settingsBody(props.shape, config.value, secrets.value, connections.value));

/**
 * THE CONNECTIONS A SLOT CAN TAKE, read when the plugin has slots and not otherwise - a plugin
 * without them makes no connections call at all. A failure is said under the slots, not thrown: the
 * rest of the form is still usable.
 */
const available = ref<Connection[]>([]);
const providers = ref<ConnectionProvider[]>([]);
const connectionsProblem = ref('');

async function loadConnections() {
  if (!hasSlots(props.shape)) return;

  connectionsProblem.value = '';
  try {
    [available.value, providers.value] = await Promise.all([listConnections(), listConnectionProviders()]);
  } catch (cause) {
    connectionsProblem.value = `The connections could not be read: ${cause instanceof Error ? cause.message : String(cause)}`;
  }
}

onMounted(() => void loadConnections());
watch(() => props.shape, () => void loadConnections());

const showJson = ref(false);
const json = computed(() => JSON.stringify(body.value, null, 2));

function set(name: string, value: string | boolean | string[]) {
  config.value = { ...config.value, [name]: value };
}

function reset(name: string) {
  const field = props.shape.config[name];
  if (!field) return;

  set(name, defaultValue(field));
}

function listOf(name: string): string[] {
  const value = config.value[name];
  return Array.isArray(value) ? value : [];
}

function text(name: string): string {
  const value = config.value[name];
  return typeof value === 'string' ? value : '';
}

function secretHint(description: string | null | undefined, required: boolean) {
  return `${description ? description + ' ' : ''}The name of a key set with secret set, never its value.${required ? '' : ' Optional.'}`;
}
</script>

<template>
  <div class="plugin-settings q-gutter-sm" data-plugin-settings>
    <div
      v-for="(field, key) in shape.config"
      :key="`config-${key}`"
      class="plugin-setting"
      :data-setting="key"
    >
      <q-toggle
        v-if="field.type === 'bool'"
        :model-value="config[key] === true"
        dense
        :label="String(key)"
        @update:model-value="(value: boolean) => set(String(key), value)"
      />

      <ChipListInput
        v-else-if="field.type === 'list'"
        :model-value="listOf(String(key))"
        :label="String(key)"
        :options="field.enum && field.enum.length > 0 ? field.enum : null"
        @update:model-value="(value: string[]) => set(String(key), value)"
      />

      <q-select
        v-else-if="field.enum && field.enum.length > 0"
        :model-value="text(String(key))"
        :options="field.enum"
        outlined
        dense
        :label="String(key)"
        :error="missing.includes(String(key)) ? true : undefined"
        @update:model-value="(value: string) => set(String(key), value)"
      />

      <q-input
        v-else
        :model-value="text(String(key))"
        outlined
        dense
        :type="field.type === 'number' ? 'number' : 'text'"
        :label="String(key)"
        :hint="fieldBoundsHint(field) ?? undefined"
        :error="missing.includes(String(key)) || bounds[key] ? true : undefined"
        :error-message="bounds[key] ?? ''"
        :data-out-of-range="bounds[key] ? '' : undefined"
        @update:model-value="(value) => set(String(key), value === null ? '' : String(value))"
      />

      <div class="plugin-setting-marks text-caption os-text-muted row items-center q-gutter-x-sm">
        <span v-if="field.description" data-description>{{ field.description }}</span>
        <span v-if="defaultLabel(field)" data-default>{{ defaultLabel(field) }}</span>
        <span v-if="field.required" class="text-weight-medium" data-required>Required</span>
        <span v-if="setByPerson(field)" class="text-weight-medium" data-person-only>Set by a person only</span>
        <q-space />
        <q-btn
          flat
          dense
          no-caps
          size="sm"
          label="Reset to default"
          :aria-label="`Reset ${key} to default`"
          :disable="isDefault(field, config[key])"
          @click="reset(String(key))"
        />
      </div>
    </div>

    <template v-for="(secret, key) in shape.secrets" :key="`secret-${key}`">
      <q-input
        :model-value="secrets[key] ?? ''"
        outlined
        dense
        :label="`Secret ${key}: key name`"
        :hint="secretHint(secret.description, secret.required)"
        :error="missing.includes(String(key)) ? true : undefined"
        autocomplete="off"
        spellcheck="false"
        @update:model-value="(value) => (secrets = { ...secrets, [key]: value === null ? '' : String(value) })"
      />
    </template>

    <!-- CONNECTION SLOTS: one picker each, listing connections of an allowed provider. The member
         stores the connection's id, never a token. -->
    <template v-if="hasSlots(shape)">
      <ConnectionPicker
        v-for="(declared, key) in shape.connections"
        :key="`connection-${key}`"
        :model-value="connections[key] ?? ''"
        :slot-name="String(key)"
        :spec="declared"
        :connections="available"
        :providers="providers"
        @update:model-value="(value: string) => (connections = { ...connections, [key]: value })"
      />
      <div v-if="connectionsProblem" class="text-caption text-negative" data-connections-problem>
        {{ connectionsProblem }}
      </div>
    </template>

    <div>
      <q-btn
        flat
        dense
        no-caps
        size="sm"
        icon="data_object"
        :label="showJson ? 'Hide JSON' : 'As JSON'"
        @click="showJson = !showJson"
      />
      <!-- READ-ONLY, and exactly what will be stored: only fields that differ from their default,
           and secrets as key names. For checking, never for editing. -->
      <pre v-if="showJson" class="plugin-settings-json mono" data-settings-json>{{ json }}</pre>
    </div>
  </div>
</template>

<style scoped>

.plugin-settings-json {
  margin: 4px 0 0;
  padding: 8px;
  font-size: 12px;
  background: var(--os-chrome);
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
