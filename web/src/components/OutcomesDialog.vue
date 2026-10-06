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
  type OutcomeBacklogItem,
  type OutcomeBucket,
  type OutcomeDetail,
  type OutcomeEvent,
  type OutcomeFields,
  type OutcomeFigures,
  type OutcomeList,
  type OutcomeMoney,
  type OutcomeTransition,
  type OutcomeWorkflowLine,
} from '../api/outcomes';
import { saveTenantSettings } from '../api/client';
import {
  BucketLabel,
  LinkHowLabel,
  PeriodOptions,
  StatusLabel,
  accountingSinceText,
  actionsFor,
  agentHoursText,
  backlogSegments,
  duration,
  efficiencyText,
  firstLine,
  lineTokensFigure,
  matchesFilter,
  mergePreviewText,
  moneyText,
  periodRange,
  rateText,
  reachesBeforeLedger,
  shareOfValueText,
  tileSummaryText,
  unmeasuredText,
  valueAmount,
  weekHeights,
  weekText,
  when,
  type Period,
} from '../lib/outcomes';
import { itemLabel } from '../lib/backlog';
import { vResizableColumns } from '../lib/resizableColumns';
import BacklogDialog from './BacklogDialog.vue';
import DialogTabs from './DialogTabs.vue';

/**
 * OUTCOMES, AS A PRODUCT OWNER READS THEM: a tile per outcome - what it has cost against what it is
 * worth, how its spend moved over eight weeks, where its backlog stands and how much of its time
 * waited on a person - and one outcome opened, with its budget, its spend by week, its backlog by
 * where each item stands, and its workflows and history folded below.
 *
 * EVERY FIGURE IS THE ROUTE'S. `GET /api/outcomes` answers each tile's figures and `money` (the
 * currency, the agent hourly rate and how a declared item counts); this dialog formats them
 * (`lib/outcomes.ts`) and adds nothing up. A cost with no rate set is "not set", never $0; an
 * efficiency with no time is "—", never 0%; an unmeasured run is counted, never priced.
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

/**
 * `changed` after a person's write succeeds - a confirm, merge, retire, reject, edit or move - so
 * the board's Needs You can reread the proposed outcomes it lists.
 */
const emit = defineEmits<{ changed: [] }>();

type Tab = 'active' | 'proposed' | 'ended';

const tab = ref<Tab>('active');
const period = ref<Period>({ kind: 'all', customFrom: '', customTo: '' });
const filterText = ref<string | null>('');

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

const money = computed<OutcomeMoney>(() =>
  detail.value?.money ?? list.value?.money ?? { currency: 'USD', agentHourlyRate: null, declaredCountsAs: 'achieved' });

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

// --- A tile's figures, worded ------------------------------------------------------------------

function costText(figures: OutcomeFigures) {
  return moneyText(figures.cost.amount, figures.cost.currency);
}

function valueText(value: string | null) {
  const amount = valueAmount(value);
  return amount === null ? '' : moneyText(amount, money.value.currency);
}

/** "Awaiting you" only when a declared item is counted as in progress: otherwise it is achieved. */
function awaitingCount(figures: OutcomeFigures) {
  return money.value.declaredCountsAs === 'in-progress' ? figures.backlog.inProgress : 0;
}

// --- One outcome ------------------------------------------------------------------------------

const detail = ref<OutcomeDetail | null>(null);
const detailError = ref('');
const actionProblem = ref('');
const busy = ref(false);

type Draft = Required<Pick<OutcomeFields, 'name' | 'description' | 'value'>>;

const draft = ref<Draft>({ name: '', description: '', value: '' });

function draftOf(o: { name: string; description: string | null; value: string | null }): Draft {
  return { name: o.name, description: o.description ?? '', value: o.value ?? '' };
}

async function select(id: string) {
  detailError.value = '';
  actionProblem.value = '';

  try {
    detail.value = await getOutcome(id);
    draft.value = draftOf(detail.value.outcome);
  } catch (cause) {
    detail.value = null;
    detailError.value = cause instanceof Error ? cause.message : String(cause);
  }
}

