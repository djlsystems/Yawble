<script setup lang="ts">
import { computed } from 'vue';
import type { PluginNamedSecrets, PluginNamedSecretState } from '../api/types';
import {
  namedSecretNameRefusal,
  namedSecretSetWords,
  namedSecretValueMissing,
  namedSecretsCount,
  namedSecretsMax,
  type NamedSecretRow,
  type NamedSecretsDraft,
} from '../lib/namedSecrets';
import FieldHelp from './FieldHelp.vue';

/**
 * A PLUGIN MEMBER'S NAMED SECRETS, AS ONE TABLE: the manifest's `namedSecrets` block, on every plugin
 * settings screen - Add member, Member settings, the solution wizard's secrets step and the panel's
 * settings - so they cannot drift. Name in the first column, Value in the second as a password box
 * that starts empty and says when a value is saved and by whom; Add for a new row; Remove per saved
 * row, held until the dialog's button with Keep to take it back, as Clear is for a secret field. The
 * block's label and description head it; how a name is used sits behind `FieldHelp`.
 *
 * A SAVED VALUE IS NEVER READ BACK OR SHOWN: the Host answers each name with who saved it and when,
 * and the box for it only ever holds what is typed now, which replaces it on the button. Everything
 * waits in the draft; the dialog sends `namedSecretValuesBody` as `namedSecretValues`.
 */
const props = defineProps<{
  block: PluginNamedSecrets;
  /** The names the member holds now. Empty at hire. */
  states?: PluginNamedSecretState[] | undefined;
  /** The plugin's own secret field names, which a named secret may not take. */
  fields?: string[] | undefined;
  /** The dialog's button that saves the form, named in what a typed value says. */
  saveLabel?: string | undefined;
}>();

const draft = defineModel<NamedSecretsDraft>({ required: true });

const saved = computed(() => props.states ?? []);
const button = computed(() => props.saveLabel ?? 'Save');
const max = computed(() => namedSecretsMax(props.block));
const count = computed(() => namedSecretsCount(saved.value, draft.value));
const full = computed(() => count.value >= max.value);

const help =
  'The name is how the team refers to the secret: tell it the name, never the value. A value is kept for this member alone and is never shown again; type a new one to replace it.';

let nextKey = 0;

function add() {
  if (full.value) return;
  nextKey = Math.max(nextKey, ...draft.value.added.map((row) => row.key)) + 1;
  draft.value = { ...draft.value, added: [...draft.value.added, { key: nextKey, name: '', value: '' }] };
}

function editRow(key: number, change: Partial<Pick<NamedSecretRow, 'name' | 'value'>>) {
  draft.value = { ...draft.value, added: draft.value.added.map((row) => (row.key === key ? { ...row, ...change } : row)) };
}

/** An added row is not saved yet: Remove drops it at once. */
function dropRow(key: number) {
  draft.value = { ...draft.value, added: draft.value.added.filter((row) => row.key !== key) };
}

/** Typing a saved name's value replaces it on the button; emptying the box again changes nothing. */
function replace(name: string, typed: string) {
  const { [name]: _, ...rest } = draft.value.replaced;
  draft.value = { ...draft.value, replaced: typed === '' ? rest : { ...rest, [name]: typed } };
}

/** Remove holds a saved name's removal for the button; Keep takes it back. */
function toggleRemove(name: string) {
  const removing = draft.value.removed.includes(name);
  draft.value = {
    ...draft.value,
    removed: removing ? draft.value.removed.filter((entry) => entry !== name) : [...draft.value.removed, name],
  };
}

const removing = (name: string) => draft.value.removed.includes(name);

/** The one line under a saved name's value: what waits for the button, else who saved it and when. */
function savedSaying(state: PluginNamedSecretState): string {
  if (removing(state.name)) return `Removed when you press ${button.value}.`;
  if ((draft.value.replaced[state.name] ?? '') !== '') return `Replaced when you press ${button.value}.`;
  return namedSecretSetWords(state);
}

const refusal = (row: NamedSecretRow) => namedSecretNameRefusal(row, saved.value, draft.value, props.fields ?? []);

function addedSaying(row: NamedSecretRow): string {
  if (namedSecretValueMissing(row)) return 'Type its value.';
  if (row.value !== '') return `Saved when you press ${button.value}.`;
  return 'Not set.';
}

const overMax = computed(() => (count.value > max.value ? `This member may hold at most ${max.value} named secrets.` : null));
</script>

