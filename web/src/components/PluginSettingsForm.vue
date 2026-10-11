<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { listConnectionProviders, listConnections } from '../api/client';
import type { Connection, ConnectionProvider, PluginConfigField, SecretKeyState } from '../api/types';
import {
  defaultLabel,
  defaultValue,
  fieldBoundsHint,
  hasSlots,
  isDefault,
  missingRequired,
  outOfRange,
  secretKeySentence,
  setByPerson,
  settingsBody,
  type PluginFieldValues,
  type PluginSettingsShape,
} from '../lib/pluginSettings';
import ChipListInput from './ChipListInput.vue';
import ConnectionPicker from './ConnectionPicker.vue';
import SecretKeyField from './SecretKeyField.vue';

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
 * hold Save until it is fixed, as they do for a missing required field (`outOfRange`). Secrets are KEY NAMES, the logical key a
 * member is bound to, never a value: the settings carry none. Given `checkKey`, each key typed says
 * whether the Host has it set - by whom and when, or from its environment - and offers the key's
 * write-only value field (`SecretKeyField`: Set, Replace, Clear) before the CLI's `secret set`; a
 * write there refreshes what is said. Connection slots are one picker each
 * (`ConnectionPicker`), storing a connection's id, never a token.
 *
 * The "as JSON" view is read-only, and it is exactly `settingsBody` - what will be stored - for
 * checking, never for editing.
 */
const props = defineProps<{
  shape: PluginSettingsShape;
  /** The plugin's id: with it, each slot offers Connect where nothing suitable is bound. */
  plugin?: string | undefined;
  /** Binds a slot of the member being edited at the Host, throwing its refusal. Absent at hire. */
  bindSlot?: ((slot: string, connectionId: string) => Promise<void>) | undefined;
  /** Asks the Host whether a key is set, by name. Absent: no state is shown under a secret. */
  checkKey?: ((key: string) => Promise<SecretKeyState>) | undefined;
}>();

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

/**
 * EACH SECRET KEY'S STATE ON THE HOST, by the key as typed: asked a moment after typing stops, once
 * per key. A failed ask shows nothing rather than a guess.
 */
const keyStates = ref<Record<string, SecretKeyState>>({});
let keyTimer: ReturnType<typeof setTimeout> | undefined;

function checkKeys() {
  clearTimeout(keyTimer);
  const check = props.checkKey;
  if (!check) return;

  keyTimer = setTimeout(() => {
    for (const key of new Set(Object.values(secrets.value).map((value) => value.trim()))) {
      if (key === '' || keyStates.value[key]) continue;
      check(key).then(
        (state) => (keyStates.value = { ...keyStates.value, [key]: state }),
        () => undefined,
      );
    }
  }, 250);
}

/** A write from a key's value field: the Host's answer is the key's state now. */
function keyChanged(state: SecretKeyState) {
  keyStates.value = { ...keyStates.value, [state.key]: state };
}

function keyState(field: string | number): SecretKeyState | null {
  const key = (secrets.value[field] ?? '').trim();
  return key === '' ? null : keyStates.value[key] ?? null;
}

watch(secrets, checkKeys, { immediate: true });
onBeforeUnmount(() => clearTimeout(keyTimer));

/** The essential settings first, then the advanced ones, each in the manifest's order. */
const fieldOrder = computed(() => {
  const names = Object.keys(props.shape.config);
  return [
    ...names.filter((name) => !props.shape.config[name]!.advanced),
    ...names.filter((name) => props.shape.config[name]!.advanced),
  ];
});
const firstAdvanced = computed(() => fieldOrder.value.find((name) => props.shape.config[name]!.advanced) ?? null);
const advancedCount = computed(() => fieldOrder.value.filter((name) => props.shape.config[name]!.advanced).length);
const advancedShown = ref(false);
/** Open when a person opened it, or when an advanced setting needs fixing. */
const advancedOpen = computed(
  () =>
    advancedShown.value
    || fieldOrder.value.some((name) => props.shape.config[name]!.advanced && (missing.value.includes(name) || bounds.value[name])),
);

/** A setting's explanation, and its number bounds, for its info icon. */
function helpFor(field: PluginConfigField): string {
  return [field.description, fieldBoundsHint(field)].filter((part) => part).join(' ');
}

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
  const where = props.checkKey ? 'the value is set in the field under it' : 'the value is set with secret set';
  return `${description ? description + ' ' : ''}The name of the key, never its value: ${where}.${required ? '' : ' Optional.'}`;
}
</script>

