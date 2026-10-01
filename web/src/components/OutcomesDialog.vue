<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import {
  createOutcome,
  editOutcome,
  getOutcome,
  linkWorkflowOutcome,
  listOutcomes,
  mergeOutcome,
  previewMerge,
  rejectOutcome,
  transitionOutcome,
  type MergePreview,
  type Outcome,
  type OutcomeDetail,
  type OutcomeEvent,
  type OutcomeFields,
  type OutcomeFigures,
  type OutcomeList,
  type OutcomeTransition,
  type OutcomeWorkflowLine,
} from '../api/outcomes';
import {
  LinkHowLabel,
  PeriodOptions,
  StatusLabel,
  accountingSinceText,
  actionsFor,
  duration,
  elapsedText,
  firstLine,
  matchesFilter,
  mergePreviewText,
  periodRange,
  reachesBeforeLedger,
  teamsText,
  tokensFigure,
  unmeasuredText,
  when,
  workflowsText,
  type Period,
} from '../lib/outcomes';
import { vResizableColumns } from '../lib/resizableColumns';

/**
 * MANAGE OUTCOMES (B0023): every outcome with the figures of the work that served it, and one
 * outcome opened - its details, its status actions, its workflows and its link history.
 *
 * EVERY FIGURE IS THE ROUTE'S. `GET /api/outcomes` answers the list's figures for the period and
 * `GET /api/outcomes/{id}` an outcome's workflows; this dialog formats them (`lib/outcomes.ts`) and
 * adds nothing up. Agent time is the route's sum over runs and is labelled as one; elapsed is the
 * median and longest workflow and never summed; an unmeasured run is counted beside the tokens and
 * never shown as 0.
 *
 * NAMES AND DESCRIPTIONS ARE TEXT. An outcome's name may be a Manager's proposal or a solution
 * package's; every one is bound with mustaches, never `v-html`.
 *
 * Opened from the Kanban filter bar, from Admin → Outcomes, and from a card's outcome tag - that
 * last one with `outcome`, which opens the dialog at that outcome.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  /** An outcome id to open at, or null for the list. */
  outcome?: string | null;
}>();

type Tab = 'active' | 'proposed' | 'ended';

const tab = ref<Tab>('active');
const period = ref<Period>({ kind: 'all', customFrom: '', customTo: '' });
const filterText = ref('');

const list = ref<OutcomeList | null>(null);
const loading = ref(false);
const error = ref('');

const range = computed(() => periodRange(period.value));

