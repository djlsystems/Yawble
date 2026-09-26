<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type {
  EventDefinition,
  FolderTestRequest,
  FolderTestResult,
  TeamTrigger,
  WatchRootOption,
} from '../api/types'
import {
  FileChangedEventType,
  MinimumPollSeconds,
  draftForCreate,
  draftFromTrigger,
  eventTypeSelectOptions,
  formatPollDuration,
  fromDateTimeLocalValue,
  cronPreviewForDraft,
  triggerDraftProblem,
  triggerFieldProblems,
  watchRootOptions,
  type TriggerDraft,
  type TriggerField,
} from '../lib/triggers'
import { positiveInt, timezone, type Rule } from '../lib/rules'
import TokenPicker from './TokenPicker.vue'
import CronBuilderDialog from './CronBuilderDialog.vue'

/**
 * ONE trigger, added or edited, in its own dialog.
 *
 * Separate from `TriggersDialog`, which lists them. The two questions a person actually has - "what
 * is triggered?" and "what should this one do?" - do not belong in one scroll: the list is a few
 * lines and the form is ten fields, so an inline form would make the dialog's subject the smaller
 * half and push Save below the fold on a laptop.
 *
 * PRESENTATIONAL: it owns a draft and emits it. It does not call the API, does not know whether it
 * is creating or updating, and holds no list - `TriggersDialog` decides all three. That is the same
 * split `AgentEditDialog` has from `AgentsDialog`, and for the same reason: the component that
 * knows every other row is the only one that can save correctly.
 *
 * There is no "target member" field. `TriggersDialog` is scoped to one container, so the target is
 * implicit - `container` below is fixed, not chosen.
 *
 * The folder kind keeps that split: the roots it may watch, the server's refusal of the last save
 * and the "Test this folder" call all come in as props. `testFolder` is a function handed down
 * rather than an import of the API, so this component still calls nothing itself.
 */
const open = defineModel<boolean>({ required: true })

const props = withDefaults(defineProps<{
  /** The row being edited, or null when this is a new trigger. */
  trigger: TeamTrigger | null

  /** The member every trigger opened from this dialog targets - the card it was opened from. */
  container: string

  /** The event catalog, for the Kind selector's event arm. Fetched once by `TriggersDialog` and
   *  shared with this editor rather than fetched twice. */
  events: EventDefinition[]

  saving: boolean

  /** What a folder trigger may watch, from `GET .../triggers/watch-roots`. Documents is offered
   *  even when this is empty - see `watchRootOptions`. */
  watchRoots?: WatchRootOption[]

  /** The server's sentence for why the last save was refused, shown inline above the fields - a
   *  toast vanishes while the person is still reading the field it is about. */
  refusal?: string

  /** Lists the folder the way the runner will. Absent, the Test button is not shown. */
  testFolder?: ((request: FolderTestRequest) => Promise<FolderTestResult>) | null
}>(), { watchRoots: () => [], refusal: '', testFolder: null })

const emit = defineEmits<{ save: [TriggerDraft] }>()

const draft = ref<TriggerDraft>(draftForCreate(props.container))

const editing = computed(() => props.trigger !== null)

const kindOptions = [
  { label: 'Every N', value: 'every' },
  { label: 'Cron', value: 'cron' },
  { label: 'Once', value: 'once' },
  { label: 'Event', value: 'event' },
  { label: 'Folder change', value: 'folderChange' },
] as const

/** The row's own root is kept even when the server no longer offers it - see `watchRootOptions`. */
const rootOptions = computed(() => watchRootOptions(props.watchRoots, props.trigger?.watchRoot))

const everyUnits = ['seconds', 'minutes', 'hours', 'days', 'weeks'] as const

const filterOperatorOptions = [
  { label: 'is', value: 'eq' },
  { label: 'contains', value: 'contains' },
] as const

/**
 * The definition behind the chosen event type, or null before one is picked (or for a type the
 * catalog no longer declares). Drives both the filter field picker and the sentence rendering -
 * one lookup, never a second copy of the catalog kept here.
 */
