<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuasar } from 'quasar'
import { createSkill, deleteSkill, listSkillsPage, updateSkill } from '../api/client'
import type { SkillRecord, SkillRole } from '../api/types'
import {
  DefaultSkillRoles,
  SkillRoles,
  isReadOnlySkill,
  kindLabel,
  lastModified,
  normaliseRoles,
  roleLabel,
  rolesLabel,
} from '../lib/skills'
import { useCursorList } from '../lib/useCursorList'
import { required, skillName } from '../lib/rules'
import { passes, refusalStatus } from '../lib/forms'
import CursorSentinel from './CursorSentinel.vue'

/**
 * SKILLS, ONE TENANT-WIDE LIST OF TWO KINDS.
 *
 * Built-in skills come from the build and change only through a code change, so a built-in row opens
 * read-only: no save, no delete, no rename. Custom skills are a person's, and only those are created,
 * edited and deleted here. The list shows Custom by default; "Show built-in" adds the rest.
 *
 * SERVER-SIDE, BY CURSOR, exactly as the tenant log reads. A change of filter CLEARS the list
 * and reads again from the top - never merged, or rows of the old filter would read as matching the
 * new one.
 */
const open = defineModel<boolean>({ required: true })
const $q = useQuasar()

const showBuiltIn = ref(false)
const search = ref('')

const list = useCursorList<SkillRecord>(
  (before, take) =>
    listSkillsPage({ kind: showBuiltIn.value ? 'all' : 'custom', q: search.value }, before, take),
  (row) => row.id,
  { filter: () => [showBuiltIn.value, search.value] },
)
const { rows, loading, exhausted, error } = list

watch(open, (showing) => {
  if (showing) void list.reset()
})

const editing = ref<SkillRecord | null>(null)
const editOpen = ref(false)
const formError = ref('')
const formBusy = ref(false)

const formName = ref('')
const formDescription = ref('')
const formRoles = ref<SkillRole[]>([...DefaultSkillRoles])
const formBody = ref('')

/** A built-in opened from the table. Every field is read-only and there is nothing to press. */
const readOnly = computed(() => editing.value !== null && isReadOnlySkill(editing.value))

/** Trimmed before it is judged, because it is trimmed before it is sent. */
const nameRules = [(value: unknown) => skillName(String(value ?? '').trim())]
const descriptionRules = [required('A skill needs a description.')]
const rolesRules = [
  (value: readonly SkillRole[] | null) =>
    (value?.length ?? 0) > 0 || 'Choose at least one role this skill is offered to.',
]

const roleOptions = SkillRoles.map((role) => ({ label: roleLabel(role), value: role }))

const canSave = computed(
  () =>
    !readOnly.value &&
    passes(formName.value, nameRules) &&
    passes(formDescription.value, descriptionRules) &&
    formRoles.value.length > 0,
)

/** The status behind `formError`, and the name it answered. A 409 marks the name until edited. */
const formErrorStatus = ref<number | undefined>(undefined)
const refusedName = ref<string | null>(null)
const nameTaken = computed(
  () => formErrorStatus.value === 409 && formError.value !== '' && refusedName.value === formName.value.trim(),
)

function openCreate() {
  editing.value = null
  formName.value = ''
  formDescription.value = ''
  formRoles.value = [...DefaultSkillRoles]
  formBody.value = ''
  formError.value = ''
  formErrorStatus.value = undefined
  editOpen.value = true
}

function openRow(row: SkillRecord) {
  editing.value = row
  formName.value = row.name
  formDescription.value = row.description
  formRoles.value = [...row.roles]
  formBody.value = row.body
  formError.value = ''
  formErrorStatus.value = undefined
  editOpen.value = true
}

async function save() {
  if (!canSave.value) return

  formBusy.value = true
  formError.value = ''
  formErrorStatus.value = undefined
  const name = formName.value.trim()
  refusedName.value = name

  const draft = {
    name,
    description: formDescription.value.trim(),
    roles: normaliseRoles(formRoles.value),
    body: formBody.value,
  }

  try {
    if (editing.value) await updateSkill(editing.value.name, draft)
    else await createSkill(draft)

    editOpen.value = false
    await list.reset()
    $q.notify({ type: 'positive', message: `${name} saved.` })
  } catch (failure) {
    // The server's own sentence - a built-in's name, a missing role - shown as it was written.
    formError.value = (failure as Error).message
    formErrorStatus.value = refusalStatus(failure)
  } finally {
    formBusy.value = false
  }
}

