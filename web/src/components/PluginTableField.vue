<script setup lang="ts">
import { computed, ref } from 'vue';
import type { PluginConfigField, PluginTableColumn, PluginTableRow } from '../api/types';
import { localTime, wireInstant } from '../lib/localTime';
import FieldHelp from './FieldHelp.vue';
import { boundsHint } from '../lib/numberBounds';
import {
  cellState,
  cellText,
  columnLabel,
  duplicateRow,
  emptyRow,
  rowActionHref,
  rowActionsFor,
  rowName,
  rowProblems,
  tableProblem,
  typedSecretColumns,
  withRowSecrets,
  writableColumns,
  type TableSecretStates,
  type TableSecretsDraft,
} from '../lib/pluginTable';

/**
 * A PLUGIN'S `table` SETTING, EDITABLE: one row per entry, in the order saved. Each row has Edit,
 * Duplicate and Delete; a bool column a person writes is a toggle right in the table; a read-only
 * column (the plugin's status) is shown, never edited; a secret column says only whether a value is
 * saved. Add and Edit open the row editor, which holds a row to the Host's rules (`rowProblems`)
 * before it goes into the table. Nothing is sent until the dialog's own button.
 *
 * A SECRET CELL IS A PASSWORD BOX THAT STARTS EMPTY: typing replaces the saved value, Clear removes
 * it, and a cell left alone keeps it. What is typed waits in `secrets` (`TableSecretsDraft`), sent as
 * `tableSecretValues`. A saved row's name cannot change here - its secrets are stored under it -
 * so Duplicate is how a row gets another name.
 *
 * NARROW, THE TABLE STACKS: below 40rem of its own width each row is a card of label and value, so
 * it reads at 375 px as it does at 1280 px.
 *
 * ROW ACTIONS (`rowActions` in the manifest, such as Sign in on an `interactive` row) show on a saved
 * row as a link once a page registers the action in `rowActionLinks` (`lib/pluginTable`).
 *
 * AT HIRE (`editable` false) the table is not offered: its secrets need the member to exist, so a
 * person adds the rows in Member settings after hire.
 */
const props = defineProps<{
  name: string;
  field: PluginConfigField;
  label: string;
  /** Secret cells saved on the member being edited. Absent at hire. */
  states?: TableSecretStates | undefined;
  /** The rows as saved, by key: a saved row keeps its name and shows its row actions. */
  savedRows?: string[] | undefined;
  /** The member, for a row action's link. Absent at hire. */
  context?: { team: string; member: string } | undefined;
  editable?: boolean | undefined;
  saveLabel?: string | undefined;
}>();

const rows = defineModel<PluginTableRow[]>({ required: true });
const secrets = defineModel<TableSecretsDraft>('secrets', { default: () => ({}) });

const shape = computed(() => props.field.table!);
const button = computed(() => props.saveLabel ?? 'Save');
const shown = computed(() => shape.value.columns);
const full = computed(() => rows.value.length >= shape.value.maxRows);
const problem = computed(() => tableProblem(shape.value, rows.value));

const isSaved = (row: PluginTableRow) =>
  (props.savedRows ?? []).some((name) => name.toLowerCase() === rowName(shape.value, row).toLowerCase());

/** A secret cell in the table: waiting for the button, saved, or not set. Never a value. */
function secretCell(row: PluginTableRow, column: PluginTableColumn): string {
  const name = rowName(shape.value, row);
  const typed = secrets.value[props.name]?.[name]?.[column.name];
  if (typed === null) return `Cleared on ${button.value}`;
  if (typeof typed === 'string' && typed !== '') return `Saved on ${button.value}`;
  if (!isSaved(row)) return 'Not set';
  const state = cellState(props.states, props.name, name, column.name);
  if (!state) return 'Not set';
  return column.masked === false && state.value !== undefined ? state.value : 'Saved';
}

function toggle(index: number, column: string, value: boolean) {
  rows.value = rows.value.map((row, at) => (at === index ? { ...row, [column]: value } : row));
}

// ---- The row editor -------------------------------------------------------------------------

const editing = ref(false);
/** The row's index in the table, or -1 for a new row. */
const editIndex = ref(-1);
const draft = ref<PluginTableRow>({});
/** Secret cells typed or cleared in the editor: a string replaces, null clears. */
const draftSecrets = ref<Record<string, string | null>>({});
/** The row's name when the editor opened: its secrets in the draft are kept under it. */
const openedAs = ref('');