function closeDetail() {
  detail.value = null;
  detailError.value = '';
  editing.value = false;
  showWorkflows.value = false;
  showHistory.value = false;
}

const current = computed(() => detail.value?.outcome ?? null);
const readOnly = computed(() => current.value?.status === 'merged');
// Reject is refused while a link names the outcome OR another outcome is merged into it, so
// both count as what holds it.
const actions = computed(() => (current.value
  ? actionsFor(current.value.status, (detail.value?.history.length ?? 0) + (detail.value?.mergedFrom.length ?? 0))
  : []));

/** Only the fields that changed are sent: absent leaves a field alone. */
const changes = computed<OutcomeFields>(() => {
  const o = current.value;
  if (!o) return {};
  const was = draftOf(o);
  const edits: OutcomeFields = {};
  for (const key of Object.keys(was) as (keyof Draft)[]) {
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
    emit('changed');
    await Promise.all([load(), select(o.id)]);
  } catch (cause) {
    actionProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

async function save() {
  await act(() => editOutcome(current.value!.id, changes.value));
  if (!actionProblem.value) editing.value = false;
}

const transition = (verb: OutcomeTransition) => act(() => transitionOutcome(current.value!.id, verb));

async function reject() {
  const o = current.value;
  if (!o || busy.value) return;

  busy.value = true;
  actionProblem.value = '';

  try {
    await rejectOutcome(o.id);
    emit('changed');
    closeDetail();
    await load();
  } catch (cause) {
    actionProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/** The opened outcome's backlog, one column per bucket, in the route's order. */
const columns = computed<{ key: OutcomeBucket; label: string; items: OutcomeBacklogItem[] }[]>(() =>
  (['notStarted', 'inProgress', 'achieved'] as const).map((key) => ({
    key,
    label: BucketLabel[key],
    items: (detail.value?.backlogItems ?? []).filter((i) => i.bucket === key),
  })));

function itemNote(item: OutcomeBacklogItem) {
  if (item.bucket === 'achieved') {
    const how = item.state === 'declared' ? 'declared' : 'implemented';
    return item.landedAt ? `${how} · landed ${when(item.landedAt)}` : how;
  }
  if (item.bucket === 'inProgress') {
    const team = item.teamName ?? item.team;
    if (item.state === 'declared') return team ? `declared by ${team} · awaiting you` : 'declared · awaiting you';
    return [team, item.workflow !== null ? `workflow ${item.workflow}` : null].filter(Boolean).join(' · ');
  }
  return item.state;
}

const showWorkflows = ref(false);
const showHistory = ref(false);

/** The outcome settings dialog: name, description and budget, from what is stored now. */
const editing = ref(false);

function askEdit() {
  if (!current.value) return;
  draft.value = draftOf(current.value);
  actionProblem.value = '';
  editing.value = true;
}

// --- The backlog, opened at one of this outcome's items ------------------------------------------

const backlogOpen = ref(false);
const backlogItem = ref<number | null>(null);

function openItem(item: OutcomeBacklogItem) {
  if (item.number === null) return;
  backlogItem.value = item.number;
  backlogOpen.value = true;
}

// --- The rate: the instance's currency, agent hourly rate and how a declared item counts ----------

const pricing = ref(false);
const rateDraft = ref({ currency: 'usd', rate: '', declared: 'achieved' as OutcomeMoney['declaredCountsAs'] });
const rateProblem = ref('');

const CurrencyOptions = ['USD', 'EUR', 'GBP', 'CAD', 'AUD', 'NZD', 'CHF', 'JPY', 'CNY', 'INR', 'SEK', 'NOK', 'DKK', 'PLN', 'BRL', 'MXN', 'ZAR', 'SGD', 'HKD']
  .map((code) => ({ label: code, value: code.toLowerCase() }));

const DeclaredOptions = [
  { label: 'Achieved', value: 'achieved' },
  { label: 'In progress', value: 'in-progress' },
];

function askPricing() {
  rateDraft.value = {
    currency: money.value.currency.toLowerCase(),
    rate: money.value.agentHourlyRate === null ? '' : String(money.value.agentHourlyRate),
    declared: money.value.declaredCountsAs,
  };
  rateProblem.value = '';
  pricing.value = true;
}

async function confirmPricing() {
  const rate = rateDraft.value.rate.trim();
  if (rate !== '' && !/^\d+$/.test(rate)) {
    rateProblem.value = 'The rate is a whole number of the currency an agent hour, such as 90; empty is no rate.';
    return;
  }

  busy.value = true;
  rateProblem.value = '';

  try {
    await saveTenantSettings({
      'outcomes.currency': rateDraft.value.currency,
      'outcomes.agentHourlyRate': rate === '' ? 0 : Number(rate),
      'outcomes.declaredCountsAs': rateDraft.value.declared,
    });
    pricing.value = false;
    await load();
    if (current.value) await select(current.value.id);
  } catch (cause) {
    rateProblem.value = cause instanceof Error ? cause.message : String(cause);
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
    const answered = await previewMerge(current.value.id, into);
    // A pick changed while this was in flight: the newer pick's own preview is the one to show.
    if (mergeInto.value === into) preview.value = answered;
  } catch (cause) {
    mergeProblem.value = cause instanceof Error ? cause.message : String(cause);
  }
});

async function confirmMerge() {
  const into = mergeInto.value;
  // THE PREVIEW SHOWN MUST BE THE TARGET MERGED INTO: a slower preview of an earlier pick can land
  // after a newer pick, and confirming then would merge somewhere the person was not shown.
  if (!into || !preview.value || preview.value.refusal || preview.value.into.id !== into) return;

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
const newValue = ref('');
const createProblem = ref('');

function askCreate() {
  creating.value = true;
  newName.value = '';
  newDescription.value = '';
  newValue.value = '';
  createProblem.value = '';
}

async function confirmCreate() {
  if (!newName.value.trim() || busy.value) return;

  busy.value = true;
  createProblem.value = '';

  try {
    const fields: OutcomeFields = { name: newName.value.trim(), description: newDescription.value.trim() };
    if (newValue.value.trim()) fields.value = newValue.value.trim();
    const made = await createOutcome(fields);
    creating.value = false;
    emit('changed');
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
      return 'Description or budget changed';
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
    const what = link.outcomeId === null ? 'set to no outcome' : `linked to "${link.outcomeNameAtLink}"`;
    lines.push({
      key: `link-${link.id}`,
      at: link.setAt,
      text: `Workflow ${link.correlation} (${team}) ${what} by ${link.setBy} (${LinkHowLabel[link.how] ?? link.how})`,
    });
  }
  return lines.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
});
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

        <div class="row items-start q-gutter-sm q-mt-sm">
          <div class="col">
            <div class="row items-center q-gutter-sm">
              <div class="text-h6" data-outcome-title>{{ current.name }}</div>
              <q-badge outline :label="StatusLabel[current.status]" data-outcome-status />
            </div>
            <div class="text-caption os-text-muted">
              <template v-if="current.confirmedBy">Confirmed by {{ current.confirmedBy }} on {{ when(current.confirmedAt) }}</template>
              <template v-else>Proposed by {{ current.createdBy }} on {{ when(current.createdAt) }}</template>
              <template v-if="current.figures.teams.length"> · {{ current.figures.teams.length }} team{{ current.figures.teams.length === 1 ? '' : 's' }}</template>
            </div>
          </div>
          <div class="row items-center q-gutter-xs">
            <q-btn
              outline
              dense
              no-caps
              icon="account_tree"
              :label="`Workflows (${detail.workflows.length})`"
              data-outcome-workflows-open
              @click="showWorkflows = true"
            />
            <q-btn
              outline
              dense
              no-caps
              icon="history"
              :label="`History (${history.length})`"
              data-outcome-history-open
              @click="showHistory = true"
            />
            <q-separator vertical class="q-mx-xs" />
            <q-btn v-if="actions.includes('confirm')" outline dense no-caps label="Confirm" data-outcome-action="confirm" @click="transition('confirm')" />
            <q-btn v-if="actions.includes('merge')" outline dense no-caps label="Merge into…" data-outcome-action="merge" @click="askMerge" />
            <q-btn v-if="actions.includes('retire')" outline dense no-caps label="Retire" data-outcome-action="retire" @click="transition('retire')" />
            <q-btn
              v-if="actions.includes('reactivate')"
              outline
              dense
              no-caps
              label="Reactivate"
              data-outcome-action="reactivate"
              @click="transition('reactivate')"
            />
            <q-btn
              v-if="actions.includes('reject')"
              outline
              dense
              no-caps
              color="negative"
              label="Reject"
              data-outcome-action="reject"
              @click="reject"
            />
            <q-btn
              flat
              dense
              round
              icon="settings"
              aria-label="Outcome settings"
              data-outcome-settings-open
              @click="askEdit"
            >
              <q-tooltip>Name, description and budget</q-tooltip>
            </q-btn>
          </div>
        </div>

        <div v-if="readOnly" class="os-body os-text-muted q-mt-sm" data-outcome-merged>
          Merged into {{ outcomeName(current.mergedInto) }}. Its workflows and figures count there now; it is read-only.
        </div>

        <!-- BUDGET AND COST: the budget a person keeps (the API's `value`), the route's cost at the
             instance's rate, how much of the budget it is, and how much of its time waited on a person. -->
        <div class="outcome-money q-mt-md" data-outcome-money>
          <div class="outcome-stat" data-outcome-value>
            <div class="outcome-stat__figure">{{ valueText(current.value) || 'not set' }}</div>
            <div class="outcome-stat__label">Budget</div>
          </div>
          <div class="outcome-stat" data-outcome-cost>
            <div class="outcome-stat__figure">{{ costText(current.figures) }}</div>
            <div class="outcome-stat__label">
              Cost · {{ agentHoursText(current.figures.agentSeconds) }}<template v-if="money.agentHourlyRate !== null"> at {{ moneyText(money.agentHourlyRate, money.currency) }}/h</template>
            </div>
          </div>
          <div class="outcome-stat" data-outcome-share>
            <div class="outcome-stat__figure">{{ shareOfValueText(current.figures.cost.amount, valueAmount(current.value)) || '—' }}</div>
            <div class="outcome-stat__label">Of its budget spent</div>
          </div>
          <div class="outcome-stat" data-outcome-efficiency>
            <div class="outcome-stat__figure">{{ efficiencyText(current.figures.efficiency) }}</div>
            <div class="outcome-stat__label">
              Agents working<template v-if="current.figures.efficiency !== null">, {{ duration(current.figures.blockedSeconds) }} waiting on a person</template>
            </div>
            <div v-if="current.figures.efficiency !== null" class="outcome-split" aria-hidden="true">
              <div class="outcome-split__working" :style="{ width: `${current.figures.efficiency * 100}%` }" />
              <div class="outcome-split__waiting" :style="{ width: `${(1 - current.figures.efficiency) * 100}%` }" />
            </div>
          </div>
        </div>
        <div v-if="current.figures.tokens.unmeasuredRuns > 0" class="text-caption os-text-muted q-mt-xs" data-outcome-unmeasured>
          {{ current.figures.tokens.unmeasuredRuns }} run{{ current.figures.tokens.unmeasuredRuns === 1 ? '' : 's' }} reported no usage and {{ current.figures.tokens.unmeasuredRuns === 1 ? 'is' : 'are' }} counted, not estimated.
        </div>

        <!-- SPEND BY WEEK: one series, so the title names it and there is no legend. -->
        <div class="q-mt-md">
          <div class="row items-baseline">
            <div class="text-subtitle2">Spend by week</div>
            <q-space />
            <div class="text-caption os-text-muted">{{ money.agentHourlyRate === null ? 'agent time' : 'cost' }}, last 8 weeks</div>
          </div>
          <div class="outcome-weeks outcome-weeks--large" data-outcome-weeks>
            <div v-for="(week, index) in current.figures.weekly" :key="week.weekStart" class="outcome-week">
              <div class="outcome-week__bar" :style="{ height: `${weekHeights(current.figures.weekly)[index]}%` }" />
              <q-tooltip>{{ weekText(week, money.currency) }}</q-tooltip>
            </div>
          </div>
          <div class="row text-caption os-text-muted">
            <span>8 weeks ago</span>
            <q-space />
            <span>this week</span>
          </div>
        </div>

        <!-- THE BACKLOG, by where each item stands. Each opens in the Backlog dialog. -->
        <div class="q-mt-md">
          <div class="text-subtitle2">Backlog</div>
          <div class="outcome-columns q-mt-xs" data-outcome-backlog>
            <div v-for="column in columns" :key="column.key" class="outcome-column" :data-outcome-bucket="column.key">
              <div class="outcome-column__head">
                <span class="outcome-swatch" :data-swatch="column.key" aria-hidden="true" />
                {{ column.label }} ({{ column.items.length }})
              </div>
              <div v-if="column.items.length === 0" class="text-caption os-text-muted">None.</div>
              <button
                v-for="item in column.items"
                :key="item.id"
                type="button"
                class="outcome-item"
                :disabled="item.number === null"
                :data-outcome-item="item.id"
                @click="openItem(item)"
              >
                <span class="mono os-text-muted">{{ item.number === null ? item.id : itemLabel(item.number) }}</span>
                {{ item.title }}
                <span class="outcome-item__note">{{ itemNote(item) }}</span>
              </button>
            </div>
          </div>
        </div>

        <div v-if="actionProblem && !editing" class="os-body text-negative q-mt-sm" data-outcome-problem>{{ actionProblem }}</div>
      </q-card-section>

      <q-card-section v-else-if="detailError">
        <q-btn flat dense no-caps icon="arrow_back" label="All outcomes" @click="closeDetail" />
        <div class="os-body text-negative q-mt-sm">Could not open the outcome: {{ detailError }}</div>
      </q-card-section>

      <!-- THE TILES -->
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
          <span class="os-body os-text-muted" data-outcome-rate>
            {{ rateText(money) }} ·
            <a href="#" class="outcome-link" data-outcome-rate-change @click.prevent="askPricing">change</a>
          </span>
          <q-btn unelevated no-caps color="primary" icon="add" label="New outcome" data-outcome-new @click="askCreate" />
        </q-card-section>

        <q-card-section class="q-pt-none">
          <div v-if="accountingNote" class="os-body os-text-muted q-mb-sm" data-accounting-since>{{ accountingNote }}</div>

          <DialogTabs v-model="tab">
            <q-tab name="active" label="Active" data-outcome-tab="active" />
            <q-tab name="proposed" :label="`Proposed (${proposedCount})`" data-outcome-tab="proposed" />
            <q-tab name="ended" label="Retired & merged" data-outcome-tab="ended" />
          </DialogTabs>

          <div v-if="error" class="os-body text-negative q-mt-sm">Could not list the outcomes: {{ error }}</div>

          <div v-else class="os-tiles outcome-tiles q-mt-sm" data-outcome-tiles>
            <div
              v-for="o in shown"
              :key="o.id"
              class="os-tile outcome-tile"
              :class="{ 'outcome-tile--proposed': o.status === 'proposed' }"
              role="button"
              tabindex="0"
              :data-outcome-row="o.id"
              @click="select(o.id)"
              @keydown.enter="select(o.id)"
            >
              <div class="os-tile-head">
                <span class="text-weight-medium outcome-tile__name">{{ o.name }}</span>
              </div>
              <div class="row items-center outcome-tile__status">
                <span class="os-text-muted">{{ StatusLabel[o.status] }}<template v-if="o.status === 'merged'"> into {{ outcomeName(o.mergedInto) }}</template></span>
                <q-space />
                <q-badge v-if="awaitingCount(o.figures) > 0" color="warning" text-color="dark" :label="`${awaitingCount(o.figures)} awaiting you`" />
              </div>

              <div class="outcome-tile__money" data-outcome-tile-money>
                <span class="outcome-tile__cost">{{ costText(o.figures) }}</span>
                <span class="os-text-muted">
                  spent<template v-if="valueText(o.value)"> of {{ valueText(o.value) }} budget</template>
                  · {{ agentHoursText(o.figures.agentSeconds) }}
                </span>
              </div>

              <div class="outcome-weeks" data-outcome-weeks>
                <div v-for="(week, index) in o.figures.weekly" :key="week.weekStart" class="outcome-week">
                  <div class="outcome-week__bar" :style="{ height: `${weekHeights(o.figures.weekly)[index]}%` }" />
                  <q-tooltip>{{ weekText(week, money.currency) }}</q-tooltip>
                </div>
              </div>
              <div class="row text-caption os-text-muted">
                <span>8 weeks ago</span>
                <q-space />
                <span>this week</span>
              </div>

              <template v-if="backlogSegments(o.figures.backlog).length">
                <div class="outcome-bar" data-outcome-backlog-bar>
                  <div
                    v-for="segment in backlogSegments(o.figures.backlog)"
                    v-show="segment.count > 0"
                    :key="segment.key"
                    class="outcome-bar__segment"
                    :data-swatch="segment.key"
                    :style="{ width: `${segment.share}%` }"
                  />
                </div>
                <div class="outcome-legend">
                  <span v-for="segment in backlogSegments(o.figures.backlog)" :key="segment.key" :data-outcome-bucket-count="segment.key">
                    <span class="outcome-swatch" :data-swatch="segment.key" aria-hidden="true" />
                    {{ segment.count }} {{ segment.label.toLowerCase() }}
                  </span>
                </div>
              </template>
              <div v-else class="text-caption os-text-muted q-mt-xs">No backlog items yet.</div>

              <div class="os-tile-line os-text-muted q-mt-xs" data-outcome-summary>{{ tileSummaryText(o.figures) }}</div>
              <div v-if="o.description" class="os-tile-line os-text-muted outcome-tile__description">{{ firstLine(o.description) }}</div>
            </div>

            <div v-if="shown.length === 0 && tab !== 'active'" class="os-text-muted">No outcomes here.</div>

            <!-- ALWAYS SHOWN, closing Active: the work no outcome names, with the same figures. -->
            <div
              v-if="tab === 'active' && list"
              class="os-tile outcome-tile outcome-tile--none"
              data-no-outcome-row
            >
              <div class="os-tile-head">
                <span class="text-weight-medium text-italic">{{ list.noOutcome.name }}</span>
              </div>
              <div class="outcome-tile__money">
                <span class="outcome-tile__cost">{{ costText(list.noOutcome.figures) }}</span>
                <span class="os-text-muted">spent · {{ agentHoursText(list.noOutcome.figures.agentSeconds) }}</span>
              </div>
              <div class="outcome-weeks" data-outcome-weeks>
                <div v-for="(week, index) in list.noOutcome.figures.weekly" :key="week.weekStart" class="outcome-week">
                  <div class="outcome-week__bar" :style="{ height: `${weekHeights(list.noOutcome.figures.weekly)[index]}%` }" />
                  <q-tooltip>{{ weekText(week, money.currency) }}</q-tooltip>
                </div>
              </div>
              <div class="os-tile-line os-text-muted q-mt-xs" data-outcome-summary>{{ tileSummaryText(list.noOutcome.figures) }}</div>
              <div class="os-tile-line os-text-muted" data-outcome-tokens>
                <template v-if="list.noOutcome.figures.tokens.unmeasuredRuns > 0">{{ unmeasuredText(list.noOutcome.figures.tokens.unmeasuredRuns) }}</template>
              </div>
            </div>
          </div>
        </q-card-section>
      </template>
    </q-card>
  </q-dialog>

  <!-- SETTINGS: the outcome's name, description and budget, saved through the edit route. -->
  <q-dialog :model-value="editing" @update:model-value="(showing) => (editing = showing)">
    <q-card v-if="current" class="os-dialog-md" data-outcome-settings>
      <q-card-section>
        <div class="os-dialog-title">Outcome settings</div>
        <div class="outcome-fields q-mt-sm">
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
          <q-input
            v-model="draft.value"
            dense
            outlined
            inputmode="decimal"
            :label="`Budget (${money.currency})`"
            hint="What this outcome may spend. Its cost is set against it."
            :readonly="readOnly"
            data-outcome-field="value"
          />
        </div>
        <div v-if="readOnly" class="os-body os-text-muted q-mt-sm">A merged outcome is read-only.</div>
        <div v-if="actionProblem" class="os-body text-negative q-mt-sm" data-outcome-settings-problem>{{ actionProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="readOnly ? 'Close' : 'Cancel'" @click="editing = false" />
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
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- WORKFLOWS: each workflow the outcome serves, and moving one to another outcome. -->
  <q-dialog v-model="showWorkflows">
    <q-card v-if="detail && current" class="os-dialog-xl" data-outcome-workflows>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Workflows · {{ current.name }}</div>
        <q-space />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>
      <q-card-section class="outcome-dialog-body">
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
              <td class="text-right" data-outcome-tokens>
                {{ lineTokensFigure(line) }}
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
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- HISTORY: every act on the outcome and every link, oldest first, with who and when. -->
  <q-dialog v-model="showHistory">
    <q-card v-if="current" class="os-dialog-lg" data-outcome-history-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">History · {{ current.name }}</div>
        <q-space />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>
      <q-card-section class="outcome-dialog-body">
        <div v-if="history.length === 0" class="os-body os-text-muted">Nothing recorded yet.</div>
        <ul v-else class="outcome-history" data-outcome-history>
          <li v-for="line in history" :key="line.key">
            <span class="os-text-muted">{{ when(line.at) }}</span>
            {{ line.text }}
          </li>
        </ul>
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- THE RATE: the instance's currency, what an agent hour costs, and how a declared item counts. -->
  <q-dialog :model-value="pricing" @update:model-value="(showing) => (pricing = showing)">
    <q-card class="os-dialog-sm" data-outcome-pricing>
      <q-card-section>
        <div class="os-dialog-title">Pricing agent time</div>
        <div class="os-body os-text-muted q-mt-sm">
          One rate for every agent. An outcome's cost is its agent hours times this rate, worked out
          when it is read, so a change re-prices its whole history.
        </div>
        <q-select
          v-model="rateDraft.currency"
          :options="CurrencyOptions"
          dense
          outlined
          emit-value
          map-options
          label="Currency"
          class="q-mt-sm"
          data-outcome-pricing-currency
        />
        <q-input
          v-model="rateDraft.rate"
          dense
          outlined
          inputmode="numeric"
          label="Per agent hour"
          hint="Empty prices nothing."
          class="q-mt-sm"
          data-outcome-pricing-rate
        />
        <q-select
          v-model="rateDraft.declared"
          :options="DeclaredOptions"
          dense
          outlined
          emit-value
          map-options
          label="An item a Manager declared delivered counts as"
          class="q-mt-sm"
          data-outcome-pricing-declared
        />
        <div v-if="rateProblem" class="os-body text-negative q-mt-sm" data-outcome-pricing-problem>{{ rateProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="pricing = false" />
        <q-btn unelevated no-caps color="primary" label="Save" :loading="busy" data-outcome-pricing-save @click="confirmPricing" />
      </q-card-actions>
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
        <q-input v-model="newValue" dense outlined inputmode="decimal" :label="`Budget (${money.currency}, optional)`" class="q-mt-sm" data-outcome-create-value />
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

  <BacklogDialog v-model="backlogOpen" :item="backlogItem" />
</template>

<style scoped>
/* The grid itself is `os-tiles` (css/tiles.scss); this only says how narrow a column may get. */
.outcome-tiles {
  --os-tile-min: 20rem;
}

.outcome-tile {
  cursor: pointer;
  gap: 4px;
}

.outcome-tile:focus-visible {
  outline: 2px solid var(--q-primary);
  outline-offset: 2px;
}

.outcome-tile__name {
  overflow-wrap: anywhere;
}

.outcome-tile__status {
  font-size: 12px;
}

/* A proposal is not yet a person's: dashed, as the card tag draws it. */
.outcome-tile--proposed {
  border-style: dashed;
}

.outcome-tile--none {
  border-style: dashed;
  background: var(--os-chrome);
  cursor: default;
}

.outcome-tile__money {
  display: flex;
  align-items: baseline;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 4px;
  font-size: 12px;
}

.outcome-tile__cost {
  font-size: 20px;
  font-weight: 600;
}

.outcome-tile__description {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

/* WEEKLY SPEND: one series, thin bars on a baseline, a 2px gap between them, rounded data-ends. */
.outcome-weeks {
  display: flex;
  align-items: flex-end;
  gap: 2px;
  height: 40px;
  margin-top: 6px;
  border-bottom: 1px solid var(--os-rule);
}

.outcome-weeks--large {
  height: 96px;
  margin-top: 8px;
}

.outcome-week {
  flex: 1;
  height: 100%;
  display: flex;
  align-items: flex-end;
}

.outcome-week__bar {
  width: 100%;
  min-height: 1px;
  background: var(--os-series-1);
  border-radius: 4px 4px 0 0;
}

/* THE BACKLOG BAR: three states, a 2px surface gap between segments, each labelled with its count. */
.outcome-bar {
  display: flex;
  gap: 2px;
  height: 10px;
  margin-top: 8px;
  border-radius: 5px;
  overflow: hidden;
}

.outcome-bar__segment {
  height: 100%;
}

.outcome-legend {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 11px;
}

.outcome-swatch {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 2px;
  margin-right: 3px;
}

/* THE THREE STATES, by `data-swatch` on the swatch and on the bar's segment. */
[data-swatch='notStarted'] {
  background: var(--os-rule-strong);
}

[data-swatch='inProgress'] {
  background: var(--os-series-1);
}

[data-swatch='achieved'] {
  background: var(--os-stat-running);
}

.outcome-money {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
  gap: 10px;
}

.outcome-stat {
  border: 1px solid var(--os-rule);
  border-radius: 6px;
  padding: 8px 10px;
}

.outcome-stat__figure {
  font-size: 20px;
  font-weight: 600;
}

.outcome-stat__label {
  font-size: 11px;
  color: var(--os-text-muted, inherit);
  opacity: 0.85;
}

.outcome-split {
  display: flex;
  gap: 2px;
  height: 6px;
  margin-top: 6px;
  border-radius: 3px;
  overflow: hidden;
}

.outcome-split__working {
  background: var(--os-stat-running);
}

.outcome-split__waiting {
  background: var(--os-stat-blocked);
}

.outcome-columns {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
  gap: 12px;
}

.outcome-column__head {
  font-size: 12px;
  font-weight: 600;
  margin-bottom: 6px;
}

.outcome-item {
  display: block;
  width: 100%;
  text-align: left;
  font: inherit;
  font-size: 12px;
  color: inherit;
  background: none;
  border: 1px solid var(--os-rule);
  border-radius: 5px;
  padding: 5px 8px;
  margin-bottom: 5px;
  cursor: pointer;
}

.outcome-item:hover:not(:disabled) {
  border-color: var(--os-rule-strong);
}

.outcome-item__note {
  display: block;
  font-size: 11px;
  opacity: 0.7;
}

.outcome-link {
  color: inherit;
}

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

.outcome-history {
  margin: 0;
  padding: 4px 0 0 1.2rem;
}

.outcome-dialog-body {
  max-height: 70vh;
  overflow-y: auto;
}
</style>