const selectedDefinition = computed<EventDefinition | null>(() => {
  const type = draft.value.eventType
  if (!type) return null
  return props.events.find((entry) => entry.type === type) ?? null
})

/** `file.changed`'s definition, for a folder trigger's instruction tokens. */
const fileChangedDefinition = computed<EventDefinition | null>(
  () => props.events.find((entry) => entry.type === FileChangedEventType) ?? null,
)

/**
 * The event-type picker's options: grouped by category, coded first and described second - see
 * `eventTypeSelectOptions` for the shape and why a flat list with disabled header rows is what
 * keeps keyboard navigation working with no native `QSelect` option-group support.
 */
const eventOptions = computed(() => eventTypeSelectOptions(props.events))

/**
 * The description shown BELOW the field once a type is picked - the code lives in the field itself
 * (`option-label` reads the raw type), so this is what keeps the description visible after
 * selection and not just in the open dropdown. Falls back to the generic hint before anything is
 * chosen, or for a type the catalog no longer declares.
 */
const eventTypeHint = computed(() =>
  selectedDefinition.value?.summary ?? 'What has to happen for this to fire.',
)

const fieldOptions = computed(() =>
  (selectedDefinition.value?.fields ?? []).map((field) => {
    const name = field.kind === 'List' ? `${field.name} (list)` : field.name
    return { label: field.summary ? `${name} — ${field.summary}` : name, value: field.name }
  }),
)

/**
 * A `List` field is matched as its raw JSON text (`["inbox/a.pdf","inbox/b.pdf"]`), so `is` could
 * only ever match the whole array spelled exactly. `contains` is the one operator that means
 * something there - "one of the paths contains this" - so it is the only one offered.
 */
function isListField(name: string): boolean {
  return (selectedDefinition.value?.fields ?? []).some((field) => field.name === name && field.kind === 'List')
}

const operatorOptions = computed(() =>
  isListField(filterParts.value.field)
    ? filterOperatorOptions.filter((option) => option.value === 'contains')
    : filterOperatorOptions,
)

/**
 * `draft.filter` is ONE string, `field op value` - the wire shape `lib/triggers.ts` validates and
 * sends. This is the read/decompose half; `setFilterPart` below is the write/compose half. Neither
 * is a second store of the filter - both read and write through `draft.value.filter` alone, so
 * there is nothing for this split to leave out of sync.
 */
const filterParts = computed(() => {
  const trimmed = draft.value.filter.trim()
  if (trimmed.length === 0) return { field: '', op: 'eq' as const, value: '' }

  const [field = '', rawOp = '', ...rest] = trimmed.split(/\s+/)
  const op = rawOp.toLowerCase() === 'contains' ? ('contains' as const) : ('eq' as const)
  return { field, op, value: rest.join(' ') }
})

function setFilterPart(part: Partial<{ field: string; op: 'eq' | 'contains'; value: string }>) {
  const current = filterParts.value
  const field = (part.field ?? current.field).trim()
  const op = isListField(field) ? 'contains' : (part.op ?? current.op)
  const value = part.value ?? current.value

  // BOTH BLANK MEANS "no filter" - every message of this event type wakes the member. Anything
  // else is composed even when incomplete, so a field chosen with no value yet reads back as a
  // malformed filter rather than silently vanishing - the Value field's rule is what surfaces that.
  draft.value.filter = field.length === 0 && value.trim().length === 0 ? '' : `${field} ${op} ${value}`
}

/**
 * Filled on every OPENING rather than at mount, because the parent keeps one instance and retargets
 * it - a form populated at construction would show whichever row was pressed first for the rest of
 * the session. The same rule `AgentEditDialog` follows, and `immediate` covers the parent mounting
 * this behind a `v-if` and setting `open` in the same tick.
 */
watch(
  open,
  (showing) => {
    if (!showing) return

    draft.value = props.trigger
      ? draftFromTrigger(props.trigger)
      : draftForCreate(props.container)
    folderTest.value = null
  },
  { immediate: true },
)

