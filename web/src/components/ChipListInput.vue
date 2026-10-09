<script setup lang="ts">
import { computed, ref } from 'vue';
import type { QSelect } from 'quasar';

/**
 * THE ONE MULTIPLE-ITEM INPUT. Every unordered list a person edits - a plugin's list settings, a
 * package's list inputs, an Agent's tags, a skill's roles, system packages, repositories - is a
 * Quasar select showing its items as chips, so a list looks and behaves the same everywhere:
 *
 * - FROM A FIXED SET (`options`): the dropdown offers what is not chosen yet, and each chosen item is
 *   a removable chip.
 * - FREE TEXT (`freeform`, the default when there are no options): typed into the list's own field,
 *   Return adds it as a removable chip; the Add button opens a dialog for it, where several lines add
 *   several items. Both make the same check (`accept`): an empty, repeated or refused value adds
 *   nothing and the one line under the field says why, keeping the text to be corrected. A person who
 *   typed an address and pressed Return once saw nothing happen and nothing said.
 * - READ-ONLY (`readonly`): the same chips with no remove and no add.
 *
 * ORDERED LISTS ARE NOT THIS. A command line, `KEY=value` lines and a priority allowlist have order
 * as their meaning, and chips cannot be reordered; those keep their own editors.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string[] | null | undefined;
    label?: string | undefined;
    hint?: string | undefined;
    /** The fixed set to choose from. Strings, or `{ label, value }` where the label differs. */
    options?: (string | { label: string; value: string })[] | null;
    /** Whether a value outside `options` may be typed in as well. Always so when there are no options. */
    freeform?: boolean;
    readonly?: boolean;
    disable?: boolean;
    /** A refusal for one value about to be added, or null to accept it. */
    validate?: ((value: string, list: string[]) => string | null) | undefined;
    /** Compares case-insensitively for duplicates, as tags do. */
    ignoreCase?: boolean;
    mono?: boolean;
    error?: boolean | undefined;
    errorMessage?: string | undefined;
    /** What the add dialog calls one item: "tag", "position", "package". */
    itemName?: string;
    /** A class for the dropdown and the add dialog, when this sits in a lifted dialog. */
    layerClass?: string | undefined;
  }>(),
  {
    options: null,
    freeform: false,
    readonly: false,
    disable: false,
    ignoreCase: false,
    mono: false,
    itemName: 'item',
  },
);

const emit = defineEmits<{ 'update:modelValue': [string[]] }>();

const list = computed(() => props.modelValue ?? []);

const optionEntries = computed(() =>
  (props.options ?? []).map((option) => (typeof option === 'string' ? { label: option, value: option } : option)),
);

// `||`, not `??`: Vue reads an absent boolean prop as false, never undefined.
const canType = computed(() => props.freeform || optionEntries.value.length === 0);

/** What the add dialog and its button say: "Add to positions", or "Add a tag" with no label. */
const addTitle = computed(() => {
  const target = props.label?.replace(/\s*\(optional\)$/i, '');
  return target ? `Add to ${target}` : `Add a ${props.itemName}`;
});

const labelOf = (value: string) => optionEntries.value.find((option) => option.value === value)?.label ?? value;

const same = (a: string, b: string) => (props.ignoreCase ? a.toLowerCase() === b.toLowerCase() : a === b);
const has = (items: string[], value: string) => items.some((item) => same(item, value));

/** The dropdown offers only what is not chosen yet. */
const remaining = computed(() => optionEntries.value.filter((option) => !has(list.value, option.value)));

function remove(value: string) {
  emit('update:modelValue', list.value.filter((item) => item !== value));
}

function onSelect(values: string[] | null) {
  emit('update:modelValue', values ?? []);
}

/**
 * THE ONE CHECK BOTH WAYS IN MAKE: the list with `values` added, or the sentence saying why nothing
 * is - no value, one already there, or one the caller refuses.
 */
function accept(values: string[]): { next: string[] } | { problem: string } {
  if (values.length === 0) return { problem: 'Type a value first.' };

  const next = [...list.value];
  for (const value of values) {
    if (has(next, value)) return { problem: `${value} is already in the list.` };
    const refusal = props.validate?.(value, next) ?? null;
    if (refusal !== null) return { problem: refusal };
    next.push(value);
  }
  return { next };
}

/* THE LIST'S OWN FIELD: Return adds what is typed there. */
const select = ref<QSelect | null>(null);
const typed = ref('');
const typedProblem = ref('');

function onTyped(value: string) {
  typed.value = value;
  typedProblem.value = '';
}

