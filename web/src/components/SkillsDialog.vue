<script setup lang="ts">
import { ref, watch } from 'vue'
import { listSkillsPage } from '../api/client'
import type { SkillRecord } from '../api/types'
import { kindLabel, lastModified, rolesLabel, teamLabel } from '../lib/skills'
import { useCursorList } from '../lib/useCursorList'
import { vResizableColumns } from '../lib/resizableColumns'
import { useConsoleStore } from '../stores/console'
import CursorSentinel from './CursorSentinel.vue'
import SkillEditDialog from './SkillEditDialog.vue'

/**
 * SKILLS, ONE TENANT-WIDE LIST OF TWO KINDS.
 *
 * Built-in skills come from the build and change only through a code change, so a built-in row opens
 * read-only: no save, no delete, no rename. Custom skills are a person's, and only those are created,
 * edited and deleted here. The list shows Custom by default; "Show built-in" adds the rest.
 *
 * A TEAM SKILL is a custom skill that belongs to one team, offered only to that team's members; the
 * Team column names it ("All teams" for every other skill). It is created in Team settings → Skills,
 * and edited here or there - the editor writes it through its team's routes.
 *
 * SERVER-SIDE, BY CURSOR, exactly as the tenant log reads. A change of filter CLEARS the list
 * and reads again from the top - never merged, or rows of the old filter would read as matching the
 * new one.
 */
const open = defineModel<boolean>({ required: true })
const board = useConsoleStore()

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

function openCreate() {
  editing.value = null
  editOpen.value = true
}

function openRow(row: SkillRecord) {
  editing.value = row
  editOpen.value = true
}

const teamOf = (row: SkillRecord) => teamLabel(row.team ?? null, board.teams)
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
        offered to the roles you choose for it. A team's own skills are offered only to that team's
        members; add them in Team settings → Skills.
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
          v-resizable-columns="'skills'"
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
                <th class="text-left">Team</th>
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
              <td class="skills-team">{{ teamOf(row) }}</td>
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
                <td colspan="6">
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

  <SkillEditDialog v-model="editOpen" :skill="editing" @saved="list.reset()" @removed="list.reset()" />
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

/* A resized table's column is as wide as a person dragged it: the cap above is for the natural
   layout only. `lib/resizableColumns.ts` marks a pinned table `os-cols-fixed` at run time, inside
   the virtual scroll, hence :deep. */
:deep(.os-cols-fixed) .skills-description {
  max-width: none;
}

.skills-when,
.skills-team {
  white-space: nowrap;
}

.mono {
  font-family: var(--os-mono);
}
</style>
