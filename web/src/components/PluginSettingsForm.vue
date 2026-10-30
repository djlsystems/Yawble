<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { listConnectionProviders, listConnections } from '../api/client';
import type { Connection, ConnectionProvider, PluginNamedSecretState, PluginSecretState, PluginTableRow } from '../api/types';
import { tableRows, type TableSecretStates, type TableSecretsDraft } from '../lib/pluginTable';
import { emptyNamedSecretsDraft, type NamedSecretsDraft } from '../lib/namedSecrets';
import {
  defaultValue,
  fieldBoundsHint,
  fieldLabel,
  fieldWords,
  hasSlots,
  isDefault,
  isUnmasked,
  listValue,
  missingRequired,
  outOfRange,
  placeholderOf,
  secretSetWords,
  secretsSet,
  settingsBody,
  type PluginFieldValues,
  type PluginSettingsShape,
  type SecretValues,
} from '../lib/pluginSettings';
import ChipListInput from './ChipListInput.vue';
import ConnectionPicker from './ConnectionPicker.vue';
import NamedSecretsTable from './NamedSecretsTable.vue';
import PluginTableField from './PluginTableField.vue';

/**
 * A PLUGIN MEMBER'S SETTINGS, GENERATED FROM ITS MANIFEST. The one editor Add member (a hire),
 * Member settings (an edit after hire) and a solution's Change setup show, so they cannot drift on
 * what a field looks like or what is saved.
 *
 * NO FREE-FORM JSON. One input per `config` field, typed - text for a string, a number box for a
 * number, a toggle for a bool, a dropdown for a field with `enum`, chips for a list (`ChipListInput`:
 * chosen from the `enum` when there is one, else added through its dialog; × removes) - so the shape
 * saved is always one the plugin accepts. A field with `options` (a manifest's own list, or a named
 * set such as every language Chrome accepts or every IANA time zone) shows each choice's words and
 * stores its value; its dropdown filters as the person types.
 *
 * REQUIRED SETTINGS UP FRONT, THE REST UNDER ADVANCED. Settings show in the manifest's order; one the
 * manifest marks `advanced` - and does not require - sits under the Advanced toggle at the end, closed
 * until opened, and opened by itself when one of them is out of range. Each is labelled by the manifest's `label`, else its
 * name in words (`apiKey` -> "API key"); a text or number box's default is its placeholder, and
 * Reset to default puts it back. No info icon and no Default line: the plugin's Documentation says
 * what its settings do. What a person must see now - an error, Required, a value not set, the saved
 * line - stays printed. A number field with manifest bounds shows a value outside them as out of
 * range - typed, or already stored - and the dialogs hold Save until it is fixed, as they do for a
 * missing required field (`outOfRange`).
 *
 * A SECRET IS ONE BOX FOR ITS VALUE, the member's own: no other member can read or change it. A
 * masked field (the default) is a password box that starts empty and says when a value is saved and
 * by whom; typing replaces it and Clear removes it. A field the manifest declares `masked: false` is
 * a text box showing the saved value. What is typed waits in `values` for the dialog's own button,
 * which sends it as `secretValues` with the hire or the Save. A key name the member is bound to (the
 * old binding, still honoured by the Host) is never on the screen: only "As JSON" shows it. Connection
 * slots are one picker each (`ConnectionPicker`), storing a connection's id, never a token.
 *
 * A PLUGIN THAT DECLARES A `namedSecrets` BLOCK gets one table after its secret fields
 * (`NamedSecretsTable`): names a person chooses, each value typed masked beside it, waiting in
 * `named` for the dialog's button, which sends them as `namedSecretValues`. A plugin without the
 * block shows nothing of it.
 *
 * A `table` SETTING is its own editor (`PluginTableField`): rows added, edited, duplicated and
 * deleted, bool columns toggled inline, each secret cell a password box whose value waits in
 * `tableSecrets` for the dialog's button, sent as `tableSecretValues`. Only Member settings edits one
 * (`tableStates` given); at hire it says the rows are added after hire.
 *
 * The "as JSON" view is read-only, and it is exactly `settingsBody` - the settings and key bindings
 * stored - for checking, never for editing. It never shows a value.
 */