/**
 * Why this cannot be saved, or null when it can.
 *
 * ONE call, into `lib/triggers`, rather than a completeness check here and a shape check there.
 * Combining them in the component is what disabled Save permanently: `validateSchedule` answers
 * `string | null`, this compared it against `''`, and `null === ''` is false.
 */
const problem = computed(() => triggerDraftProblem(draft.value))

/**
 * A field's rule: the draft with the field's value in it, asked of `triggerFieldProblems`, which is
 * the same rule `problem` reads - so what is shown under a field and what disables Save cannot
 * disagree. Each field says only its own problem, and only once it has been left (`lazy-rules`):
 * a brand-new draft is incomplete by definition, and greeting it in red is nagging.
 */
function fieldRule(field: TriggerField, patch: (value: unknown) => Partial<TriggerDraft>): Rule {
  return (value) => triggerFieldProblems({ ...draft.value, ...patch(value) })[field] ?? true
}

const asText = (value: unknown) => (value === null || value === undefined ? '' : String(value))

/** A number box yields '' when cleared; NaN is what `triggerFieldProblems` refuses for that. */
const asCount = (value: unknown) => (value === '' || value === null || value === undefined ? NaN : Number(value))

const nameRules = [fieldRule('name', (value) => ({ name: asText(value) }))]
const instructionRules = [fieldRule('instruction', (value) => ({ instruction: asText(value) }))]
const everyCountRules = [positiveInt, fieldRule('everyCount', (value) => ({ everyCount: asCount(value) }))]
const cronExpressionRules = [fieldRule('cronExpression', (value) => ({ cronExpression: asText(value) }))]
const cronTimezoneRules = [timezone, fieldRule('cronTimezone', (value) => ({ cronTimezone: asText(value) }))]
const fireAtRules = [fieldRule('fireAt', (value) => ({ fireAt: asText(value) }))]
const startsAtRules = [fieldRule('startsAt', (value) => ({ startsAt: asText(value) }))]
const eventTypeRules = [fieldRule('eventType', (value) => ({ eventType: asText(value) }))]
const watchRootRules = [fieldRule('watchRoot', (value) => ({ watchRoot: asText(value) }))]
const watchPathRules = [fieldRule('watchPath', (value) => ({ watchPath: asText(value) }))]
const pollSecondsRules = [fieldRule('pollSeconds', (value) => ({ pollSeconds: asCount(value) }))]
const quietSecondsRules = [fieldRule('quietSeconds', (value) => ({ quietSeconds: asCount(value) }))]
const minIntervalSecondsRules = [
  fieldRule('minIntervalSeconds', (value) => ({ minIntervalSeconds: asCount(value) })),
]

/** The filter is composed from three boxes into `draft.filter` before this runs, so it reads that. */
const filterRules: Rule[] = [() => triggerFieldProblems(draft.value).filter ?? true]

/**
 * `problem` plus the two rules from `lib/rules` that are stricter than `lib/triggers`: a whole
 * count, and a timezone the SERVER can look up (the browser also accepts raw offsets).
 */
const valid = computed(
  () =>
    problem.value === null &&
    (draft.value.mode !== 'every' || positiveInt(draft.value.everyCount) === true) &&
    (draft.value.mode !== 'cron' || timezone(draft.value.cronTimezone) === true),
)

function submit() {
  if (!valid.value || props.saving) return

  emit('save', draft.value)
}

// Only the schedule's own fields decide the preview - an empty name does not hide it.
const cronPreview = computed(() => cronPreviewForDraft(draft.value, 5))

/** The schedule's start as a Date, or null for "now" - read by both previews. */
const startsAtDate = computed(() => {
  const iso = fromDateTimeLocalValue(draft.value.startsAt ?? '')
  return iso === null ? null : new Date(iso)
})

const cronBuilderOpen = ref(false)

function useBuiltCron(expression: string) {
  draft.value.cronExpression = expression
}