<template>
  <div class="plugin-settings q-gutter-sm" data-plugin-settings>
    <template v-for="key in fieldOrder" :key="`config-${key}`">
      <!-- ADVANCED SETTINGS, the ones most people never change (`"advanced": true` in the
           manifest), behind one toggle before the first of them. It opens by itself when one of
           them needs fixing, so a problem is never hidden. -->
      <q-btn
        v-if="key === firstAdvanced"
        flat
        dense
        no-caps
        align="left"
        class="plugin-settings-advanced"
        :icon="advancedOpen ? 'keyboard_arrow_up' : 'keyboard_arrow_down'"
        :label="`Advanced settings (${advancedCount})`"
        data-advanced-toggle
        @click="advancedShown = !advancedShown"
      />
      <div
        v-if="!shape.config[key]!.advanced || advancedOpen"
        class="plugin-setting"
        :data-setting="key"
        :data-advanced="shape.config[key]!.advanced ? '' : undefined"
      >
        <div v-if="shape.config[key]!.type === 'bool'" class="row items-center no-wrap q-gutter-x-xs">
          <q-toggle
            :model-value="config[key] === true"
            dense
            :label="key"
            @update:model-value="(value: boolean) => set(key, value)"
          />
          <FieldHelp :text="helpFor(shape.config[key]!)" />
        </div>

        <ChipListInput
          v-else-if="shape.config[key]!.type === 'list'"
          :model-value="listOf(key)"
          :label="key"
          :hint="helpFor(shape.config[key]!)"
          :options="shape.config[key]!.enum && shape.config[key]!.enum!.length > 0 ? shape.config[key]!.enum : null"
          @update:model-value="(value: string[]) => set(key, value)"
        />

        <q-select
          v-else-if="shape.config[key]!.enum && shape.config[key]!.enum!.length > 0"
          :model-value="text(key)"
          :options="shape.config[key]!.enum!"
          outlined
          dense
          :label="key"
          :error="missing.includes(key) ? true : undefined"
          @update:model-value="(value: string) => set(key, value)"
        >
          <template #append><FieldHelp :text="helpFor(shape.config[key]!)" /></template>
        </q-select>

        <q-input
          v-else
          :model-value="text(key)"
          outlined
          dense
          :type="shape.config[key]!.type === 'number' ? 'number' : 'text'"
          :label="key"
          :error="missing.includes(key) || bounds[key] ? true : undefined"
          :error-message="bounds[key] ?? ''"
          :data-out-of-range="bounds[key] ? '' : undefined"
          @update:model-value="(value) => set(key, value === null ? '' : String(value))"
        >
          <template #append><FieldHelp :text="helpFor(shape.config[key]!)" /></template>
        </q-input>

        <div class="plugin-setting-marks text-caption os-text-muted row items-center q-gutter-x-sm">
          <span v-if="defaultLabel(shape.config[key]!)" data-default>{{ defaultLabel(shape.config[key]!) }}</span>
          <span v-if="shape.config[key]!.required" class="text-weight-medium" data-required>Required</span>
          <span v-if="setByPerson(shape.config[key]!)" class="text-weight-medium" data-person-only>Set by a person only</span>
          <q-space />
          <q-btn
            flat
            dense
            no-caps
            size="sm"
            label="Reset to default"
            :aria-label="`Reset ${key} to default`"
            :disable="isDefault(shape.config[key]!, config[key])"
            @click="reset(key)"
          />
        </div>
      </div>
    </template>

    <template v-for="(secret, key) in shape.secrets" :key="`secret-${key}`">
      <q-input
        :model-value="secrets[key] ?? ''"
        outlined
        dense
        :label="`Secret ${key}: key name`"
        :error="missing.includes(String(key)) ? true : undefined"
        autocomplete="off"
        spellcheck="false"
        @update:model-value="(value) => (secrets = { ...secrets, [key]: value === null ? '' : String(value) })"
      >
        <template #append><FieldHelp :text="secretHint(secret.description, secret.required)" /></template>
      </q-input>
      <div
        v-if="keyState(key)"
        class="text-caption q-mt-xs"
        :class="keyState(key)!.set ? 'os-text-muted' : 'text-warning'"
        :data-secret-key-state="keyState(key)!.set ? 'set' : 'unset'"
        :data-secret-field="key"
      >
        {{ secretKeySentence(keyState(key)!, secret.required) }}
      </div>
      <SecretKeyField v-if="keyState(key)" :state="keyState(key)!" @changed="keyChanged" />
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
        :plugin="plugin"
        :bind="bindSlot ? (id: string) => bindSlot!(String(key), id) : undefined"
        @update:model-value="(value: string) => (connections = { ...connections, [key]: value })"
        @connected="loadConnections"
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
  font-size: 0.75rem;
  background: var(--os-chrome);
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
