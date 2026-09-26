<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuasar } from 'quasar'
import {
  createSchedule,
  deleteSchedule,
  listContainerTriggers,
  listEvents,
  listWatchRoots,
  testTriggerFolder,
  updateSchedule,
} from '../api/client'
import type {
  EventDefinition,
  FolderTestRequest,
  MemberId,
  TeamId,
  TeamTrigger,
  WatchRootOption,
} from '../api/types'
import {
  builtInWakeSources,
  createTriggerRequestFromDraft,
  folderPollSummary,
  instructionWakeSource,
  outcomeBadge,
  renderNextDue,
  triggerSentence,
  updateTriggerPatchFromDraft,
  type TriggerDraft,
  type WakeSource,
} from '../lib/triggers'
import { useConsoleStore } from '../stores/console'
import TriggerEditDialog from './TriggerEditDialog.vue'

/**
 * What ONE member has triggered - a LIST, and nothing else.
 *
 * Editing is `TriggerEditDialog`, opened over this one, not an inline form: "what is triggered?" and
 * "what should this one do?" do not belong in one scroll, and a ten-field form under a short list
 * would make the dialog's own subject the smaller half and push Save below the fold on a laptop.
 *
 * This component keeps what only it can know - the rows, the API calls, and whether a save is a
 * create or an update - and hands the editor a draft. Same split as `AgentsDialog`/`AgentEditDialog`.
 */
const open = defineModel<boolean>({ required: true })
const props = defineProps<{ team: TeamId; container: MemberId; subscribes: string[] }>()

const $q = useQuasar()
const board = useConsoleStore()

const loading = ref(false)
const saving = ref(false)
const busyId = ref<string | null>(null)
const error = ref('')
const rows = ref<TeamTrigger[]>([])

/** The event catalog, for the editor's event arm and for rendering an event row in words rather
 *  than its raw type string. Fetched here, alongside the rows, so both dialogs share one call. */
const events = ref<EventDefinition[]>([])

/** What a folder trigger may watch. Fetched with the rows; a failure leaves documents only. */
const watchRoots = ref<WatchRootOption[]>([])

/** The server's sentence for the last refused save, shown inside the editor rather than a toast. */
const saveRefusal = ref('')

const testFolder = (request: FolderTestRequest) => testTriggerFolder(props.team, request)

/** The row the editor is open on, or null for a new one. `editorOpen` is separate because null is
 *  a legitimate editing state rather than "closed". */
const editorOpen = ref(false)
const editingRow = ref<TeamTrigger | null>(null)

/**
 * The member's BUILT-IN wake sources - everything in `snapshot.subscribes` that no trigger row
 * accounts for, plus the container's own instruction type - rendered as read-only rows alongside the
 * editable trigger rows above. This is what turns the dialog from "what triggers has someone
 * configured" into "what wakes this member": a Manager subscribes to six things
 * (`TeamRegistry.ManagerSubscriptions()`) before anyone configures a single trigger, and without
 * this the dialog shows an empty list for a member that in fact wakes on seven things.
 *
 * The instruction row is always last and always present - see `instructionWakeSource`'s own doc for
 * why it is shown rather than treated as noise.
 */
const readOnlySources = computed<WakeSource[]>(() => [
  ...builtInWakeSources(props.subscribes, rows.value, props.team, props.container, events.value),
  instructionWakeSource(props.team, props.container),
])

function definitionFor(eventType: string | null): EventDefinition | null {
  if (!eventType) return null
  return events.value.find((entry) => entry.type === eventType) ?? null
}

function rowSentence(row: TeamTrigger): string {
  return triggerSentence(
    {
      kind: row.kind,
      expression: row.expression,
      timezone: row.timezone,
      intervalSeconds: row.intervalSeconds,
      fireAt: row.fireAt,
      eventType: row.eventType,
      filter: row.filter,
      watchRoot: row.watchRoot ?? null,
      watchPath: row.watchPath ?? null,
      watchGlob: row.watchGlob ?? null,
    },
    definitionFor(row.eventType),
  )
}