/**
 * What `TokenPicker` offers for the instruction box.
 *
 * `undefined` for every non-event kind, which leaves `TokenPicker` on its own default - the
 * general tokens a member's prompt already resolves. An event trigger gets a DIFFERENT, narrower
 * list instead of that default: the fields the chosen event type actually carries, each wrapped as
 * `{event.<field>}` - the one substitution `PromptTokens` resolves from the waking message's own
 * payload. Offering a token that cannot resolve is worse than offering none; see `TokenPicker`'s
 * own comment about a Concierge and `{member}`.
 */
const instructionTokens = computed<string[] | undefined>(() => {
  // A folder trigger wakes its member through `file.changed`, so the same `{event.<field>}` tokens
  // resolve - from that event's catalog entry, never a list kept here.
  if (draft.value.mode === 'folderChange') {
    return (fileChangedDefinition.value?.fields ?? []).map((field) => `{event.${field.name}}`)
  }
  if (draft.value.mode !== 'event') return undefined
  return (selectedDefinition.value?.fields ?? []).map((field) => `{event.${field.name}}`)
})

/**
 * "Test this folder": what the runner would see, and how long listing it took.
 *
 * `failure` is for a call that did not answer at all (the network, an unknown team); a folder the
 * server will not watch is an ANSWER - `ok: false` with `refusal` - and is shown the same way, in
 * the server's own sentence.
 */
const folderTest = ref<
  | { state: 'running' }
  | { state: 'done'; result: FolderTestResult }
  | { state: 'failed'; message: string }
  | null
>(null)

/** How many of the listed files the dialog shows; the server sends up to 200. */
const FolderTestShown = 10

/** A result is about the folder it was asked for. Changing root, path or glob makes it stale. */
watch(
  () => [draft.value.watchRoot, draft.value.watchPath, draft.value.watchGlob],
  () => {
    if (folderTest.value?.state !== 'running') folderTest.value = null
  },
)

const canTestFolder = computed(
  () =>
    props.testFolder !== null &&
    folderTest.value?.state !== 'running' &&
    triggerFieldProblems(draft.value).watchRoot === undefined &&
    triggerFieldProblems(draft.value).watchPath === undefined,
)

async function runFolderTest() {
  const test = props.testFolder
  if (!test || !canTestFolder.value) return

  folderTest.value = { state: 'running' }

  try {
    const result = await test({
      watchRoot: draft.value.watchRoot.trim(),
      watchPath: draft.value.watchPath.trim(),
      watchGlob: draft.value.watchGlob.trim() || null,
    })
    folderTest.value = { state: 'done', result }
  } catch (cause) {
    folderTest.value = { state: 'failed', message: cause instanceof Error ? cause.message : String(cause) }
  }
}

/**
 * The instruction textarea, reached through Quasar's own ref so a token can be inserted at the
 * cursor - same mechanism `MemberSettingsDialog` uses for its own prompt field.
 */
const instructionInput = ref<{ getNativeElement: () => HTMLTextAreaElement } | null>(null)