/** The editor's own button: what its secret lines name, so the words and the button never drift apart. */
const applyLabel = computed(() => (editIndex.value >= 0 ? 'Done' : 'Add'));
const draftSaved = computed(() => editIndex.value >= 0 && isSaved(rows.value[editIndex.value]!));
const draftProblems = computed(() =>
  rowProblems(shape.value, draft.value, rows.value.filter((_, at) => at !== editIndex.value)),
);

function open(index: number, row: PluginTableRow, typed: Record<string, string | null> = {}) {
  editIndex.value = index;
  draft.value = { ...row };
  openedAs.value = index >= 0 ? rowName(shape.value, row) : '';
  draftSecrets.value = { ...typed };
  editing.value = true;
}

function add() {
  if (!full.value) open(-1, emptyRow(shape.value));
}

function edit(index: number) {
  const row = rows.value[index]!;
  open(index, row, secrets.value[props.name]?.[rowName(shape.value, row)] ?? {});
}

function duplicate(index: number) {
  if (!full.value) open(-1, duplicateRow(shape.value, rows.value[index]!, rows.value));
}

/** Delete takes the row out of the table, and any secret typed for it; a saved row's secrets go on the button. */
function remove(index: number) {
  const name = rowName(shape.value, rows.value[index]!);
  rows.value = rows.value.filter((_, at) => at !== index);
  secrets.value = withRowSecrets(secrets.value, props.name, name, {});
}

function apply() {
  if (Object.keys(draftProblems.value).length > 0) return;

  const name = rowName(shape.value, draft.value);
  if (editIndex.value >= 0) {
    // Read-only cells ride along untouched: the Host keeps the stored ones whatever is sent.
    rows.value = rows.value.map((row, at) => (at === editIndex.value ? { ...row, ...draft.value } : row));
  } else {
    rows.value = [...rows.value, { ...draft.value }];
  }

  let next = secrets.value;
  if (openedAs.value && openedAs.value !== name) next = withRowSecrets(next, props.name, openedAs.value, {});
  secrets.value = withRowSecrets(next, props.name, name, draftSecrets.value);
  editing.value = false;
}

function setCell(column: string, value: string | number | boolean | null) {
  draft.value = { ...draft.value, [column]: value };
}

/** The line under an editor box: why a saved row's name is fixed, else the column's description and a number's bounds. */
function hintFor(column: PluginTableColumn): string | undefined {
  if (column.name === shape.value.key && draftSaved.value) return 'A saved row keeps its name: duplicate it to use another.';
  const bounds = column.type === 'number' ? boundsHint(column) : null;
  return [column.description, bounds].filter((part) => part).join(' ') || undefined;
}

/** A masked box emptied again changes nothing. */
function typeSecret(column: string, typed: string) {
  const { [column]: _, ...rest } = draftSecrets.value;
  draftSecrets.value = typed === '' ? rest : { ...rest, [column]: typed };
}

function toggleClear(column: string) {
  const { [column]: entry, ...rest } = draftSecrets.value;
  draftSecrets.value = entry === null ? rest : { ...rest, [column]: null };
}

/** The line under an editor's secret box, naming the editor's own button (Add or Done), not the form's. */
function secretSaying(column: PluginTableColumn): string {
  const typed = draftSecrets.value[column.name];
  if (typed === null) return `Cleared when you press ${applyLabel.value}.`;
  if (typeof typed === 'string' && typed !== '') return `Saved when you press ${applyLabel.value}.`;
  if (!draftSaved.value) return 'Not set.';
  const state = cellState(props.states, props.name, openedAs.value, column.name);
  if (!state) return 'Not set.';
  const at = wireInstant(state.setAt);
  return Number.isNaN(at) ? `Saved by ${state.setBy}.` : `Saved by ${state.setBy} on ${localTime(at, { date: true })}.`;
}

const savedCell = (column: PluginTableColumn) =>
  draftSaved.value && cellState(props.states, props.name, openedAs.value, column.name) !== null;

function href(action: string, row: PluginTableRow): string | null {
  if (!props.context) return null;
  return rowActionHref(action, { ...props.context, table: props.name, row: rowName(shape.value, row), cells: row, shape: shape.value });
}
</script>