const props = defineProps<{
  shape: PluginSettingsShape;
  /** The plugin's id: with it, each slot offers Connect where nothing suitable is bound. */
  plugin?: string | undefined;
  /** Binds a slot of the member being edited at the Host, throwing its refusal. Absent at hire. */
  bindSlot?: ((slot: string, connectionId: string) => Promise<void>) | undefined;
  /** Each secret field's state on the member being edited. Absent at hire: nothing is set yet. */
  states?: PluginSecretState[] | undefined;
  /** The named secrets the member being edited holds. Absent at hire: it holds none. */
  namedStates?: PluginNamedSecretState[] | undefined;
  /** The dialog's button that saves the form, named in what a typed value says. */
  saveLabel?: string | undefined;
  /** Each table's secret cells saved on the member being edited. Absent at hire: a table is not edited then. */
  tableStates?: TableSecretStates | undefined;
  /** Each table's rows as saved, by key. */
  savedTableRows?: Record<string, string[]> | undefined;
  /** The member being edited, for a table row action's link. */
  memberContext?: { team: string; member: string } | undefined;
}>();

const config = defineModel<PluginFieldValues>('config', { required: true });
/** Each secret's key binding as stored: shown only in "As JSON", never edited here. */
const secrets = defineModel<Record<string, string>>('secrets', { required: true });
/** The secret values typed or cleared, waiting for the dialog's button. */
const values = defineModel<SecretValues>('values', { default: () => ({}) });
/** Named secrets added, replaced or removed, waiting for the dialog's button. */
const named = defineModel<NamedSecretsDraft>('named', { default: () => emptyNamedSecretsDraft() });
/** Each table's secret cells typed or cleared, waiting for the dialog's button. */
const tableSecrets = defineModel<TableSecretsDraft>('tableSecrets', { default: () => ({}) });
/** Slot -> the chosen connection's id. Only a plugin that declares slots has any. */
const connections = defineModel<Record<string, string>>('connections', { default: () => ({}) });

const missing = computed(() =>
  missingRequired(props.shape, config.value, secretsSet(props.shape, props.states, values.value)),
);
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

/** A secret field's state on the member, or null at hire and for a field the Host did not answer. */
function stateOf(name: string): PluginSecretState | null {
  return props.states?.find((entry) => entry.field === name) ?? null;
}

const button = computed(() => props.saveLabel ?? 'Save');

/** What the box shows: what was typed; else, for an unmasked field, the saved value; else nothing. */
function boxValue(name: string): string {
  const typed = values.value[name];
  if (typeof typed === 'string') return typed;
  if (typed === null) return '';
  return isUnmasked(props.shape.secrets[name]) ? stateOf(name)?.value ?? '' : '';
}

/**
 * Typing into a secret's box. A masked box emptied again changes nothing. An unmasked box set back
 * to its saved value changes nothing; one emptied clears the saved value, as Clear does.
 */
function typeSecret(name: string, typed: string) {
  const { [name]: _, ...rest } = values.value;
  const saved = stateOf(name);

  if (isUnmasked(props.shape.secrets[name]) && saved?.source === 'member') {
    if (typed === (saved.value ?? '')) values.value = rest;
    else values.value = { ...rest, [name]: typed === '' ? null : typed };
    return;
  }

  values.value = typed === '' ? rest : { ...rest, [name]: typed };
}

/** Clear asks for the member's own value to be removed on the button; Keep takes that back. */
function toggleClear(name: string) {
  const { [name]: entry, ...rest } = values.value;
  values.value = entry === null ? rest : { ...rest, [name]: null };
}

/** Whether the member has its own value for the field now: what Clear removes. */
const clearable = (name: string) => stateOf(name)?.source === 'member';

