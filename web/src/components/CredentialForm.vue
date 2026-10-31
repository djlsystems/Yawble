<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import { computed, ref, watch } from 'vue';
import { addCredentialConnection, editCredentialConnection } from '../api/client';
import type { Connection, CredentialFieldValue, CredentialPreset } from '../api/types';
import { credentialKindWords, credentialRows, type CredentialRow } from '../lib/connections';

/**
 * A WEBSITE, AN API KEY, A USERNAME AND PASSWORD: the form inside Add connection, and the same form
 * to edit one from Admin -> Connections.
 *
 * The forms are the Host's presets (`GET /api/connections/credentials/presets`), never hard-coded
 * here: an API key chooses its service first (Stripe, Namecheap, Twilio, Other) and then shows only
 * that service's fields, each masked or not as the preset says. Other names its own fields, each with
 * a Masked switch. A website (CON 6) has typed fields - its Site URL, its method (a dropdown), May act
 * on pages (a switch, off unless a person turns it on) - with the field selectors, language and time
 * zone under Advanced, and a saved session only Sign in keeps: it is shown as kept or not, never typed.
 *
 * EVERY MASKED VALUE IS WRITE-ONLY: typed, sent once, cleared once saved, never filled from anything.
 * Editing, a masked field that is set says so and stays empty; left empty it keeps its value, typed it
 * replaces it - and every team and member using the connection gets the new value at its next run.
 *
 * THE NAME IS REQUIRED and is the connection's key: the Host refuses one another connection has.
 */
const props = withDefaults(defineProps<{
  presets: CredentialPreset[];
  kind: 'website' | 'apikey' | 'login';
  /** Editing: the connection whose fields are changed. Its name is renamed elsewhere. */
  connection?: Connection | null;
}>(), { connection: null });
const emit = defineEmits<{ saved: [connection: Connection] }>();

const editing = computed(() => props.connection !== null);
const choices = computed(() => props.presets.filter((preset) => preset.kind === props.kind));
const presetId = ref<string | null>(null);
const preset = computed(() => choices.value.find((candidate) => candidate.id === presetId.value) ?? null);
const name = ref('');
const rows = ref<CredentialRow[]>([]);
const problem = ref('');
const busy = ref(false);
const saved = ref(false);

function reset() {
  presetId.value = props.connection?.preset ?? (choices.value.length === 1 ? choices.value[0]!.id : null);
  name.value = '';
  problem.value = '';
  saved.value = false;
  fill();
}

function fill() {
  rows.value = preset.value ? credentialRows(preset.value, props.connection) : [];
}

watch(() => [props.kind, props.connection?.id, props.presets.length], reset, { immediate: true });

function choosePreset(id: string | null) {
  presetId.value = id;
  problem.value = '';
  fill();
}

function addField() {
  rows.value = [...rows.value, { name: '', label: '', masked: true, required: true, value: '', set: false, named: true }];
}

function removeField(index: number) {
  rows.value = rows.value.filter((_, at) => at !== index);
}

const ready = computed(() => {
  if (!preset.value || saved.value) return false;
  if (!editing.value && name.value.trim() === '') return false;
  if (preset.value.namedFields && rows.value.length === 0) return false;
  return rows.value.every((row) =>
    row.readOnly
    || ((!row.named || row.name.trim() !== '')
    && (!row.required || row.value !== '' || (editing.value && row.set))));
});

/** The fields shown first, and those under Advanced (a website's selectors, language and time zone). */
const mainRows = computed(() => rows.value.filter((row) => !row.advanced));
const advancedRows = computed(() => rows.value.filter((row) => row.advanced));
const showAdvanced = ref(false);