<template>
  <div class="named-secrets" data-named-secrets>
    <div class="row items-center no-wrap q-gutter-x-xs">
      <div class="text-weight-medium" data-named-secrets-label>{{ block.label }}</div>
      <FieldHelp :text="help" />
    </div>
    <div v-if="block.description" class="text-caption os-text-muted q-mb-xs" data-named-secrets-description>{{ block.description }}</div>

    <table class="named-secrets-table">
      <thead>
        <tr>
          <th scope="col" class="text-left">Name</th>
          <th scope="col" class="text-left">Value</th>
          <th scope="col"><span class="sr-only">Remove</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="saved.length === 0 && draft.added.length === 0" data-named-secrets-empty>
          <td colspan="3" class="os-text-muted text-caption">No named secrets yet. Add one to give it a name and a value.</td>
        </tr>

        <!-- A SAVED NAME: the name as text, and a password box that starts empty - the value is
             never read back. Remove is held until the button; Keep takes it back. -->
        <tr
          v-for="state in saved"
          :key="`saved-${state.name}`"
          :data-named-secret="state.name"
          :data-named-secret-removing="removing(state.name) ? '' : undefined"
        >
          <td class="named-secret-name mono" :class="{ 'named-secret-removing': removing(state.name) }">{{ state.name }}</td>
          <td>
            <q-input
              :model-value="draft.replaced[state.name] ?? ''"
              outlined
              dense
              type="password"
              :aria-label="`Value of ${state.name}`"
              placeholder="Saved, never shown: type a new value to replace it"
              :disable="removing(state.name)"
              autocomplete="new-password"
              spellcheck="false"
              data-named-secret-value
              @update:model-value="(value) => replace(state.name, value === null ? '' : String(value))"
            />
            <div class="text-caption os-text-muted" data-named-secret-state>{{ savedSaying(state) }}</div>
          </td>
          <td class="named-secret-action">
            <q-btn
              flat
              dense
              no-caps
              size="sm"
              :label="removing(state.name) ? 'Keep' : 'Remove'"
              :aria-label="`${removing(state.name) ? 'Keep' : 'Remove'} ${state.name}`"
              data-named-secret-remove
              @click="toggleRemove(state.name)"
            />
          </td>
        </tr>

        <!-- AN ADDED ROW: a box for the name and a password box for its value, sent on the button.
             Remove drops it at once: nothing of it is saved yet. -->
        <tr v-for="(row, index) in draft.added" :key="`added-${row.key}`" data-named-secret-added>
          <td class="named-secret-name">
            <q-input
              :model-value="row.name"
              outlined
              dense
              :aria-label="`Name of new secret ${index + 1}`"
              placeholder="Name"
              autocomplete="off"
              spellcheck="false"
              :error="refusal(row) !== null ? true : undefined"
              :error-message="refusal(row) ?? ''"
              data-named-secret-name-input
              @update:model-value="(value) => editRow(row.key, { name: value === null ? '' : String(value) })"
            />
          </td>
          <td>
            <q-input
              :model-value="row.value"
              outlined
              dense
              type="password"
              :aria-label="`Value of new secret ${index + 1}`"
              placeholder="Value"
              autocomplete="new-password"
              spellcheck="false"
              data-named-secret-value
              @update:model-value="(value) => editRow(row.key, { value: value === null ? '' : String(value) })"
            />
            <div
              class="text-caption"
              :class="namedSecretValueMissing(row) ? 'text-warning' : 'os-text-muted'"
              data-named-secret-state
            >{{ addedSaying(row) }}</div>
          </td>
          <td class="named-secret-action">
            <q-btn
              flat
              dense
              no-caps
              size="sm"
              label="Remove"
              :aria-label="`Remove new secret ${index + 1}`"
              data-named-secret-remove
              @click="dropRow(row.key)"
            />
          </td>
        </tr>
      </tbody>
    </table>

    <div class="row items-center q-gutter-x-sm q-mt-xs">
      <q-btn
        flat
        dense
        no-caps
        size="sm"
        icon="add"
        label="Add"
        :aria-label="`Add to ${block.label}`"
        :disable="full"
        data-named-secret-add
        @click="add"
      />
      <span class="text-caption os-text-muted" data-named-secrets-count>{{ count }} of {{ max }}</span>
      <span v-if="overMax" class="text-caption text-negative" data-named-secrets-over>{{ overMax }}</span>
    </div>
  </div>
</template>

<style scoped>
.named-secrets-table {
  width: 100%;
  border-collapse: collapse;
}

.named-secrets-table th {
  font-weight: 500;
  font-size: 0.75rem;
  padding: 2px 4px;
  color: var(--os-ink-faint);
}

.named-secrets-table td {
  padding: 2px 4px;
  vertical-align: top;
}

.named-secret-name {
  width: 35%;
  padding-top: 10px;
  word-break: break-all;
}

.named-secret-name:has(.q-field) {
  padding-top: 2px;
}

.named-secret-removing {
  text-decoration: line-through;
}

.named-secret-action {
  width: 1%;
  white-space: nowrap;
  padding-top: 6px;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