<template>
  <div class="plugin-table" :data-table-setting="name">
    <div class="row items-center no-wrap q-gutter-x-sm">
      <div class="text-subtitle2">{{ label }}</div>
      <q-space />
      <span v-if="editable" class="text-caption os-text-muted" data-table-count>{{ rows.length }} / {{ shape.maxRows }}</span>
      <q-btn
        v-if="editable"
        outline
        dense
        no-caps
        size="sm"
        icon="add"
        label="Add row"
        :disable="full"
        data-table-add
        @click="add"
      />
    </div>
    <div v-if="field.description" class="text-caption os-text-muted q-mb-xs">{{ field.description }}</div>

    <div v-if="!editable" class="text-caption os-text-muted" data-table-after-hire>
      Add rows in this member's settings once it is hired.
    </div>

    <template v-else>
      <div v-if="rows.length === 0" class="text-caption os-text-muted q-py-sm" data-table-empty>No rows yet.</div>

      <div v-else class="plugin-table-frame">
        <table class="plugin-table-grid" data-table-grid>
          <thead>
            <tr>
              <th v-for="column in shown" :key="column.name" scope="col">{{ columnLabel(column) }}</th>
              <th scope="col" class="plugin-table-actions"><span class="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, index) in rows" :key="`${index}-${rowName(shape, row)}`" :data-table-row="rowName(shape, row)">
              <td
                v-for="column in shown"
                :key="column.name"
                :data-label="columnLabel(column)"
                :data-column="column.name"
                :class="{ 'plugin-table-key': column.name === shape.key }"
              >
                <q-toggle
                  v-if="column.type === 'bool' && !column.readOnly"
                  dense
                  :model-value="row[column.name] === true"
                  :aria-label="`${columnLabel(column)} for ${rowName(shape, row)}`"
                  data-table-toggle
                  @update:model-value="(value: boolean) => toggle(index, column.name, value)"
                />
                <span v-else-if="column.type === 'secret'" class="os-text-muted" data-table-secret-cell>{{ secretCell(row, column) }}</span>
                <span v-else class="plugin-table-text" :title="cellText(row[column.name] ?? column.default)">{{ cellText(row[column.name] ?? column.default) }}</span>
              </td>
              <td class="plugin-table-actions" data-label="">
                <template v-for="action in rowActionsFor(shape, row, isSaved(row))" :key="action.action">
                  <a v-if="href(action.action, row)" :href="href(action.action, row)!" class="q-mr-sm" :data-row-action="action.action">{{ action.label }}</a>
                </template>
                <q-btn flat dense round size="sm" icon="edit" :aria-label="`Edit ${rowName(shape, row)}`" data-table-edit @click="edit(index)" />
                <q-btn
                  flat
                  dense
                  round
                  size="sm"
                  icon="content_copy"
                  :disable="full"
                  :aria-label="`Duplicate ${rowName(shape, row)}`"
                  data-table-duplicate
                  @click="duplicate(index)"
                />
                <q-btn flat dense round size="sm" icon="delete" :aria-label="`Delete ${rowName(shape, row)}`" data-table-delete @click="remove(index)" />
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-if="problem" class="text-caption text-negative" data-table-problem>{{ problem }}</div>
    </template>

    <!-- THE ROW EDITOR: every column a person writes, typed, and a password box per secret column. -->
    <q-dialog v-model="editing" no-route-dismiss>
      <q-card class="os-dialog-md" data-table-editor>
        <q-card-section class="text-subtitle1">
          {{ editIndex >= 0 ? `Edit ${openedAs}` : `Add a row to ${label}` }}
        </q-card-section>
        <q-card-section class="q-gutter-sm">
          <template v-for="column in writableColumns(shape)" :key="column.name">
            <!-- The help sits beside the switch, not in its label, so a tap on the icon never flips it. -->
            <div v-if="column.type === 'bool'" class="row items-center no-wrap q-gutter-x-xs" :data-editor-bool="column.name">
              <q-toggle
                dense
                :model-value="draft[column.name] === true"
                :label="columnLabel(column)"
                :data-editor-cell="column.name"
                @update:model-value="(value: boolean) => setCell(column.name, value)"
              />
              <FieldHelp :text="column.description" />
            </div>
            <q-select
              v-else-if="column.type === 'enum'"
              outlined
              dense
              :model-value="draft[column.name] ?? null"
              :options="column.enum ?? []"
              :label="columnLabel(column) + (column.required ? ' *' : '')"
              :error="!!draftProblems[column.name]"
              :error-message="draftProblems[column.name] ?? ''"
              :data-editor-cell="column.name"
              @update:model-value="(value: string) => setCell(column.name, value)"
            >
              <template #append><FieldHelp :text="column.description" /></template>
            </q-select>
            <q-input
              v-else
              outlined
              dense
              stack-label
              :model-value="draft[column.name] === undefined || draft[column.name] === null ? '' : String(draft[column.name])"
              :type="column.type === 'number' ? 'number' : 'text'"
              :label="columnLabel(column) + (column.required ? ' *' : '')"
              :placeholder="column.default !== null ? String(column.default) : undefined"
              :disable="column.name === shape.key && draftSaved"
              :error="!!draftProblems[column.name]"
              :error-message="draftProblems[column.name] ?? ''"
              spellcheck="false"
              :data-editor-cell="column.name"
              @update:model-value="(value) => setCell(column.name, value === null ? '' : String(value))"
            >
              <template #append><FieldHelp :text="hintFor(column)" /></template>
            </q-input>
          </template>

          <div v-for="column in typedSecretColumns(shape)" :key="column.name" :data-editor-secret="column.name">
            <q-input
              outlined
              dense
              :model-value="typeof draftSecrets[column.name] === 'string' ? draftSecrets[column.name]! : ''"
              :type="column.masked === false ? 'text' : 'password'"
              :label="columnLabel(column)"
              :placeholder="savedCell(column) ? 'Saved, never shown: type a new value to replace it' : undefined"
              :stack-label="savedCell(column) ? true : undefined"
              :disable="draftSecrets[column.name] === null"
              :autocomplete="column.masked === false ? 'off' : 'new-password'"
              spellcheck="false"
              data-secret-value-input
              @update:model-value="(value) => typeSecret(column.name, value === null ? '' : String(value))"
            />
            <div class="text-caption row items-center no-wrap">
              <span class="os-text-muted">{{ secretSaying(column) }}</span>
              <q-space />
              <q-btn
                v-if="savedCell(column)"
                flat
                dense
                no-caps
                size="sm"
                :label="draftSecrets[column.name] === null ? 'Keep' : 'Clear'"
                :aria-label="`${draftSecrets[column.name] === null ? 'Keep' : 'Clear'} ${columnLabel(column)}`"
                @click="toggleClear(column.name)"
              />
            </div>
          </div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="editing = false" />
          <q-btn
            color="primary"
            no-caps
            :label="applyLabel"
            :disable="Object.keys(draftProblems).length > 0"
            data-table-apply
            @click="apply"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.plugin-table {
  container-type: inline-size;
}