async function loadRows() {
  loading.value = true
  error.value = ''

  // ALLSETTLED, NOT ONE TRY/CATCH: the rows and the catalog are independent reads, and a catalog
  // fetch failing must not blank the list of triggers this member already has - the event arm
  // simply offers nothing to pick from until it succeeds.
  const [rowsResult, eventsResult, rootsResult] = await Promise.allSettled([
    listContainerTriggers(props.team, props.container),
    listEvents(),
    listWatchRoots(props.team),
  ])

  if (rowsResult.status === 'fulfilled') {
    rows.value = rowsResult.value
  } else {
    error.value = rowsResult.reason instanceof Error ? rowsResult.reason.message : String(rowsResult.reason)
  }

  events.value = eventsResult.status === 'fulfilled' ? eventsResult.value : []
  watchRoots.value = rootsResult.status === 'fulfilled' ? rootsResult.value : []

  loading.value = false
}

function isFolder(row: TeamTrigger): boolean {
  return row.kind.toLowerCase() === 'folderchange'
}

function beginAdd() {
  saveRefusal.value = ''
  editingRow.value = null
  editorOpen.value = true
}

function beginEdit(row: TeamTrigger) {
  saveRefusal.value = ''
  editingRow.value = row
  editorOpen.value = true
}

/**
 * Whether this save is a create or an update is decided HERE, from the row the editor was opened
 * on - the editor does not know and must not have to.
 */
async function save(draft: TriggerDraft) {
  saving.value = true
  saveRefusal.value = ''

  try {
    if (editingRow.value === null) {
      await createSchedule(props.team, createTriggerRequestFromDraft(draft))
      $q.notify({ type: 'positive', message: 'Trigger created.', timeout: 3500 })
    } else {
      // Re-found rather than reused: the row may have been deleted or changed by somebody else
      // while this dialog sat open, and patching a stale copy sends fields nobody edited.
      const existing = rows.value.find((row) => row.id === editingRow.value?.id)

      if (!existing) throw new Error('That trigger no longer exists.')

      const patch = updateTriggerPatchFromDraft(existing, draft)

      // ONLY CHANGED FIELDS. An empty patch means the person opened the editor and changed nothing,
      // which is not a failure and should not be a request.
      if (Object.keys(patch).length > 0) {
        await updateSchedule(props.team, existing.id, patch)
        $q.notify({ type: 'positive', message: 'Trigger updated.', timeout: 3500 })
      }
    }

    editorOpen.value = false
    editingRow.value = null

    await loadRows()
    await board.refresh()
  } catch (cause) {
    // INLINE, in the editor that is still open - the refusal names a field the person now has to
    // change ("pollSeconds must be at least 15."), and a toast is gone before they find it.
    saveRefusal.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    saving.value = false
  }
}

async function toggleEnabled(row: TeamTrigger, enabled: boolean) {
  busyId.value = row.id

  try {
    await updateSchedule(props.team, row.id, { enabled })
    row.enabled = enabled
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    })
  } finally {
    busyId.value = null
  }
}

async function remove(row: TeamTrigger) {
  busyId.value = row.id

  try {
    await deleteSchedule(props.team, row.id)
    rows.value = rows.value.filter((item) => item.id !== row.id)

    if (editingRow.value?.id === row.id) {
      editorOpen.value = false
      editingRow.value = null
    }

    await board.refresh()
    $q.notify({ type: 'positive', message: 'Trigger deleted.', timeout: 3500 })
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    })
  } finally {
    busyId.value = null
  }
}