async function load() {
  loading.value = true;
  error.value = '';

  try {
    list.value = await listOutcomes({
      status: ['proposed', 'active', 'retired', 'merged'],
      from: range.value.from,
      to: range.value.to,
    });
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

const outcomes = computed(() => list.value?.outcomes ?? []);

const proposedCount = computed(() => outcomes.value.filter((o) => o.status === 'proposed').length);

const shown = computed(() => {
  const statuses = tab.value === 'active' ? ['active'] : tab.value === 'proposed' ? ['proposed'] : ['retired', 'merged'];
  return outcomes.value.filter((o) => statuses.includes(o.status) && matchesFilter(o, filterText.value));
});

/** The note shows whenever the period reaches before the ledger began. */
const accountingNote = computed(() => {
  const started = list.value?.ledgerStartedAt ?? null;
  return started && reachesBeforeLedger(range.value.from, started) ? accountingSinceText(started) : '';
});

function outcomeName(id: string | null | undefined) {
  return outcomes.value.find((o) => o.id === id)?.name ?? id ?? '';
}

/** What a live outcome may be merged into, or a workflow moved to: active and proposed, never itself. */
function liveOptions(except: string | null) {
  return outcomes.value
    .filter((o) => (o.status === 'active' || o.status === 'proposed') && o.id !== except)
    .map((o) => ({ label: o.status === 'proposed' ? `${o.name} (proposed)` : o.name, value: o.id }));
}

watch(open, (showing) => {
  if (!showing) return;
  void load();
  if (props.outcome) void select(props.outcome);
  else closeDetail();
}, { immediate: true });

watch(() => props.outcome, (id) => {
  if (open.value && id) void select(id);
});

watch(() => [period.value.kind, period.value.customFrom, period.value.customTo], () => {
  if (open.value) void load();
});

// --- One outcome ------------------------------------------------------------------------------

const detail = ref<OutcomeDetail | null>(null);
const detailError = ref('');
const actionProblem = ref('');
const busy = ref(false);

const draft = ref<Required<OutcomeFields>>({ name: '', description: '', targetMetric: '', targetUnit: '', targetValue: '' });

async function select(id: string) {
  detailError.value = '';
  actionProblem.value = '';

  try {
    detail.value = await getOutcome(id);
    const o = detail.value.outcome;
    draft.value = {
      name: o.name,
      description: o.description ?? '',
      targetMetric: o.targetMetric ?? '',
      targetUnit: o.targetUnit ?? '',
      targetValue: o.targetValue ?? '',
    };
  } catch (cause) {
    detail.value = null;
    detailError.value = cause instanceof Error ? cause.message : String(cause);
  }
}

function closeDetail() {
  detail.value = null;
  detailError.value = '';
}

const current = computed(() => detail.value?.outcome ?? null);
const readOnly = computed(() => current.value?.status === 'merged');
const actions = computed(() => (current.value ? actionsFor(current.value.status, detail.value?.history.length ?? 0) : []));

/** Only the fields that changed are sent: absent leaves a field alone. */
const changes = computed<OutcomeFields>(() => {
  const o = current.value;
  if (!o) return {};
  const was: Required<OutcomeFields> = {
    name: o.name,
    description: o.description ?? '',
    targetMetric: o.targetMetric ?? '',
    targetUnit: o.targetUnit ?? '',
    targetValue: o.targetValue ?? '',
  };
  const edits: OutcomeFields = {};
  for (const key of Object.keys(was) as (keyof OutcomeFields)[]) {
    if (draft.value[key] !== was[key]) edits[key] = draft.value[key];
  }
  return edits;
});

const dirty = computed(() => Object.keys(changes.value).length > 0);

async function act(work: () => Promise<unknown>) {
  const o = current.value;
  if (!o || busy.value) return;

  busy.value = true;
  actionProblem.value = '';

  try {
    await work();
    await Promise.all([load(), select(o.id)]);
  } catch (cause) {
    actionProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

const save = () => act(() => editOutcome(current.value!.id, changes.value));

const transition = (verb: OutcomeTransition) => act(() => transitionOutcome(current.value!.id, verb));

async function reject() {
  const o = current.value;
  if (!o || busy.value) return;

  busy.value = true;
  actionProblem.value = '';

  try {
    await rejectOutcome(o.id);
    closeDetail();
    await load();
  } catch (cause) {
    actionProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

// --- Merge into…, which shows the route's preview first -----------------------------------------

const merging = ref(false);
const mergeInto = ref<string | null>(null);
const preview = ref<MergePreview | null>(null);
const mergeProblem = ref('');

function askMerge() {
  merging.value = true;
  mergeInto.value = null;
  preview.value = null;
  mergeProblem.value = '';
}

watch(mergeInto, async (into) => {
  preview.value = null;
  mergeProblem.value = '';
  if (!into || !current.value) return;

  try {
    preview.value = await previewMerge(current.value.id, into);
  } catch (cause) {
    mergeProblem.value = cause instanceof Error ? cause.message : String(cause);
  }
});

async function confirmMerge() {
  const into = mergeInto.value;
  if (!into || !preview.value || preview.value.refusal) return;

  await act(() => mergeOutcome(current.value!.id, into));
  if (!actionProblem.value) merging.value = false;
}

// --- Move a workflow to another outcome -------------------------------------------------------

const moving = ref<OutcomeWorkflowLine | null>(null);
const moveTo = ref<string | null>(null);

function askMove(line: OutcomeWorkflowLine) {
  moving.value = line;
  moveTo.value = null;
  actionProblem.value = '';
}

async function confirmMove() {
  const line = moving.value;
  const to = moveTo.value;
  if (!line || !to || !line.team) return;

  await act(() => linkWorkflowOutcome(line.team!.id, line.correlation, to));
  if (!actionProblem.value) moving.value = null;
}

// --- New outcome ------------------------------------------------------------------------------

const creating = ref(false);
const newName = ref('');
const newDescription = ref('');
const createProblem = ref('');

function askCreate() {
  creating.value = true;
  newName.value = '';
  newDescription.value = '';
  createProblem.value = '';
}

async function confirmCreate() {
  if (!newName.value.trim() || busy.value) return;

  busy.value = true;
  createProblem.value = '';

  try {
    const made = await createOutcome({ name: newName.value.trim(), description: newDescription.value.trim() });
    creating.value = false;
    tab.value = 'active';
    await load();
    await select(made.id);
  } catch (cause) {
    createProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

// --- History ----------------------------------------------------------------------------------

interface HistoryLine {
  key: string;
  at: string;
  text: string;
}

/** A tenant row as a person reads it. Names are interpolated as text, never as HTML. */
function eventText(e: OutcomeEvent): string {
  const name = e.name ?? 'an outcome';
  switch (e.action) {
    case 'outcome.created':
      return `Created as "${name}"`;
    case 'outcome.renamed':
      return e.from ? `Renamed from "${e.from}" to "${name}"` : `Renamed to "${name}"`;
    case 'outcome.changed':
      return 'Description or target changed';
    case 'outcome.confirmed':
      return 'Confirmed';
    case 'outcome.retired':
      return 'Retired';
    case 'outcome.reactivated':
      return 'Reactivated';
    case 'outcome.rejected':
      return 'Rejected';
    case 'outcome.merged': {
      const into = typeof e.detail?.intoName === 'string' ? e.detail.intoName : outcomeName(String(e.detail?.into ?? ''));
      return `"${name}" merged into "${into}"`;
    }
    default:
      return e.action;
  }
}

/** What the route answers, oldest first: every act on the outcome (and on one merged into it) and
 *  every link, each with who and when. */
const history = computed<HistoryLine[]>(() => {
  const d = detail.value;
  if (!d) return [];

  const o = d.outcome;
  const lines: HistoryLine[] = [];
  for (const e of d.events) {
    const other = e.outcomeId !== o.id && e.action !== 'outcome.merged' ? ` (on "${e.name ?? ''}", merged in since)` : '';
    lines.push({ key: `event-${e.seq}`, at: e.at, text: `${eventText(e)}${other} by ${e.by ?? 'unknown'}` });
  }
  for (const link of d.history) {
    const team = link.teamNameAtLink ?? link.teamId ?? 'no team';
    lines.push({
      key: `link-${link.id}`,
      at: link.setAt,
      text: `Workflow ${link.correlation} (${team}) linked to "${link.outcomeNameAtLink}" by ${link.setBy} (${LinkHowLabel[link.how] ?? link.how})`,
    });
  }
  return lines.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
});

function tokensCell(figures: OutcomeFigures) {
  return tokensFigure(figures.tokens);
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-xl" data-outcomes-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Outcomes</div>
        <q-space />
        <q-btn flat dense no-caps icon="refresh" label="Refresh" :loading="loading" @click="load" />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <!-- ONE OUTCOME OPENED -->
      <q-card-section v-if="detail && current" data-outcome-detail :data-outcome-id="current.id">
        <q-btn flat dense no-caps icon="arrow_back" label="All outcomes" data-outcome-back @click="closeDetail" />

        <div class="row items-center q-gutter-sm q-mt-sm">
          <div class="text-h6" data-outcome-title>{{ current.name }}</div>
          <q-badge outline :label="StatusLabel[current.status]" data-outcome-status />
        </div>

        <div v-if="readOnly" class="os-body os-text-muted q-mt-sm" data-outcome-merged>
          Merged into {{ outcomeName(current.mergedInto) }}. Its workflows and figures count there now; it is read-only.
        </div>

        <div class="text-subtitle2 q-mt-md">Details</div>
        <div class="outcome-fields">
          <q-input v-model="draft.name" dense outlined label="Name" :readonly="readOnly" data-outcome-field="name" />
          <q-input
            v-model="draft.description"
            dense
            outlined
            autogrow
            type="textarea"
            label="Description"
            :readonly="readOnly"
            data-outcome-field="description"
          />
          <div class="row q-col-gutter-sm">
            <q-input v-model="draft.targetMetric" class="col" dense outlined label="Target metric" :readonly="readOnly" />
            <q-input v-model="draft.targetUnit" class="col" dense outlined label="Unit" :readonly="readOnly" />
            <q-input v-model="draft.targetValue" class="col" dense outlined label="Target" :readonly="readOnly" />
          </div>
          <div class="os-body os-text-muted" data-outcome-target-note>
            The target is text a person keeps; nothing measures it yet.
          </div>
        </div>

        <div class="row items-center q-gutter-sm q-mt-sm">
          <q-btn
            v-if="!readOnly"
            unelevated
            no-caps
            color="primary"
            label="Save"
            :disable="!dirty"
            :loading="busy"
            data-outcome-save
            @click="save"
          />
          <q-btn v-if="actions.includes('confirm')" outline no-caps label="Confirm" data-outcome-action="confirm" @click="transition('confirm')" />
          <q-btn v-if="actions.includes('merge')" outline no-caps label="Merge into…" data-outcome-action="merge" @click="askMerge" />
          <q-btn v-if="actions.includes('retire')" outline no-caps label="Retire" data-outcome-action="retire" @click="transition('retire')" />
          <q-btn
            v-if="actions.includes('reactivate')"
            outline
            no-caps
            label="Reactivate"
            data-outcome-action="reactivate"
            @click="transition('reactivate')"
          />
          <q-btn
            v-if="actions.includes('reject')"
            outline
            no-caps
            color="negative"
            label="Reject"
            data-outcome-action="reject"
            @click="reject"
          />
        </div>
        <div v-if="actionProblem" class="os-body text-negative q-mt-sm" data-outcome-problem>{{ actionProblem }}</div>

        <div class="text-subtitle2 q-mt-md">Workflows</div>
        <div v-if="detail.workflows.length === 0" class="os-body os-text-muted">No workflow serves this outcome yet.</div>
        <q-markup-table v-else v-resizable-columns="'outcome-workflows'" flat bordered dense separator="horizontal">
          <thead>
            <tr>
              <th class="text-left">Team</th>
              <th class="text-left">Workflow</th>
              <th class="text-left">State</th>
              <th class="text-left">Started</th>
              <th class="text-right">Elapsed</th>
              <th class="text-right">Agent time</th>
              <th class="text-right">Tokens</th>
              <th class="text-left">Linked</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr v-for="line in detail.workflows" :key="line.correlation" :data-outcome-workflow="line.correlation">
              <td class="text-left">{{ line.team ? (line.team.deleted ? `${line.team.name} (deleted)` : line.team.name) : '—' }}</td>
              <td class="text-left mono">{{ line.correlation }}</td>
              <td class="text-left">{{ line.state }}</td>
              <td class="text-left">{{ when(line.startedAt) }}</td>
              <td class="text-right">{{ line.elapsedSeconds === null ? '—' : duration(line.elapsedSeconds) }}</td>
              <td class="text-right">{{ duration(line.agentSeconds) }}</td>
              <td class="text-right">
                {{ line.billableTokens === 0 && line.unmeasuredRuns > 0 ? 'not measured' : line.billableTokens.toLocaleString() }}
                <div v-if="line.unmeasuredRuns > 0" class="text-caption os-text-muted">{{ unmeasuredText(line.unmeasuredRuns) }}</div>
              </td>
              <td class="text-left">
                {{ line.how ? (LinkHowLabel[line.how] ?? line.how) : '—' }}
                <div v-if="line.setBy" class="text-caption os-text-muted">{{ line.setBy }} · {{ when(line.setAt) }}</div>
              </td>
              <td class="text-right">
                <q-btn
                  v-if="!readOnly && line.team"
                  flat
                  dense
                  no-caps
                  label="Move to another outcome…"
                  data-outcome-move
                  @click="askMove(line)"
                />
              </td>
            </tr>
          </tbody>
        </q-markup-table>

        <div class="text-subtitle2 q-mt-md">History</div>
        <ul class="outcome-history" data-outcome-history>
          <li v-for="line in history" :key="line.key">
            <span class="os-text-muted">{{ when(line.at) }}</span>
            {{ line.text }}
          </li>
        </ul>
      </q-card-section>

      <q-card-section v-else-if="detailError">
        <q-btn flat dense no-caps icon="arrow_back" label="All outcomes" @click="closeDetail" />
        <div class="os-body text-negative q-mt-sm">Could not open the outcome: {{ detailError }}</div>
      </q-card-section>

      <!-- THE LIST -->
      <template v-else>
        <q-card-section class="row items-center q-gutter-sm">
          <q-select
            v-model="period.kind"
            :options="PeriodOptions"
            dense
            outlined
            emit-value
            map-options
            label="Period"
            class="outcome-period"
            data-outcome-period
          />
          <template v-if="period.kind === 'custom'">
            <q-input v-model="period.customFrom" dense outlined type="date" label="From" data-outcome-period-from />
            <q-input v-model="period.customTo" dense outlined type="date" label="To" data-outcome-period-to />
          </template>
          <q-input v-model="filterText" dense outlined clearable label="Filter" class="outcome-filter" data-outcome-filter>
            <template #prepend><q-icon name="search" /></template>
          </q-input>
          <q-space />
          <q-btn unelevated no-caps color="primary" icon="add" label="New outcome" data-outcome-new @click="askCreate" />
        </q-card-section>

        <q-card-section class="q-pt-none">
          <div v-if="accountingNote" class="os-body os-text-muted q-mb-sm" data-accounting-since>{{ accountingNote }}</div>
          <div class="text-caption os-text-muted q-mb-sm" data-outcome-figures-note>
            Agent time is the sum over runs, so runs in parallel can make it exceed elapsed. Elapsed is per
            workflow, median · longest, and never summed.
          </div>

          <q-tabs v-model="tab" dense no-caps align="left" active-color="primary" indicator-color="primary">
            <q-tab name="active" label="Active" data-outcome-tab="active" />
            <q-tab name="proposed" :label="`Proposed (${proposedCount})`" data-outcome-tab="proposed" />
            <q-tab name="ended" label="Retired & merged" data-outcome-tab="ended" />
          </q-tabs>

          <div v-if="error" class="os-body text-negative q-mt-sm">Could not list the outcomes: {{ error }}</div>

          <q-markup-table
            v-else
            v-resizable-columns="'outcomes'"
            flat
            bordered
            dense
            separator="horizontal"
            class="q-mt-sm"
            data-outcomes-table
          >
            <thead>
              <tr>
                <th class="text-left">Outcome</th>
                <th class="text-left">Teams</th>
                <th class="text-left">Workflows</th>
                <th class="text-right">Agent time</th>
                <th class="text-right">Waiting</th>
                <th class="text-right">Elapsed</th>
                <th class="text-right">Tokens</th>
                <th class="text-left">Last worked</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="o in shown"
                :key="o.id"
                class="outcome-row"
                :class="{ 'outcome-row--proposed': o.status === 'proposed' }"
                :data-outcome-row="o.id"
                @click="select(o.id)"
              >
                <td class="text-left">
                  <div class="text-weight-medium">{{ o.name }}</div>
                  <div class="text-caption os-text-muted">{{ firstLine(o.description) }}</div>
                  <div v-if="o.status === 'merged'" class="text-caption os-text-muted">merged into {{ outcomeName(o.mergedInto) }}</div>
                </td>
                <td class="text-left">{{ teamsText(o.figures.teams) }}</td>
                <td class="text-left">{{ workflowsText(o.figures.workflows) }}</td>
                <td class="text-right">{{ duration(o.figures.agentSeconds) }}</td>
                <td class="text-right">{{ duration(o.figures.waitingSeconds) }}</td>
                <td class="text-right">{{ elapsedText(o.figures.elapsed) }}</td>
                <td class="text-right" data-outcome-tokens>
                  {{ tokensCell(o.figures) }}
                  <div v-if="o.figures.tokens.unmeasuredRuns > 0" class="text-caption os-text-muted">
                    {{ unmeasuredText(o.figures.tokens.unmeasuredRuns) }}
                  </div>
                </td>
                <td class="text-left">{{ when(o.figures.lastWorkedAt) }}</td>
              </tr>
              <tr v-if="shown.length === 0 && tab !== 'active'">
                <td colspan="8" class="os-text-muted">No outcomes here.</td>
              </tr>
              <!-- ALWAYS SHOWN, closing Active: the work no outcome names, with the same figures. -->
              <tr v-if="tab === 'active' && list" class="outcome-row--none" data-no-outcome-row>
                <td class="text-left text-italic">{{ list.noOutcome.name }}</td>
                <td class="text-left">{{ teamsText(list.noOutcome.figures.teams) }}</td>
                <td class="text-left">{{ workflowsText(list.noOutcome.figures.workflows) }}</td>
                <td class="text-right">{{ duration(list.noOutcome.figures.agentSeconds) }}</td>
                <td class="text-right">{{ duration(list.noOutcome.figures.waitingSeconds) }}</td>
                <td class="text-right">{{ elapsedText(list.noOutcome.figures.elapsed) }}</td>
                <td class="text-right" data-outcome-tokens>
                  {{ tokensCell(list.noOutcome.figures) }}
                  <div v-if="list.noOutcome.figures.tokens.unmeasuredRuns > 0" class="text-caption os-text-muted">
                    {{ unmeasuredText(list.noOutcome.figures.tokens.unmeasuredRuns) }}
                  </div>
                </td>
                <td class="text-left">{{ when(list.noOutcome.figures.lastWorkedAt) }}</td>
              </tr>
            </tbody>
          </q-markup-table>
        </q-card-section>
      </template>
    </q-card>
  </q-dialog>

  <!-- MERGE INTO…: the route's preview first, then the merge. -->
  <q-dialog :model-value="merging" @update:model-value="(showing) => (merging = showing)">
    <q-card v-if="current" class="os-dialog-md" data-outcome-merge>
      <q-card-section>
        <div class="os-dialog-title">Merge {{ current.name }} into…</div>
        <q-select
          v-model="mergeInto"
          :options="liveOptions(current.id)"
          dense
          outlined
          emit-value
          map-options
          label="Outcome"
          class="q-mt-sm"
          data-outcome-merge-into
        />
        <div v-if="preview" class="os-body q-mt-sm" data-outcome-merge-preview>{{ mergePreviewText(preview) }}</div>
        <div v-if="preview?.refusal" class="os-body text-negative q-mt-sm">{{ preview.refusal }}</div>
        <div v-if="mergeProblem || actionProblem" class="os-body text-negative q-mt-sm">{{ mergeProblem || actionProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="merging = false" />
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Merge"
          :disable="!preview || !!preview.refusal"
          :loading="busy"
          data-outcome-merge-confirm
          @click="confirmMerge"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- MOVE TO ANOTHER OUTCOME…: a person's link, which appends a new link row. -->
  <q-dialog :model-value="moving !== null" @update:model-value="(showing) => showing || (moving = null)">
    <q-card v-if="moving" class="os-dialog-sm" data-outcome-move-dialog>
      <q-card-section>
        <div class="os-dialog-title">Move workflow {{ moving.correlation }}</div>
        <q-select
          v-model="moveTo"
          :options="liveOptions(current?.id ?? null)"
          dense
          outlined
          emit-value
          map-options
          label="To outcome"
          class="q-mt-sm"
          data-outcome-move-to
        />
        <div v-if="actionProblem" class="os-body text-negative q-mt-sm">{{ actionProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="moving = null" />
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Move"
          :disable="!moveTo"
          :loading="busy"
          data-outcome-move-confirm
          @click="confirmMove"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- NEW OUTCOME: a person's, active at once. -->
  <q-dialog :model-value="creating" @update:model-value="(showing) => (creating = showing)">
    <q-card class="os-dialog-sm" data-outcome-create>
      <q-card-section>
        <div class="os-dialog-title">New outcome</div>
        <div class="os-body os-text-muted q-mt-sm">Name the result the work produces, not the activity. It is active at once.</div>
        <q-input v-model="newName" dense outlined label="Name" class="q-mt-sm" />
        <q-input v-model="newDescription" dense outlined autogrow type="textarea" label="Description" class="q-mt-sm" />
        <div v-if="createProblem" class="os-body text-negative q-mt-sm">{{ createProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="creating = false" />
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Create"
          :disable="!newName.trim()"
          :loading="busy"
          data-outcome-create-confirm
          @click="confirmCreate"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.outcome-fields {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 40rem;
}

.outcome-period {
  min-width: 10rem;
}

.outcome-filter {
  min-width: 14rem;
}

.outcome-row {
  cursor: pointer;
}

/* A proposal is not yet a person's: dashed, as the card tag draws it. */
.outcome-row--proposed td:first-child {
  border-left: 2px dashed var(--os-rule);
}

.outcome-row--none {
  background: var(--os-chrome);
}

.outcome-history {
  margin: 0;
  padding-left: 1.2rem;
}
</style>
