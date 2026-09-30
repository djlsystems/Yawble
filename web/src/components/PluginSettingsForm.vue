<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { listConnectionProviders, listConnections } from '../api/client';
import type { Connection, ConnectionProvider } from '../api/types';
import {
  addToList,
  defaultLabel,
  defaultValue,
  hasSlots,
  isDefault,
  missingRequired,
  setByPerson,
  settingsBody,
  type PluginFieldValues,
  type PluginSettingsShape,
} from '../lib/pluginSettings';
import ConnectionPicker from './ConnectionPicker.vue';

/**
 * A PLUGIN MEMBER'S SETTINGS, GENERATED FROM ITS MANIFEST. The one editor Add member (a hire) and
 * Member settings (an edit after hire) both show, so the two cannot drift on what a field looks like
 * or what is saved.
 *
 * NO FREE-FORM JSON. One input per `config` field, typed - text for a string, a number box for a
 * number, a toggle for a bool, a dropdown for a field with `enum`, chips for a list (type, Enter to
 * add, × to remove, limited to the `enum` when there is one) - so the shape saved is always one the
 * plugin accepts. Each field says its default, whether it is required and whether only a person may
 * set it, and can be put back to its default. Secrets are KEY NAMES, the logical key set with
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

/** What is typed into each list field's box before Enter adds it, and why the last Enter did not. */
const pending = ref<Record<string, string>>({});
const listProblem = ref<Record<string, string>>({});

function set(name: string, value: string | boolean | string[]) {
  config.value = { ...config.value, [name]: value };
}

function reset(name: string) {
  const field = props.shape.config[name];
  if (!field) return;

  set(name, defaultValue(field));
  listProblem.value = { ...listProblem.value, [name]: '' };
}

/**
 * Enter in a list's box adds the chip. It STOPS HERE: the dialog's form submits on Enter, and a
 * person adding a value has not asked to save.
 */
function onListKey(name: string, event: KeyboardEvent) {
  if (event.key !== 'Enter' || event.isComposing) return;

  event.preventDefault();
  event.stopPropagation();

  commitList(name);
}

/**
 * THE TYPED VALUE BECOMES A CHIP ON ENTER AND WHEN THE BOX LOSES FOCUS - pressing Save included,
 * which takes the focus before its click. Only on Enter, a person who typed a position and pressed
 * Save saw "saved" while nothing was sent: the settings had not moved, so the dialog wrote nothing.
 */
function commitList(name: string) {
  if ((pending.value[name] ?? '').trim() === '') return;

  const field = props.shape.config[name];
  if (!field) return;

  const result = addToList(field, config.value[name], pending.value[name] ?? '');

  if ('problem' in result) {
    listProblem.value = { ...listProblem.value, [name]: result.problem };
    return;
  }

  set(name, result.list);
  pending.value = { ...pending.value, [name]: '' };
  listProblem.value = { ...listProblem.value, [name]: '' };
}

/**
 * Every list's typed-but-not-added value, made a chip - what a dialog calls as its Save or Hire
 * starts. Leaving a box does it too, but Quasar reports the focus loss on a timer, and a quick
 * click must not beat it.
 */
function commitAllLists() {
  for (const [name, field] of Object.entries(props.shape.config)) {
    if (field.type === 'list') commitList(name);
  }
}

defineExpose({ commitAllLists });

function removeFromList(name: string, item: string) {
  const current = config.value[name];
  set(name, Array.isArray(current) ? current.filter((entry) => entry !== item) : []);
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

      <template v-else-if="field.type === 'list'">
        <div class="text-body2">{{ key }}</div>
        <div class="plugin-setting-chips" data-chips>
          <q-chip
            v-for="item in listOf(String(key))"
            :key="item"
            dense
            removable
            :remove-aria-label="`Remove ${item}`"
            :data-chip="item"
            @remove="removeFromList(String(key), item)"
          >{{ item }}</q-chip>
        </div>
        <q-input
          :model-value="pending[key] ?? ''"
          outlined
          dense
          :label="`Add to ${key}`"
          :hint="field.enum && field.enum.length > 0
            ? `Type a value and press Enter to add another; what is typed is kept when you leave the box. Allowed: ${field.enum.join(', ')}.`
            : 'Type a value and press Enter to add another; what is typed is kept when you leave the box.'"
          :error="listProblem[key] ? true : undefined"
          :error-message="listProblem[key] ?? ''"
          autocomplete="off"
          @update:model-value="(value) => (pending = { ...pending, [key]: value === null ? '' : String(value) })"
          @keydown="(event: KeyboardEvent) => onListKey(String(key), event)"
          @blur="commitList(String(key))"
        />
      </template>

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
        :error="missing.includes(String(key)) ? true : undefined"
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
.plugin-setting-chips {
  min-height: 8px;
}

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