/** The one line under a secret: what waits for the button, else where the value stands now. */
function secretSaying(name: string): string {
  const typed = values.value[name];
  if (typed === null) return `Cleared when you press ${button.value}.`;
  if (typeof typed === 'string' && typed !== '') return `Saved when you press ${button.value}.`;

  const state = stateOf(name);
  if (state && !state.needed) return 'Not needed with these settings.';
  if (state?.set) return secretSetWords(state);
  return 'Not set.';
}

/** Whether the line under a secret is a warning: nothing set and nothing typed. */
function secretUnset(name: string): boolean {
  const typed = values.value[name];
  if (typeof typed === 'string' && typed !== '') return false;
  const state = stateOf(name);
  if (state && !state.needed) return false;
  return typed === null || !state?.set;
}

/**
 * THE FORM'S ROWS, IN THE MANIFEST'S ORDER: the settings, then the secrets, then the connection
 * slots; then, when the manifest marks any setting `advanced` (and does not require it), the
 * Advanced toggle and those settings. A required setting is never behind the toggle.
 */
type Row =
  | { kind: 'config'; name: string; advanced?: boolean }
  | { kind: 'secret'; name: string }
  | { kind: 'named' }
  | { kind: 'slots' }
  | { kind: 'advanced' };

const behindAdvanced = (name: string) => {
  const field = props.shape.config[name];
  return field?.advanced === true && !field.required;
};

const rows = computed<Row[]>(() => {
  const names = Object.keys(props.shape.config);
  const advanced = names.filter(behindAdvanced);
  return [
    ...names.filter((name) => !behindAdvanced(name)).map((name): Row => ({ kind: 'config', name })),
    ...Object.keys(props.shape.secrets).map((name): Row => ({ kind: 'secret', name })),
    ...(props.shape.namedSecrets ? [{ kind: 'named' } as Row] : []),
    ...(hasSlots(props.shape) ? [{ kind: 'slots' } as Row] : []),
    ...(advanced.length > 0 ? [{ kind: 'advanced' } as Row] : []),
    ...advanced.map((name): Row => ({ kind: 'config', name, advanced: true })),
  ];
});

/** Whether the Advanced settings show: opened by the person, or by one of them being out of range. */
const advancedOpen = ref(false);
const advancedNeedsYou = computed(() => Object.keys(bounds.value).some(behindAdvanced));
const showAdvanced = computed(() => advancedOpen.value || advancedNeedsYou.value);

/** A field's choices as the dropdown lists them: its `options`, else its `enum` as they are. */
function choicesOf(name: string): { label: string; value: string }[] {
  const field = props.shape.config[name]!;
  if (field.options && field.options.length > 0) return field.options;
  return (field.enum ?? []).map((value) => ({ label: value, value }));
}

const hasChoices = (name: string) => choicesOf(name).length > 0;

/** What the one-choice dropdown lists now: every choice, or those whose words or value hold what is typed. */
const filter = ref<Record<string, string>>({});

function shownChoices(name: string) {
  const typed = (filter.value[name] ?? '').trim().toLowerCase();
  const all = choicesOf(name);
  if (!typed) return all;
  return all.filter((option) => option.label.toLowerCase().includes(typed) || option.value.toLowerCase().includes(typed));
}

function filterChoices(name: string, typed: string, update: (fn: () => void) => void) {
  update(() => {
    filter.value = { ...filter.value, [name]: typed };
  });
}

/** What a setting or secret is called on the form: the manifest's label, else its name in words. */
function labelOf(name: string): string {
  const declared = props.shape.config[name] ?? props.shape.secrets[name];
  return declared ? fieldLabel(name, declared) : fieldWords(name);
}

const rowKey = (row: Row) => ('name' in row ? `${row.kind}-${row.name}` : row.kind);

/** Whether a row shows now: a setting under Advanced only while Advanced is open. */
const visible = (row: Row) => row.kind !== 'config' || !row.advanced || showAdvanced.value;

