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
import { isClear, writeKeyValues, type PendingKeyValues } from '../lib/secretKeyValues';
import { productCli } from '../presentation/product';

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
 * write-only value field (`SecretKeyField`), and Clear for a key set from the screen. What is typed
 * there is written by the dialog's own button, through `saveKeys`, before the settings are saved;
 * the Host's answer is then what is said. Connection slots are one picker each
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
  /** The dialog's button that saves the form, named in what a key not set says. */
  saveLabel?: string | undefined;
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

/** Values typed, and clears asked for, by key name: written by the dialog's button (`saveKeys`). */
const pending = ref<PendingKeyValues>({});

function typedValue(key: string): string {
  const entry = pending.value[key];
  return entry && !isClear(entry) ? entry.value : '';
}

function typeValue(key: string, value: string) {
  pending.value = { ...pending.value, [key]: { value } };
}

function toggleClear(key: string) {
  const { [key]: entry, ...rest } = pending.value;
  pending.value = isClear(entry) ? rest : { ...rest, [key]: { clear: true } };
}

/**
 * Writes what was typed into the key fields, each answered by the Host's state of the key; the
 * dialog calls it on its button before it saves the settings, and a refusal (a sentence naming the
 * key, never the value) stops the save.
 */
async function saveKeys(): Promise<void> {
  await writeKeyValues(pending.value, (state) => {
    const { [state.key]: _, ...rest } = pending.value;
    pending.value = rest;
    keyStates.value = { ...keyStates.value, [state.key]: state };
  });
}

defineExpose({ saveKeys });

/** What is said under a key: what waits for the button, else the Host's state of it. */
function keySaying(state: SecretKeyState, required: boolean): string {
  const entry = pending.value[state.key];
  const button = props.saveLabel ?? 'Save';
  if (isClear(entry)) return `Cleared when you press ${button}.`;
  if (entry && entry.value !== '') return `Saved when you press ${button}.`;
  return secretKeySentence(state, required, button);
}

function valueHelp(key: string): string {
  return `The value is sent to the Host when you press ${props.saveLabel ?? 'Save'} and is never shown again. It can also be set with ${productCli} secret set ${key}, then ${productCli} up.`;
}

function keyState(field: string | number): SecretKeyState | null {
  const key = (secrets.value[field] ?? '').trim();
  return key === '' ? null : keyStates.value[key] ?? null;
}

watch(secrets, checkKeys, { immediate: true });
onBeforeUnmount(() => clearTimeout(keyTimer));

/** The essential settings, then (after the secrets and slots) the advanced ones, each in the manifest's order. */
const essential = computed(() => Object.keys(props.shape.config).filter((name) => !props.shape.config[name]!.advanced));
const advancedNames = computed(() => Object.keys(props.shape.config).filter((name) => props.shape.config[name]!.advanced));
const fieldOrder = computed(() => [...essential.value, ...advancedNames.value]);
const advancedCount = computed(() => advancedNames.value.length);
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

/**
 * THE FORM'S ROWS, IN THE ORDER A PERSON READS THEM: the everyday settings, the secrets, the
 * connection slots, then the advanced settings behind their one toggle - so nothing an everyday
 * person must fill (a key) sits under Advanced.
 */
type Row =
  | { kind: 'config'; name: string }
  | { kind: 'secret'; name: string }
  | { kind: 'slots' }
  | { kind: 'advanced' };

const rows = computed<Row[]>(() => [
  ...essential.value.map((name): Row => ({ kind: 'config', name })),
  ...Object.keys(props.shape.secrets).map((name): Row => ({ kind: 'secret', name })),
  ...(hasSlots(props.shape) ? [{ kind: 'slots' } as Row] : []),
  ...(advancedCount.value > 0 ? [{ kind: 'advanced' } as Row] : []),
  ...(advancedOpen.value ? advancedNames.value.map((name): Row => ({ kind: 'config', name })) : []),
]);