async function remove() {
  const row = editing.value
  if (!row || isReadOnlySkill(row)) return

  formBusy.value = true
  formError.value = ''
  try {
    await deleteSkill(row.name)
    editOpen.value = false
    await list.reset()
    $q.notify({ type: 'positive', message: `${row.name} removed.` })
  } catch (failure) {
    formError.value = (failure as Error).message
  } finally {
    formBusy.value = false
  }
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="skills-card os-dialog-xl">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Skills</div>
        <q-space />
        <q-toggle v-model="showBuiltIn" dense label="Show built-in" />
        <q-btn v-close-popup flat round dense icon="close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        Built-in skills come with this build and are read-only. Your own skills are Custom, and each is
        offered to the roles you choose for it.
      </q-card-section>

      <q-card-section class="q-pt-none">
        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-md">
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>

        <q-input
          v-model="search"
          dense
          outlined
          clearable
          debounce="300"
          label="Search skills"
          class="q-mb-sm"
        >
          <template #prepend><q-icon name="search" /></template>
        </q-input>

        <q-virtual-scroll
          type="table"
          class="skills-scroll"
          dense
          flat
          bordered
          :items="rows"
          :virtual-scroll-item-size="33"
          :virtual-scroll-sticky-size-start="33"
        >
          <template #before>
            <thead class="skills-head">
              <tr>
                <th class="text-left">Name</th>
                <th class="text-left">Description</th>
                <th class="text-left">Roles</th>
                <th class="text-left">Kind</th>
                <th class="text-left">Last modified</th>
              </tr>
            </thead>
          </template>

          <template #default="{ item: row }">
            <tr
              :key="row.id"
              class="skills-row cursor-pointer"
              tabindex="0"
              :data-skill="row.name"
              @click="openRow(row)"
              @keydown.enter="openRow(row)"
            >
              <td class="mono">{{ row.name }}</td>
              <td class="skills-description">{{ row.description }}</td>
              <td>{{ rolesLabel(row.roles) }}</td>
              <td>
                <q-badge
                  :outline="row.kind !== 'builtin'"
                  :color="row.kind === 'builtin' ? 'grey-7' : 'primary'"
                  :label="kindLabel(row.kind)"
                />
              </td>
              <td class="skills-when">{{ lastModified(row) }}</td>
            </tr>
          </template>

          <template #after>
            <tfoot>
              <tr>
                <td colspan="5">
                  <CursorSentinel
                    :loading="loading"
                    :exhausted="exhausted"
                    :error="error"
                    :done-label="rows.length ? 'No more skills.' : showBuiltIn ? 'No skills.' : 'No custom skills yet.'"
                    @more="list.loadMore"
                  />
                </td>
              </tr>
            </tfoot>
          </template>
        </q-virtual-scroll>

        <div class="row justify-end q-mt-sm">
          <q-btn flat dense no-caps icon="add" label="Add a skill" @click="openCreate" />
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>

  <q-dialog v-model="editOpen">
    <q-card class="os-dialog-lg">
      <q-form @submit="save">
      <q-card-section class="os-dialog-title">
        {{ editing ? (readOnly ? editing.name : `Edit ${editing.name}`) : 'Add a skill' }}
      </q-card-section>
      <q-card-section v-if="readOnly" class="q-pt-none">
        <q-banner dense class="os-bg-tint-info">
          <template #avatar><q-icon name="lock" /></template>
          A built-in skill comes with this build and changes only with it. It cannot be edited,
          relabelled or deleted here.
        </q-banner>
      </q-card-section>
      <q-card-section v-if="formError" class="q-pt-none">
        <q-banner dense class="os-bg-tint-error text-negative">
          <template #avatar><q-icon name="error" /></template>
          {{ formError }}
        </q-banner>
      </q-card-section>
      <q-card-section class="q-gutter-sm">
        <div class="row q-col-gutter-sm">
          <div class="col-12 col-sm-5">
            <q-input
              v-model="formName"
              dense
              outlined
              label="Name"
              lazy-rules
              :rules="readOnly ? [] : nameRules"
              :error="nameTaken ? true : undefined"
              :error-message="formError"
              :readonly="readOnly"
              :disable="formBusy"
            />
          </div>
          <div class="col-12 col-sm-7">
            <q-select
              v-model="formRoles"
              dense
              outlined
              multiple
              emit-value
              map-options
              use-chips
              label="Roles"
              :options="roleOptions"
              :rules="readOnly ? [] : rolesRules"
              :readonly="readOnly"
              :disable="formBusy"
              :hint="readOnly ? undefined : 'Which agents are offered this skill. Any is every role.'"
            />
          </div>
        </div>

        <q-input
          v-model="formDescription"
          dense
          outlined
          label="Description"
          lazy-rules
          :rules="readOnly ? [] : descriptionRules"
          :readonly="readOnly"
          :disable="formBusy"
        />

        <q-input
          v-model="formBody"
          type="textarea"
          autogrow
          outlined
          label="Body"
          :readonly="readOnly"
          :disable="formBusy"
          input-style="font-family: var(--os-mono)"
        />
      </q-card-section>
      <q-card-actions align="right">
        <q-btn
          v-if="editing && !readOnly"
          flat
          no-caps
          color="negative"
          icon="delete"
          label="Delete"
          :disable="formBusy"
          @click="remove"
        />
        <q-space />
        <q-btn v-close-popup flat no-caps :label="readOnly ? 'Close' : 'Cancel'" :disable="formBusy" />
        <q-btn
          v-if="!readOnly"
          type="submit"
          color="primary"
          unelevated
          no-caps
          label="Save"
          :loading="formBusy"
          :disable="!canSave || formBusy"
        />
      </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* A BOUND ON THE TABLE, NOT ON THE CARD, so the toggle and Close never scroll away with the rows;
   the footer sentinel sits at its bottom. */
.skills-scroll {
  max-height: min(60vh, 36rem);
}

.skills-head th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: var(--os-surface);
}

.skills-description {
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.skills-when {
  white-space: nowrap;
}

.mono {
  font-family: var(--os-mono);
}
</style>