const showJson = ref(false);
const json = computed(() => JSON.stringify(body.value, null, 2));

function set(name: string, value: string | boolean | string[] | PluginTableRow[]) {
  config.value = { ...config.value, [name]: value };
}

function reset(name: string) {
  const field = props.shape.config[name];
  if (!field) return;

  set(name, defaultValue(field));
}

function listOf(name: string): string[] {
  return listValue(config.value[name]);
}

function text(name: string): string {
  const value = config.value[name];
  return typeof value === 'string' ? value : '';
}
</script>

<template>
  <div class="plugin-settings q-gutter-sm" data-plugin-settings>
    <template v-for="row in rows" :key="rowKey(row)">
      <!-- A SETTING: its label, its value (the default as an empty box's placeholder), and one line
           with what a person must see now - Required, a number's bounds - and Reset to default. No info icon and no
           Default line: the plugin's documentation explains its settings. -->
      <!-- THE ADVANCED TOGGLE: the settings the manifest marks advanced follow it, shown while open. -->
      <q-btn
        v-if="row.kind === 'advanced'"
        flat
        dense
        no-caps
        align="left"
        class="plugin-settings-advanced"
        :icon="showAdvanced ? 'keyboard_arrow_up' : 'keyboard_arrow_down'"
        label="Advanced"
        :aria-expanded="showAdvanced ? 'true' : 'false'"
        :disable="advancedNeedsYou"
        data-advanced-toggle
        @click="advancedOpen = !advancedOpen"
      />

      <div
        v-else-if="row.kind === 'config' && visible(row)"
        class="plugin-setting"
        :data-setting="row.name"
        :data-advanced="row.advanced ? '' : undefined"
      >
        <PluginTableField
          v-if="shape.config[row.name]!.type === 'table' && shape.config[row.name]!.table"
          v-model:secrets="tableSecrets"
          :model-value="tableRows(config[row.name])"
          :name="row.name"
          :field="shape.config[row.name]!"
          :label="labelOf(row.name)"
          :states="tableStates"
          :saved-rows="savedTableRows?.[row.name]"
          :context="memberContext"
          :editable="tableStates !== undefined"
          :save-label="button"
          @update:model-value="(value: PluginTableRow[]) => set(row.name, value)"
        />

        <div v-else-if="shape.config[row.name]!.type === 'bool'" class="row items-center no-wrap q-gutter-x-xs">
          <q-toggle
            :model-value="config[row.name] === true"
            dense
            :label="labelOf(row.name)"
            @update:model-value="(value: boolean) => set(row.name, value)"
          />
        </div>

        <ChipListInput
          v-else-if="shape.config[row.name]!.type === 'list'"
          :model-value="listOf(row.name)"
          :label="labelOf(row.name)"
          :options="hasChoices(row.name) ? choicesOf(row.name) : null"
          :error="bounds[row.name] ? true : undefined"
          :error-message="bounds[row.name] ?? undefined"
          @update:model-value="(value: string[]) => set(row.name, value)"
        />

        <q-select
          v-else-if="hasChoices(row.name)"
          :model-value="text(row.name)"
          :options="shownChoices(row.name)"
          emit-value
          map-options
          use-input
          fill-input
          hide-selected
          input-debounce="0"
          outlined
          dense
          :label="labelOf(row.name)"
          :error="missing.includes(row.name) || bounds[row.name] ? true : undefined"
          :error-message="bounds[row.name] ?? ''"
          :data-out-of-range="bounds[row.name] ? '' : undefined"
          data-choice-select
          @filter="(typed: string, update: (fn: () => void) => void) => filterChoices(row.name, typed, update)"
          @update:model-value="(value: string) => set(row.name, value)"
        />

        <q-input
          v-else
          :model-value="text(row.name)"
          outlined
          dense
          :type="shape.config[row.name]!.type === 'number' ? 'number' : 'text'"
          :label="labelOf(row.name)"
          :placeholder="placeholderOf(shape.config[row.name]!)"
          stack-label
          :error="missing.includes(row.name) || bounds[row.name] ? true : undefined"
          :error-message="bounds[row.name] ?? ''"
          :data-out-of-range="bounds[row.name] ? '' : undefined"
          @update:model-value="(value) => set(row.name, value === null ? '' : String(value))"
        />

        <div v-if="shape.config[row.name]!.type !== 'table'" class="plugin-setting-marks text-caption os-text-muted row items-center q-gutter-x-sm">
          <span v-if="shape.config[row.name]!.required" class="text-weight-medium" data-required>Required</span>
          <span v-if="fieldBoundsHint(shape.config[row.name]!)" data-bounds>{{ fieldBoundsHint(shape.config[row.name]!) }}</span>
          <q-space />
          <q-btn
            flat
            dense
            no-caps
            size="sm"
            label="Reset to default"
            :aria-label="`Reset ${labelOf(row.name)} to default`"
            :disable="isDefault(shape.config[row.name]!, config[row.name])"
            @click="reset(row.name)"
          />
        </div>
      </div>

      <!-- A SECRET: one box for the member's own value - a password box that starts empty for a
           masked field, a text box showing the saved value for an unmasked one - then one line
           saying where it stands, with Clear where Reset sits on a setting. No key name. -->
      <div
        v-else-if="row.kind === 'secret'"
        class="plugin-setting"
        :data-secret-setting="row.name"
        :data-masked="isUnmasked(shape.secrets[row.name]) ? 'false' : 'true'"
      >
        <q-input
          :model-value="boxValue(row.name)"
          outlined
          dense
          :type="isUnmasked(shape.secrets[row.name]) ? 'text' : 'password'"
          :label="labelOf(row.name)"
          :placeholder="!isUnmasked(shape.secrets[row.name]) && stateOf(row.name)?.set ? 'Saved, never shown: type a new value to replace it' : undefined"
          :stack-label="!isUnmasked(shape.secrets[row.name]) && stateOf(row.name)?.set ? true : undefined"
          :error="missing.includes(row.name) ? true : undefined"
          :disable="values[row.name] === null"
          :autocomplete="isUnmasked(shape.secrets[row.name]) ? 'off' : 'new-password'"
          spellcheck="false"
          data-secret-value-input
          @update:model-value="(value) => typeSecret(row.name, value === null ? '' : String(value))"
        />
        <div class="plugin-setting-marks text-caption row items-center no-wrap q-gutter-x-sm">
          <span
            :class="secretUnset(row.name) ? 'text-warning' : 'os-text-muted'"
            :data-secret-state="stateOf(row.name)?.set ? 'set' : 'unset'"
            :data-secret-field="row.name"
          >{{ secretSaying(row.name) }}</span>
          <span v-if="shape.secrets[row.name]!.required" class="text-weight-medium" data-required>Required</span>
          <q-space />
          <q-btn
            v-if="clearable(row.name)"
            flat
            dense
            no-caps
            size="sm"
            :label="values[row.name] === null ? 'Keep' : 'Clear'"
            :aria-label="`${values[row.name] === null ? 'Keep' : 'Clear'} ${labelOf(row.name)}`"
            data-secret-value-clear
            @click="toggleClear(row.name)"
          />
        </div>
      </div>

      <!-- NAMED SECRETS: one table, the plugin's `namedSecrets` block, after its secret fields. -->
      <NamedSecretsTable
        v-else-if="row.kind === 'named' && shape.namedSecrets"
        v-model="named"
        :block="shape.namedSecrets"
        :states="namedStates"
        :fields="Object.keys(shape.secrets)"
        :save-label="button"
      />

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
      <!-- READ-ONLY, and exactly what is stored: only fields that differ from their default, and
           any key binding by name - the one place a key name shows. Never a value. -->
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