function insertToken(token: string) {
  const el = instructionInput.value?.getNativeElement()
  const start = el?.selectionStart ?? draft.value.instruction.length
  const end = el?.selectionEnd ?? draft.value.instruction.length

  draft.value.instruction =
    draft.value.instruction.slice(0, start) + token + draft.value.instruction.slice(end)

  void nextTick(() => {
    if (!el) return

    const cursor = start + token.length
    el.focus()
    el.setSelectionRange(cursor, cursor)
  })
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="trigger-edit-card os-dialog-md">
      <q-form @submit="submit">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">{{ editing ? 'Edit trigger' : 'New trigger' }}</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="saving" />
      </q-card-section>

      <q-card-section class="q-gutter-md q-pt-md">
        <!-- The server's refusal of the last save, in its own words. Inline and not a toast: the
             sentence usually names a field ("That path leaves the team's documents folder."), and
             it has to still be there while the person fixes that field. -->
        <q-banner
          v-if="refusal"
          dense
          rounded
          class="bg-negative text-white trigger-refusal"
          role="alert"
        >
          {{ refusal }}
        </q-banner>

        <q-input
          v-model="draft.name"
          outlined
          dense
          label="Name"
          autofocus
          lazy-rules
          :rules="nameRules"
        />

        <!-- `popup-content-class`, here and on every other select in this dialog: a QSelect's
             dropdown teleports to <body> SEPARATELY from the dialog it sits in, so without it a
             popup can paint behind the dialog - which reads as "the dropdown only has one option"
             and sends you looking at the data rather than the stacking. See `app.scss`. -->
        <q-select
          v-model="draft.mode"
          outlined
          dense
          emit-value
          map-options
          :options="kindOptions"
          label="Kind"
          popup-content-class="triggers-popup"
        />

        <!-- `q-gutter-sm`, NEVER `q-col-gutter-sm`. The col- variant spaces children with PADDING
             and puts a negative margin on the row, so the row overhangs its container and every
             child rides the gutter's height - which reads as "nearly right" and sends you looking
             at field heights. AGENTS.md records the same trap costing a misaligned button in
             ProfileDialog. Margin does not lose that fight. -->
        <div v-if="draft.mode === 'every'" class="row q-gutter-sm no-wrap items-start">
          <q-input
            v-model.number="draft.everyCount"
            type="number"
            min="1"
            outlined
            dense
            label="Every"
            lazy-rules
            :rules="everyCountRules"
            class="col-4"
          />
          <q-select
            v-model="draft.everyUnit"
            outlined
            dense
            :options="everyUnits"
            label="Unit"
            class="col"
            popup-content-class="triggers-popup"
          />
        </div>

        <template v-else-if="draft.mode === 'cron'">
          <q-input
            v-model="draft.cronExpression"
            outlined
            dense
            label="Cron expression"
            hint="Six fields, seconds first. Build… writes one for you."
            lazy-rules
            :rules="cronExpressionRules"
          >
            <template #append>
              <q-btn flat dense no-caps icon="tune" label="Build…" @click="cronBuilderOpen = true" />
            </template>
          </q-input>
          <q-input
            v-model="draft.cronTimezone"
            outlined
            dense
            label="Timezone (IANA)"
            lazy-rules
            :rules="cronTimezoneRules"
          />

          <div>
            <div class="text-caption os-text-muted q-mb-xs">Next 5 occurrences</div>
            <q-list dense bordered class="rounded-borders">
              <q-item v-if="cronPreview.length === 0">
                <q-item-section class="os-text-muted">No preview available.</q-item-section>
              </q-item>
              <q-item v-for="(line, index) in cronPreview" v-else :key="index">
                <q-item-section>{{ line }}</q-item-section>
              </q-item>
            </q-list>
          </div>
        </template>

        <q-input
          v-else-if="draft.mode === 'once'"
          v-model="draft.fireAt"
          outlined
          dense
          type="datetime-local"
          label="Fire at"
          stack-label
          lazy-rules
          :rules="fireAtRules"
        />

        <template v-else-if="draft.mode === 'folderChange'">
          <q-select
            v-model="draft.watchRoot"
            outlined
            dense
            emit-value
            map-options
            :options="rootOptions"
            label="Root"
            lazy-rules
            :rules="watchRootRules"
            hint="The team's documents, or a file-browser root the operator allowed watching."
            popup-content-class="triggers-popup"
          />
          <q-input
            v-model="draft.watchPath"
            outlined
            dense
            label="Path"
            lazy-rules
            :rules="watchPathRules"
            hint="A folder inside that root, like inbox/scans. Blank watches the root itself."
          />
          <q-input
            v-model="draft.watchGlob"
            outlined
            dense
            label="Glob (optional)"
            hint="*.pdf matches file names; in/**/*.csv matches paths. Blank is every file."
          />

          <!-- `q-gutter-sm`, never `q-col-gutter-sm` - see the Every row above. -->
          <div class="row q-gutter-sm no-wrap items-start">
            <q-input
              v-model.number="draft.pollSeconds"
              type="number"
              :min="MinimumPollSeconds"
              outlined
              dense
              label="Poll every (s)"
              lazy-rules
              :rules="pollSecondsRules"
              :hint="`At least ${MinimumPollSeconds}.`"
              class="col"
            />
            <q-input
              v-model.number="draft.quietSeconds"
              type="number"
              min="0"
              outlined
              dense
              label="Quiet period (s)"
              lazy-rules
              :rules="quietSecondsRules"
              hint="Held still this long first."
              class="col"
            />
            <q-input
              v-model.number="draft.minIntervalSeconds"
              type="number"
              min="0"
              outlined
              dense
              label="Minimum interval (s)"
              lazy-rules
              :rules="minIntervalSecondsRules"
              hint="Between two fires."
              class="col"
            />
          </div>

          <div v-if="testFolder" class="folder-test">
            <q-btn
              outline
              no-caps
              icon="folder_open"
              label="Test this folder"
              :loading="folderTest?.state === 'running'"
              :disable="!canTestFolder"
              @click="runFolderTest"
            />

            <div v-if="folderTest?.state === 'failed'" class="text-negative q-mt-sm folder-test-result" role="alert">
              {{ folderTest.message }}
            </div>

            <template v-else-if="folderTest?.state === 'done'">
              <div
                v-if="!folderTest.result.ok"
                class="text-negative q-mt-sm folder-test-result"
                role="alert"
              >
                {{ folderTest.result.refusal ?? 'That folder cannot be watched.' }}
              </div>

              <div v-else class="q-mt-sm folder-test-result">
                <div>
                  Sees {{ folderTest.result.count }} file{{ folderTest.result.count === 1 ? '' : 's' }}
                  in <span class="mono">{{ folderTest.result.folder }}</span>. Listing took
                  {{ formatPollDuration(folderTest.result.elapsedMs) }}.
                </div>
                <q-list
                  v-if="folderTest.result.entries.length > 0"
                  dense
                  bordered
                  class="rounded-borders q-mt-xs"
                >
                  <q-item v-for="entry in folderTest.result.entries.slice(0, FolderTestShown)" :key="entry.path">
                    <q-item-section class="mono ellipsis">{{ entry.path }}</q-item-section>
                  </q-item>
                </q-list>
                <div
                  v-if="folderTest.result.count > Math.min(folderTest.result.entries.length, FolderTestShown)"
                  class="text-caption os-text-muted q-mt-xs"
                >
                  and {{ folderTest.result.count - Math.min(folderTest.result.entries.length, FolderTestShown) }} more.
                </div>
              </div>
            </template>
          </div>
        </template>

        <template v-else>
          <!-- Grouped by category with a flat option list carrying disabled, non-selectable header
               rows - QSelect has no native option-group. A flat list is the usual Quasar
               workaround, and it is the one that keeps KEYBOARD NAVIGATION working: QSelect's own
               arrow-key handling already skips any option with `disable: true`, so a header is
               unreachable by arrowing through the list without a second mechanism. `option-disable`
               also blocks a click and a screen reader's activation, so it cannot be "selected" by
               any input method - see `eventTypeSelectOptions` in lib/triggers.ts.

               The CODE is the primary label (`option-label` reads `label`, which
               `eventTypeSelectOptions` sets to the raw type) so it is what shows once collapsed,
               not just inside the open dropdown; the two-line `option` slot below adds the
               description in the open list, and `eventTypeHint` carries it below the field once
               collapsed too. -->
          <q-select
            v-model="draft.eventType"
            outlined
            dense
            emit-value
            map-options
            option-disable="disable"
            :options="eventOptions"
            label="Event type"
            lazy-rules
            :rules="eventTypeRules"
            :hint="eventTypeHint"
            popup-content-class="triggers-popup"
          >
            <template #option="scope">
              <q-item
                v-bind="scope.itemProps"
                :class="{ 'event-type-group-header': scope.opt.isHeader }"
              >
                <q-item-section>
                  <q-item-label
                    v-if="scope.opt.isHeader"
                    caption
                    class="text-weight-bold text-uppercase"
                  >
                    {{ scope.opt.label }}
                  </q-item-label>
                  <template v-else>
                    <q-item-label class="mono">{{ scope.opt.label }}</q-item-label>
                    <q-item-label caption>{{ scope.opt.caption }}</q-item-label>
                  </template>
                </q-item-section>
              </q-item>
            </template>
          </q-select>

          <!-- Shown only once an event type is chosen - `fieldOptions` is empty before then, and a
               field select with nothing in it reads exactly like the bug `popup-content-class`
               exists to prevent: expanded, with nothing under it. -->
          <div v-if="draft.eventType" class="row q-gutter-sm no-wrap items-start">
            <q-select
              :model-value="filterParts.field"
              outlined
              dense
              emit-value
              map-options
              :options="fieldOptions"
              label="Field"
              class="col-4"
              popup-content-class="triggers-popup"
              @update:model-value="(value) => setFilterPart({ field: value })"
            />
            <q-select
              :model-value="filterParts.op"
              outlined
              dense
              emit-value
              map-options
              :options="operatorOptions"
              label="Op"
              class="col-3"
              popup-content-class="triggers-popup"
              @update:model-value="(value) => setFilterPart({ op: value })"
            />
            <q-input
              :model-value="filterParts.value"
              outlined
              dense
              label="Value"
              class="col"
              lazy-rules
              :rules="filterRules"
              hint="Blank matches every message of this event type."
              @update:model-value="(value) => setFilterPart({ value: String(value ?? '') })"
            />
          </div>
        </template>

        <!-- A cron or every-N schedule's start: it never fires before this, and the loop begins
             once it is reached. Blank starts now. Sent as `fireAt`. -->
        <q-input
          v-if="draft.mode === 'every' || draft.mode === 'cron'"
          v-model="draft.startsAt"
          outlined
          dense
          clearable
          type="datetime-local"
          label="Start at (optional)"
          stack-label
          hint="It never fires before this; blank starts now."
          lazy-rules
          :rules="startsAtRules"
        />

        <CronBuilderDialog
          v-model="cronBuilderOpen"
          :timezone="draft.cronTimezone"
          :starts-at="startsAtDate"
          @use="useBuiltCron"
        />

        <div>
          <div class="text-caption os-text-muted q-mb-xs">Instruction</div>
          <!-- Two branches rather than one `:tokens="instructionTokens"` binding: the prop is
               optional so `TokenPicker` can fall back to its own default list, and
               `exactOptionalPropertyTypes` treats an explicit `undefined` differently from the
               attribute being absent - passing `instructionTokens` straight through when it is
               `undefined` does not fall back, it fails the build. -->
          <TokenPicker v-if="instructionTokens" class="q-mb-xs" :tokens="instructionTokens" @insert="insertToken" />
          <TokenPicker v-else class="q-mb-xs" @insert="insertToken" />
          <q-input
            ref="instructionInput"
            v-model="draft.instruction"
            outlined
            dense
            type="textarea"
            autogrow
            lazy-rules
            :rules="instructionRules"
            hint="What the member is told when this fires."
          />
        </div>

        <!-- A column, because a checkbox is inline-flex and two of them on one row put the second
             one's label a long way from the box it belongs to on a narrow dialog. -->
        <div class="column items-start q-gutter-xs">
          <q-checkbox v-model="draft.idleOnly" dense label="Only when the member is idle" />
          <q-checkbox v-model="draft.enabled" dense label="Enabled" />
        </div>

      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat label="Cancel" :disable="saving" />
        <q-btn
          type="submit"
          color="primary"
          label="Save"
          :loading="saving"
          :disable="!valid || saving"
        />
      </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* The event-type picker's category header rows - non-selectable (`option-disable`), so this is
   styling only: smaller, dimmer and not the pointer cursor a selectable row gets, which is what
   tells a reader "this is a label, not a choice" before they ever try clicking it. */
.event-type-group-header {
  min-height: 28px;
  cursor: default;
  opacity: 0.7;
}
</style>