watch(open, (showing) => {
  if (!showing) return

  editorOpen.value = false
  editingRow.value = null

  void loadRows()
})
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="triggers-card os-dialog-lg">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">What wakes {{ container }}</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="saving" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        Every trigger, subscription and direct instruction that can start a run - not only the
        triggers someone configured.
      </q-card-section>

      <q-card-section v-if="error" class="q-pt-none">
        <q-banner dense class="bg-negative text-white">{{ error }}</q-banner>
      </q-card-section>

      <q-card-section class="q-pt-none">
        <q-list bordered separator class="rounded-borders">
          <q-item v-if="loading">
            <q-item-section class="os-text-muted">Loading…</q-item-section>
          </q-item>

          <template v-else>
            <q-item v-if="rows.length === 0">
              <q-item-section class="os-text-muted">
                No triggers configured. Add one to have this member start work on its own, on a
                schedule or an event - see below for what already wakes it without one.
              </q-item-section>
            </q-item>

            <q-item v-for="row in rows" :key="row.id">
              <q-item-section>
                <q-item-label class="text-weight-medium">{{ row.name }}</q-item-label>
                <q-item-label caption>
                  {{ rowSentence(row) }} • {{ renderNextDue(row.nextDueAt) }}
                </q-item-label>
                <!-- A folder trigger polls: when it last listed, how long that took (a share is
                     slow), and when it last published. A poll that could not list says why. -->
                <q-item-label v-if="isFolder(row)" caption class="folder-poll">
                  {{ folderPollSummary(row) }}
                </q-item-label>
                <q-item-label v-if="isFolder(row) && row.lastPollError" caption class="text-negative folder-poll-error">
                  {{ row.lastPollError }}
                </q-item-label>
                <q-item-label caption class="ellipsis">{{ row.instruction }}</q-item-label>
              </q-item-section>

              <!-- `items-end` on a column, so the chip, the toggle and the buttons share one right
                   edge. Each is a different width, and left-aligned they read as three unrelated
                   controls that happen to be near each other. -->
              <q-item-section side class="column items-end q-gutter-xs">
                <q-chip dense :color="outcomeBadge(row.lastOutcome).color" text-color="white">
                  {{ outcomeBadge(row.lastOutcome).label }}
                </q-chip>

                <q-toggle
                  :model-value="row.enabled"
                  :disable="busyId === row.id"
                  dense
                  label="Enabled"
                  @update:model-value="(value) => toggleEnabled(row, !!value)"
                />

                <div class="row items-center q-gutter-xs">
                  <q-btn
                    flat
                    dense
                    round
                    icon="edit"
                    aria-label="Edit this trigger"
                    :disable="busyId === row.id || saving"
                    @click="beginEdit(row)"
                  />
                  <q-btn
                    flat
                    dense
                    round
                    color="negative"
                    icon="delete"
                    aria-label="Delete this trigger"
                    :disable="busyId === row.id || saving"
                    @click="remove(row)"
                  />
                </div>
              </q-item-section>
            </q-item>

            <!-- READ-ONLY rows: wake sources with no trigger behind them, plus the container's own
                 instruction type - see `builtInWakeSources` and `instructionWakeSource`. Deliberately
                 NO edit/delete buttons here at all, rather than disabled ones - a control that looks
                 actionable and is not is worse than no control, and these cannot be edited or deleted
                 because there is no row underneath them to change. -->
            <q-item v-for="source in readOnlySources" :key="source.type" class="wake-source-builtin">
              <q-item-section>
                <!-- Named the way the New trigger picker names it: the event type, then the
                     catalog's description under it. -->
                <q-item-label :class="source.isInstruction ? 'text-weight-medium' : 'mono'">{{ source.label }}</q-item-label>
                <q-item-label v-if="source.summary" caption>{{ source.summary }}</q-item-label>
                <q-item-label caption>
                  {{ source.isInstruction ? 'Direct instructions, whenever someone tells this member something' : 'Built in — wakes on every one, with no trigger to edit' }}
                </q-item-label>
              </q-item-section>

              <q-item-section side>
                <q-chip dense outline icon="lock" class="os-text-muted">
                  {{ source.isInstruction ? 'always on' : 'built in' }}
                </q-chip>
              </q-item-section>
            </q-item>
          </template>
        </q-list>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn
          flat
          no-caps
          icon="add"
          label="Add trigger"
          :disable="saving || loading"
          @click="beginAdd"
        />
        <q-btn v-close-popup flat no-caps label="Done" color="primary" :disable="saving" />
      </q-card-actions>
    </q-card>

    <!-- Nested, and rendered from inside this dialog deliberately: Quasar raises each dialog above
         the last, so the editor sits over the list rather than behind it. That is NOT true of a
         dialog opened from inside the Concierge panel, which is pinned at a z-index Quasar
         does not know about - see `app.scss`. -->
    <TriggerEditDialog
      v-model="editorOpen"
      :trigger="editingRow"
      :container="container"
      :events="events"
      :saving="saving"
      :watch-roots="watchRoots"
      :refusal="saveRefusal"
      :test-folder="testFolder"
      @save="save"
    />
  </q-dialog>
</template>

<style scoped>
/* A built-in wake source has no edit/delete affordance - dimming it is what marks a row as
   read-only at a glance rather than as a trigger that merely has nothing to say in its chip. */
.wake-source-builtin {
  opacity: 0.75;
}
</style>