const rowKey = (row: Row) => ('name' in row ? `${row.kind}-${row.name}` : row.kind);

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
    <template v-for="row in rows" :key="rowKey(row)">
      <!-- ADVANCED SETTINGS, the ones most people never change (`"advanced": true` in the
           manifest), behind one toggle after everything else. It opens by itself when one of them
           needs fixing, so a problem is never hidden. -->
      <div v-if="row.kind === 'advanced'">
        <q-btn
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
      </div>

      <div
        v-else-if="row.kind === 'config'"
        class="plugin-setting"
        :data-setting="row.name"
        :data-advanced="shape.config[row.name]!.advanced ? '' : undefined"
      >
        <div v-if="shape.config[row.name]!.type === 'bool'" class="row items-center no-wrap q-gutter-x-xs">
          <q-toggle
            :model-value="config[row.name] === true"
            dense
            :label="row.name"
            @update:model-value="(value: boolean) => set(row.name, value)"
          />
          <FieldHelp :text="helpFor(shape.config[row.name]!)" />
        </div>

        <ChipListInput
          v-else-if="shape.config[row.name]!.type === 'list'"
          :model-value="listOf(row.name)"
          :label="row.name"
          :hint="helpFor(shape.config[row.name]!)"
          :options="shape.config[row.name]!.enum && shape.config[row.name]!.enum!.length > 0 ? shape.config[row.name]!.enum : null"
          @update:model-value="(value: string[]) => set(row.name, value)"
        />

        <q-select
          v-else-if="shape.config[row.name]!.enum && shape.config[row.name]!.enum!.length > 0"
          :model-value="text(row.name)"
          :options="shape.config[row.name]!.enum!"
          outlined
          dense
          :label="row.name"
          :error="missing.includes(row.name) ? true : undefined"
          @update:model-value="(value: string) => set(row.name, value)"
        >
          <template #append><FieldHelp :text="helpFor(shape.config[row.name]!)" /></template>
        </q-select>

        <q-input
          v-else
          :model-value="text(row.name)"
          outlined
          dense
          :type="shape.config[row.name]!.type === 'number' ? 'number' : 'text'"
          :label="row.name"
          :error="missing.includes(row.name) || bounds[row.name] ? true : undefined"
          :error-message="bounds[row.name] ?? ''"
          :data-out-of-range="bounds[row.name] ? '' : undefined"
          @update:model-value="(value) => set(row.name, value === null ? '' : String(value))"
        >
          <template #append><FieldHelp :text="helpFor(shape.config[row.name]!)" /></template>
        </q-input>

        <div class="plugin-setting-marks text-caption os-text-muted row items-center q-gutter-x-sm">
          <span v-if="defaultLabel(shape.config[row.name]!)" data-default>{{ defaultLabel(shape.config[row.name]!) }}</span>
          <span v-if="shape.config[row.name]!.required" class="text-weight-medium" data-required>Required</span>
          <span v-if="setByPerson(shape.config[row.name]!)" class="text-weight-medium" data-person-only>Set by a person only</span>
          <q-space />
          <q-btn
            flat
            dense
            no-caps
            size="sm"
            label="Reset to default"
            :aria-label="`Reset ${row.name} to default`"
            :disable="isDefault(shape.config[row.name]!, config[row.name])"
            @click="reset(row.name)"
          />
        </div>
      </div>

      <!-- A SECRET: the key name it is bound to, then (once the Host has said what it holds) the
           key's write-only value field, then one line saying where the key stands, as every
           setting has its line - with Clear for a key set from the screen, where Reset is. -->
      <div v-else-if="row.kind === 'secret'" class="plugin-setting" :data-secret-setting="row.name">
        <q-input
          :model-value="secrets[row.name] ?? ''"
          outlined
          dense
          :label="`Secret ${row.name}: key name`"
          :error="missing.includes(row.name) ? true : undefined"
          autocomplete="off"
          spellcheck="false"
          @update:model-value="(value) => (secrets = { ...secrets, [row.name]: value === null ? '' : String(value) })"
        >
          <template #append>
            <FieldHelp :text="secretHint(shape.secrets[row.name]!.description, shape.secrets[row.name]!.required)" />
          </template>
        </q-input>
        <template v-if="keyState(row.name)">
          <SecretKeyField
            class="q-mt-sm"
            :state="keyState(row.name)!"
            :help="valueHelp(keyState(row.name)!.key)"
            :clearing="isClear(pending[keyState(row.name)!.key])"
            :model-value="typedValue(keyState(row.name)!.key)"
            @update:model-value="(value: string) => typeValue(keyState(row.name)!.key, value)"
          />
          <div class="plugin-setting-marks text-caption row items-center no-wrap q-gutter-x-sm">
            <span
              :class="keyState(row.name)!.set || pending[keyState(row.name)!.key] ? 'os-text-muted' : 'text-warning'"
              :data-secret-key-state="keyState(row.name)!.set ? 'set' : 'unset'"
              :data-secret-field="row.name"
            >{{ keySaying(keyState(row.name)!, shape.secrets[row.name]!.required) }}</span>
            <q-space />
            <q-btn
              v-if="keyState(row.name)!.source === 'stored'"
              flat
              dense
              no-caps
              size="sm"
              :label="isClear(pending[keyState(row.name)!.key]) ? 'Keep' : 'Clear'"
              :aria-label="`${isClear(pending[keyState(row.name)!.key]) ? 'Keep' : 'Clear'} ${keyState(row.name)!.key}`"
              data-secret-value-clear
              @click="toggleClear(keyState(row.name)!.key)"
            />
          </div>
        </template>
      </div>

      <!-- CONNECTION SLOTS: one picker each, listing connections of an allowed provider. The member
           stores the connection's id, never a token. -->
      <div v-else-if="row.kind === 'slots'" class="plugin-settings-slots">
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
