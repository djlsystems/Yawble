<script setup lang="ts">
import ChipListInput from './ChipListInput.vue'
import { computed, ref, watch } from 'vue'
import { useQuasar } from 'quasar'
import {
  createSkill,
  createTeamSkill,
  deleteSkill,
  deleteTeamSkill,
  updateSkill,
  updateTeamSkill,
} from '../api/client'
import type { SkillRecord, SkillRole, TeamId } from '../api/types'
import { DefaultSkillRoles, SkillRoles, isReadOnlySkill, normaliseRoles, roleLabel } from '../lib/skills'
import { required, skillName } from '../lib/rules'
import { passes, refusalStatus } from '../lib/forms'

/**
 * ONE SKILL'S EDITOR, shared by the Skills dialog and Team settings → Skills.
 *
 * `skill` null is a new skill; a built-in or a plugin's opens read-only. Where a write goes follows
 * the skill: a TEAM SKILL (one carrying `team`, or a new one created with `team` given) is written
 * through that team's routes, everything else through the instance-wide ones.
 */
const open = defineModel<boolean>({ required: true })
const props = defineProps<{
  skill: SkillRecord | null
  /** The team a NEW skill is created on; ignored when editing, where the skill says. */
  team?: TeamId | null
}>()
const emit = defineEmits<{ saved: [name: string]; removed: [name: string] }>()
const $q = useQuasar()

const formError = ref('')
const formBusy = ref(false)

const formName = ref('')
const formDescription = ref('')
const formRoles = ref<SkillRole[]>([...DefaultSkillRoles])
const formBody = ref('')

/** The team the skill belongs to, or null for an instance-wide one. */
const owner = computed<TeamId | null>(() => (props.skill ? props.skill.team ?? null : props.team ?? null))

/** A built-in opened from the table. Every field is read-only and there is nothing to press. */
const readOnly = computed(() => props.skill !== null && isReadOnlySkill(props.skill))

/** Trimmed before it is judged, because it is trimmed before it is sent. */
const nameRules = [(value: unknown) => skillName(String(value ?? '').trim())]
const descriptionRules = [required('A skill needs a description.')]

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

watch(
  open,
  (showing) => {
    if (!showing) return
    const row = props.skill
    formName.value = row?.name ?? ''
    formDescription.value = row?.description ?? ''
    formRoles.value = row ? [...row.roles] : [...DefaultSkillRoles]
    formBody.value = row?.body ?? ''
    formError.value = ''
    formErrorStatus.value = undefined
  },
  { immediate: true },
)

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
    const team = owner.value
    if (props.skill) {
      if (team) await updateTeamSkill(team, props.skill.name, draft)
      else await updateSkill(props.skill.name, draft)
    } else if (team) await createTeamSkill(team, draft)
    else await createSkill(draft)

    open.value = false
    emit('saved', name)
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
  const row = props.skill
  if (!row || isReadOnlySkill(row)) return

  formBusy.value = true
  formError.value = ''
  try {
    const team = owner.value
    if (team) await deleteTeamSkill(team, row.name)
    else await deleteSkill(row.name)
    open.value = false
    emit('removed', row.name)
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
    <q-card class="os-dialog-lg">
      <q-form @submit="save">
      <q-card-section class="os-dialog-title">
        {{ skill ? (readOnly ? skill.name : `Edit ${skill.name}`) : 'Add a skill' }}
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
            <ChipListInput
              :model-value="formRoles"
              label="Roles"
              :options="roleOptions"
              item-name="role"
              :readonly="readOnly"
              :disable="formBusy"
              :error="!readOnly && formRoles.length === 0"
              error-message="Choose at least one role this skill is offered to."
              :hint="readOnly ? undefined : 'Which agents are offered this skill. Any is every role.'"
              data-skill-roles
              @update:model-value="(value: string[]) => (formRoles = value as SkillRole[])"
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
          v-if="skill && !readOnly"
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
