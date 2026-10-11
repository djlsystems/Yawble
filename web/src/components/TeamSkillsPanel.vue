<script setup lang="ts">
import { ref, watch } from 'vue'
import { listTeamSkills } from '../api/client'
import type { SkillRecord, TeamId } from '../api/types'
import { rolesLabel } from '../lib/skills'
import SkillEditDialog from './SkillEditDialog.vue'

/**
 * TEAM SETTINGS → SKILLS: this team's own skills, offered only to its members of each skill's roles
 * - in their skill search and their prompt's skill list - and refused to every other team. Each is
 * written the moment it is saved, with its own Save in the editor, not by Team settings' Save.
 */
const props = defineProps<{ team: TeamId }>()

const rows = ref<SkillRecord[]>([])
const loading = ref(false)
const error = ref('')

async function load() {
  loading.value = true
  error.value = ''
  try {
    rows.value = await listTeamSkills(props.team)
  } catch (failure) {
    error.value = (failure as Error).message
  } finally {
    loading.value = false
  }
}

watch(() => props.team, () => void load(), { immediate: true })

const editing = ref<SkillRecord | null>(null)
const editOpen = ref(false)

function openCreate() {
  editing.value = null
  editOpen.value = true
}

function openRow(row: SkillRecord) {
  editing.value = row
  editOpen.value = true
}
</script>

<template>
  <div class="os-body os-text-muted q-mb-sm">
    This team's own skills. Each is offered only to this team's members of the roles you choose for
    it, and never to another team. Every team is also offered the instance's skills.
  </div>

  <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-sm">
    <template #avatar><q-icon name="error" /></template>
    {{ error }}
  </q-banner>

  <q-list bordered separator class="rounded-borders team-skills">
    <q-item
      v-for="row in rows"
      :key="row.id"
      v-ripple
      clickable
      :data-team-skill="row.name"
      @click="openRow(row)"
    >
      <q-item-section>
        <q-item-label class="mono">{{ row.name }}</q-item-label>
        <q-item-label caption>{{ row.description }}</q-item-label>
      </q-item-section>
      <q-item-section side>{{ rolesLabel(row.roles) }}</q-item-section>
    </q-item>
    <q-item v-if="!loading && rows.length === 0 && !error">
      <q-item-section class="os-text-muted">No team skills yet.</q-item-section>
    </q-item>
  </q-list>

  <div class="row justify-end q-mt-sm">
    <q-btn flat dense no-caps icon="add" label="Add a team skill" @click="openCreate" />
  </div>

  <SkillEditDialog
    v-model="editOpen"
    :skill="editing"
    :team="team"
    @saved="load()"
    @removed="load()"
  />
</template>

<style scoped>
.mono {
  font-family: var(--os-mono);
}
</style>