/**
 * Return adds the typed value. With a fixed set as well, an empty Return is left to the dropdown,
 * which opens or picks the highlighted option; a typed one is added, as its option's value when it
 * names one.
 */
function onFieldKey(event: KeyboardEvent) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing || !canType.value || props.readonly) return;

  const value = typed.value.trim();
  if (value === '' && optionEntries.value.length > 0) return;
  event.preventDefault();
  event.stopPropagation();

  const named = optionEntries.value.find((option) => option.label === value)?.value ?? value;
  const outcome = accept(value === '' ? [] : [named]);
  if ('problem' in outcome) {
    typedProblem.value = outcome.problem;
    return;
  }

  emit('update:modelValue', outcome.next);
  typed.value = '';
  select.value?.updateInputValue('', true);
}

/* THE ADD DIALOG. */
const adding = ref(false);
const draft = ref('');
const problem = ref('');

/** Opens with whatever is typed in the field, so + carries it rather than dropping it. */
function openAdd() {
  draft.value = typed.value.trim();
  problem.value = '';
  adding.value = true;
}

/** Each non-blank line, trimmed, is one item. A value with commas in it ("Boston, MA") stays whole. */
const draftValues = computed(() => draft.value.split(/\r?\n/).map((line) => line.trim()).filter((line) => line !== ''));

function submitAdd() {
  const outcome = accept(draftValues.value);
  if ('problem' in outcome) {
    problem.value = outcome.problem;
    return;
  }

  emit('update:modelValue', outcome.next);
  adding.value = false;
  typed.value = '';
  typedProblem.value = '';
  select.value?.updateInputValue('', true);
}

/** Enter adds; Shift+Enter starts another line. It stops here, so no form behind it submits. */
function onDraftKey(event: KeyboardEvent) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return;
  event.preventDefault();
  event.stopPropagation();
  submitAdd();
}
</script>

<template>
  <div class="chip-list-input" data-chip-list>
    <q-select
      ref="select"
      :model-value="list"
      :options="remaining"
      option-label="label"
      option-value="value"
      emit-value
      map-options
      multiple
      outlined
      dense
      stack-label
      :label="label"
      :hint="hint"
      :readonly="readonly"
      :disable="disable"
      :use-input="canType && !readonly && !disable"
      input-debounce="0"
      :error="typedProblem !== '' || error"
      :error-message="typedProblem || errorMessage"
      :hide-dropdown-icon="readonly || optionEntries.length === 0"
      :popup-content-class="layerClass"
      class="chip-list-select"
      @update:model-value="onSelect"
      @input-value="onTyped"
      @keydown="onFieldKey"
    >
      <template #selected>
        <div class="chip-list-chips">
          <q-chip
            v-for="item in list"
            :key="item"
            dense
            :removable="!readonly && !disable"
            :class="{ mono }"
            :remove-aria-label="`Remove ${labelOf(item)}`"
            :data-chip="item"
            @remove="remove(item)"
          >{{ labelOf(item) }}</q-chip>
          <span v-if="list.length === 0 && typed === ''" class="os-text-muted chip-list-empty">none</span>
        </div>
      </template>

      <template v-if="canType && !readonly" #append>
        <q-btn
          flat
          dense
          round
          size="sm"
          icon="add"
          :disable="disable"
          :aria-label="addTitle"
          data-chip-add
          @click.stop="openAdd"
        >
          <q-tooltip>{{ addTitle }}</q-tooltip>
        </q-btn>
      </template>

      <template v-if="optionEntries.length > 0" #no-option>
        <q-item dense>
          <q-item-section class="os-text-muted">Everything is chosen.</q-item-section>
        </q-item>
      </template>
    </q-select>

    <q-dialog v-model="adding" :class="layerClass">
      <q-card class="os-dialog-sm" data-chip-add-dialog>
        <q-card-section class="os-dialog-title">{{ addTitle }}</q-card-section>
        <q-card-section class="q-pt-none">
          <q-input
            v-model="draft"
            autofocus
            outlined
            dense
            type="textarea"
            autogrow
            :label="label ? 'Value' : `New ${itemName}`"
            hint="Enter adds it. Shift+Enter for another line: each line is one more."
            :error="problem !== ''"
            :error-message="problem"
            :class="{ mono }"
            data-chip-add-input
            @update:model-value="problem = ''"
            @keydown="onDraftKey"
          />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps label="Cancel" />
          <q-btn unelevated no-caps color="primary" label="Add" data-chip-add-submit @click="submitAdd" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.chip-list-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 2px 0;
  padding: 2px 0;
}

.chip-list-empty {
  font-size: 12px;
  padding: 4px 0;
}
</style>