.plugin-table-frame {
  overflow-x: auto;
}

.plugin-table-grid {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.8125rem;
}

.plugin-table-grid th,
.plugin-table-grid td {
  padding: 4px 8px;
  text-align: left;
  vertical-align: middle;
  border-bottom: 1px solid var(--os-rule-strong);
}

.plugin-table-grid th {
  font-weight: 500;
  white-space: nowrap;
}

.plugin-table-text {
  display: inline-block;
  max-width: 14rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: middle;
}

.plugin-table-key {
  font-weight: 500;
}

.plugin-table-actions {
  white-space: nowrap;
  text-align: right;
}

/* WIDE: the row's buttons stay in view however far a wide table scrolls. */
.plugin-table-grid .plugin-table-actions {
  position: sticky;
  right: 0;
  background: var(--os-surface);
  box-shadow: -6px 0 6px -6px rgb(0 0 0 / 25%);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

/* NARROW: each row is a card of label and value. */
@container (max-width: 40rem) {
  .plugin-table-grid thead {
    display: none;
  }

  .plugin-table-grid,
  .plugin-table-grid tbody,
  .plugin-table-grid tr,
  .plugin-table-grid td {
    display: block;
    width: 100%;
  }

  .plugin-table-grid tr {
    border: 1px solid var(--os-rule-strong);
    border-radius: 4px;
    margin-bottom: 8px;
    padding: 4px 0;
  }

  .plugin-table-grid td {
    display: flex;
    gap: 8px;
    border-bottom: none;
    padding: 2px 8px;
  }

  .plugin-table-grid td::before {
    content: attr(data-label);
    flex: 0 0 40%;
    font-weight: 500;
    color: var(--os-text-muted, inherit);
  }

  .plugin-table-text {
    max-width: none;
    white-space: normal;
    word-break: break-word;
  }

  .plugin-table-grid .plugin-table-actions {
    position: static;
    box-shadow: none;
    justify-content: flex-end;
  }

  .plugin-table-actions::before {
    display: none;
  }
}
</style>