async function save() {
  if (!ready.value || busy.value) return;

  busy.value = true;
  problem.value = '';
  // Editing, an empty masked field keeps its value: it is sent with none.
  // A field the platform sets (a website's saved session) is never sent.
  const fields: CredentialFieldValue[] = rows.value.filter((row) => !row.readOnly).map((row) => ({
    name: row.name.trim(),
    value: editing.value && row.masked && row.value === '' ? null : row.value,
    ...(preset.value!.namedFields ? { masked: row.masked } : {}),
  }));
  try {
    const answer = editing.value
      ? await editCredentialConnection(props.connection!.id, fields)
      : await addCredentialConnection({ kind: props.kind, preset: presetId.value, name: name.value.trim(), fields });
    for (const row of rows.value) if (row.masked) row.value = '';
    saved.value = true;
    emit('saved', answer.connection);
  } catch (cause) {
    // The Host's sentence: which field is wrong, or the name another connection has. Nothing was saved.
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/** Whether the person has typed anything not yet saved: the dialog then holds on to it. */
const typed = computed(() => !saved.value && (name.value !== '' || rows.value.some((row) => row.value !== '' && !(!row.masked && row.value === row.original))));

defineExpose({ save, ready, busy, saved, typed });
</script>

<template>
  <div class="q-gutter-y-sm" data-credential-form :data-credential-kind="kind">
    <div v-if="kind === 'website'" class="text-body2" data-website-note>
      What the platform's own browser signs in to the site with. Members use it through Team Connections, on the
      site's hosts only, and never see a value. For an interactive one, save it, then use Sign in on it in Connections.
    </div>

    <div v-if="choices.length > 1 && !editing" class="connect-tiles" data-credential-presets>
      <q-btn
        v-for="choice in choices"
        :key="choice.id ?? ''"
        no-caps
        align="left"
        class="connect-tile"
        :outline="presetId !== choice.id"
        :color="presetId === choice.id ? 'primary' : undefined"
        :aria-pressed="presetId === choice.id ? 'true' : 'false'"
        :data-credential-preset="choice.id"
        :disable="saved"
        @click="choosePreset(choice.id)"
      >
        <span class="text-weight-medium">{{ choice.name }}</span>
      </q-btn>
    </div>

    <template v-if="preset">
      <q-input
        v-if="!editing"
        v-model="name"
        outlined
        dense
        label="Name"
        maxlength="80"
        data-credential-name
        :disable="saved"
      >
        <template #append><FieldHelp :text="`What teams and members use this ${credentialKindWords(kind)} by. No other connection may have it.`" /></template>
      </q-input>

      <template v-for="(row, index) in rows" :key="index">
      <div
        v-if="!row.advanced || showAdvanced"
        class="row items-start q-col-gutter-sm"
        :data-credential-field="row.named ? index : row.name"
      >
        <q-select
          v-if="row.type === 'enum'"
          v-model="row.value"
          class="col-12"
          outlined
          dense
          :options="row.options ?? []"
          :label="`${row.label}${row.required ? '' : ' (optional)'}`"
          data-credential-field-value
          :disable="saved"
        >
          <template v-if="row.help" #append><FieldHelp :text="row.help" /></template>
        </q-select>
        <div v-else-if="row.type === 'bool'" class="col-12 row items-center no-wrap">
          <q-toggle
            :model-value="row.value === 'true'"
            :label="row.label"
            data-credential-field-value
            :disable="saved"
            @update:model-value="(on: boolean) => { row.value = on ? 'true' : 'false'; }"
          />
          <FieldHelp v-if="row.help" :text="row.help" />
        </div>
        <div v-else-if="row.readOnly" class="col-12 row items-center no-wrap text-body2" data-credential-field-read-only>
          <span>{{ row.label }}: {{ row.set ? 'kept' : 'none yet' }}</span>
          <FieldHelp v-if="row.help" :text="row.help" />
        </div>
        <template v-else>
        <q-input
          v-if="row.named"
          v-model="row.name"
          class="col-4"
          outlined
          dense
          label="Field"
          data-credential-field-name
          :disable="saved"
        />
        <q-input
          v-model="row.value"
          :class="row.named ? 'col' : 'col-12'"
          outlined
          dense
          autocomplete="off"
          :type="row.masked ? 'password' : 'text'"
          :label="row.named ? 'Value' : `${row.label}${row.required ? '' : ' (optional)'}`"
          data-credential-field-value
          :disable="saved"
        >
          <template v-if="row.masked" #append>
            <FieldHelp :text="row.set && editing ? 'Set. Leave empty to keep it, or type a new one to replace it.' : `${row.help ? row.help + ' ' : ''}Stored encrypted and never shown again.`" />
          </template>
          <template v-else-if="row.help" #append><FieldHelp :text="row.help" /></template>
        </q-input>
        <template v-if="row.named">
          <q-toggle v-model="row.masked" label="Masked" data-credential-field-masked :disable="saved" />
          <q-btn flat dense round icon="close" aria-label="Remove field" data-credential-field-remove :disable="saved" @click="removeField(index)" />
        </template>
        </template>
      </div>
      <q-btn
        v-if="advancedRows.length > 0 && !row.advanced && index === rows.indexOf(mainRows[mainRows.length - 1]!)"
        flat
        dense
        no-caps
        :icon="showAdvanced ? 'keyboard_arrow_up' : 'keyboard_arrow_down'"
        :label="showAdvanced ? 'Hide Advanced' : 'Advanced'"
        data-credential-advanced
        @click="showAdvanced = !showAdvanced"
      />
      </template>

      <q-btn
        v-if="preset.namedFields"
        flat
        dense
        no-caps
        icon="add"
        label="Add a field"
        data-credential-add-field
        :disable="saved || rows.length >= 20"
        @click="addField"
      />
    </template>

    <div v-if="problem" class="text-negative" data-credential-problem>{{ problem }}</div>
    <div v-if="saved" class="text-positive" data-credential-saved>
      {{ editing ? 'Saved. Every team and member using it gets the new value at its next run.' : 'Saved.' }}
    </div>
  </div>
</template>

<style scoped>
.connect-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 8px;
}
</style>
