<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { readCliVersions, readDiagnostics, readVersion } from '../api/client';
import { bundleBuild, builtWhen, type BuildInfo } from '../lib/buildInfo';
import type {
  CliVersionsAtStart,
  DiagnosticEvent,
  DiagnosticSeverity,
  DiagnosticsFilter,
} from '../api/types';
import {
  DiagnosticSeverities,
  DiagnosticSeverityColor,
  DiagnosticsEmptyMessage,
  activeDiagnosticsFilterCount,
  diagnosticKindLabel,
  diagnosticsEmptyState,
  diagnosticsVisibleColumns,
} from '../lib/diagnostics';
import { useCursorList } from '../lib/useCursorList';
import CursorSentinel from './CursorSentinel.vue';

/**
 * Admin › Diagnostics — what this instance was doing when it went wrong.
 *
 * NOT THE TENANT LOG BESIDE IT ON THE RIBBON, and the pair is worth holding apart. `Log` shows what
 * a PERSON did to the instance: it names an actor, and every row is somebody's administrative act.
 * Nobody DID any of these, which is why no row here names anyone. It is not the message log either —
 * nothing subscribes to a diagnostic row and no container is woken by one.
 *
 * HUMAN-ONLY. Every person is an administrator and may open it; `GET /api/diagnostics` refuses a
 * machine principal on the server, and the ribbon never disables it for a signed-in person.
 *
 * SERVER-SIDE filtering and searching, and reading BY CURSOR, for the reason
 * `TenantLogDialog` has them: this is the highest-volume store the product has, so a grid that
 * fetched everything and narrowed it in the browser would look identical on a quiet instance and stop
 * working on the one worth reading. Older rows arrive as the sentinel at the bottom of the virtual
 * scroll comes into view, and only ever BELOW what is held - so nothing moves under the pointer.
 *
 * Read-only by construction. There is no write here and no delete on the server either: the store is
 * written from inside the failure paths and trimmed by its own retention bound. A diagnostics log
 * with a clear button is one somebody empties on the morning they most need it.
 */
const open = defineModel<boolean>({ required: true });


/** The closed kind vocabulary, as the SERVER gave it. Never a list maintained here — see
 *  `DiagnosticsView.kinds`. Empty until the first read answers, which is why the kind select is
 *  disabled rather than empty-looking before then. */
const kinds = ref<string[]>([]);

/** `null` = the store could not be read, and it is the fact the empty state turns on. Starts null
 *  rather than false so a dialog that has not fetched yet does not assert an instance is empty. */
const hasAny = ref<boolean | null>(null);

/**
 * What is narrowed, as the server's own filter shape rather than a second one of this screen's
 * invention — `DiagnosticsFilter` is the contract, and `lib/diagnostics.ts` turns it into the query.
 *
 * `applied` is what the rows on screen were fetched with, and it is what the empty state is read
 * against. Using the LIVE filter there would explain an empty grid with narrowings the person typed
 * after it came back.
 */
const filter = ref<DiagnosticsFilter>({});
const applied = ref<DiagnosticsFilter>({});

/** The server's count of every matching row, for the caption. Never `rows.length`. */
const total = ref(0);

/**
 * THE LIST, READ BY CURSOR. The filter is snapshotted BEFORE the await on each page, so what comes
 * back is explained by what it was asked for rather than by whatever the person has typed since; and
 * a filter change clears the list and reads from the top - the composable drops a page that answers
 * after that.
 */
const list = useCursorList<DiagnosticEvent>(
  async (before, take) => {
    const asked: DiagnosticsFilter = { ...filter.value };

    // THE RANGE GUARD LIVES HERE rather than at each caller, so every read - the opening, a
    // narrowing, the sentinel asking for older rows - meets it. Nothing is asked for; the To field
    // says why, and `applied` keeps the last filter the server actually answered.
    if (rangeProblemOf(asked) !== null) return [];

    try {
      const view = await readDiagnostics(asked, before, take);

      hasAny.value = view.hasAny;
      kinds.value = view.kinds;
      applied.value = asked;
      total.value = view.page.total;

      return view.page.events;
    } catch (cause) {
      // NOT `null`. A fetch that failed is this browser's problem — a dropped connection, a 403 —
      // and is not the store saying it could not be read. Claiming (unknown) here would put the
      // store's own state on a failure that says nothing about it, and the banner already says what
      // happened.
      if (before === undefined) hasAny.value = false;
      applied.value = asked;
      throw cause;
    }
  },
  (row) => row.seq,
  { filter },
);
const { rows, loading, exhausted, error } = list;

const filterCount = computed(() => activeDiagnosticsFilterCount(filter.value));

const kindOptions = computed(() =>
  kinds.value.map((kind) => ({ label: diagnosticKindLabel(kind), value: kind })),
);

const severityOptions = computed(() =>
  DiagnosticSeverities.map((severity) => ({ label: severity, value: severity })),
);

/** The four answers to "why is this empty", read against the filter the rows were FETCHED with. */
const emptyState = computed(() =>
  diagnosticsEmptyState({ page: { events: rows.value }, hasAny: hasAny.value }, applied.value),
);

const emptyMessage = computed(() =>
  emptyState.value === 'rows' ? '' : DiagnosticsEmptyMessage[emptyState.value],
);

/** UNREADABLE READS AS A FAULT, the other two do not. An instance that cannot say what went wrong is
 *  a different thing from a quiet one, and a grey line for both would flatten exactly the distinction
 *  this screen was asked for. */
const emptyIsFault = computed(
  () => emptyState.value === 'unreadable' || emptyState.value === 'never',
);

/**
 * The chip colour for a severity, with a fallback rather than an index.
 *
 * The wire's severity is only as closed as the server that sent it: a severity this browser does not know (an older bundle against a newer
 * host) would render an UNDEFINED colour rather than a grey chip. Loudest still reads loudest; an
 * unfamiliar one reads as a fact rather than disappearing.
 */
function severityColor(severity: string): string {
  return DiagnosticSeverityColor[severity as DiagnosticSeverity] ?? 'grey-6';
}

interface Column {
  name: string;
  label: string;
  field: keyof DiagnosticEvent;
  format?: (value: string) => string;
}

const columns: Column[] = [
  {
    name: 'occurredAt',
    label: 'When',
    field: 'occurredAt',
    // Local time, because the reader is a person deciding whether this was the run they were just
    // watching. The wire carries a round-trip UTC string, so nothing here depends on the server's
    // timezone.
    format: (value: string) => new Date(value).toLocaleString(),
  },
  { name: 'severity', label: 'Severity', field: 'severity' },
  // Worded in `format`, so the stacked cards below `sm` and the wide grid say the same words.
  { name: 'kind', label: 'Kind', field: 'kind', format: diagnosticKindLabel },
  { name: 'source', label: 'Source', field: 'source' },
  { name: 'route', label: 'Route', field: 'route' },
  { name: 'status', label: 'Status', field: 'status' },
  { name: 'exceptionType', label: 'Exception', field: 'exceptionType' },
  { name: 'message', label: 'Message', field: 'message' },
  { name: 'detail', label: 'Detail', field: 'detail' },
];

const $q = useQuasar();

/**
 * Four columns below `md`, every column above it; `diagnosticsVisibleColumns` says which four.
 * Below `sm` even four do not seat - a timestamp and a sentence are 600px on their own - so the rows
 * become stacked cards, one label and value per line.
 */
const visibleColumns = computed(() => {
  const names = diagnosticsVisibleColumns($q.screen.lt.md);
  return names ? columns.filter((column) => names.includes(column.name)) : columns;
});
const stacked = computed(() => $q.screen.lt.sm);

/** A cell's words: the column's `format` when it has one, and nothing rather than `null`. */
function cell(row: DiagnosticEvent, column: Column): string {
  const value = row[column.field];
  if (value === null || value === undefined || value === '') return '';

  return column.format ? column.format(String(value)) : String(value);
}

/**
 * A range that ends before it starts, said on the To field. Both are `YYYY-MM-DD` from a date input,
 * so they compare as text. Null while the range is empty, open-ended or in order.
 */
function rangeProblemOf(range: DiagnosticsFilter): string | null {
  const { from, to } = range;
  return from && to && to < from ? 'To is before From. Choose a date on or after it.' : null;
}

const rangeProblem = computed(() => rangeProblemOf(filter.value));

/** Every control writes through here. The list watches `filter`, so a narrowing clears it and reads
 *  from the top - a narrowing that kept the rows already loaded would show rows it excludes. A range
 *  that ends before it starts is NOT asked for (the guard is in the list's reader): it can only
 *  answer nothing, which would read as "nothing happened" rather than as a typo. */
function narrow(patch: DiagnosticsFilter) {
  filter.value = { ...filter.value, ...patch };
}

function clearFilters() {
  filter.value = {};
}

/**
 * WHICH CLI VERSIONS EACH START OF THIS VOLUME HAD, newest first. `null` until read, and
 * a failed read is said as one rather than shown as "nothing recorded".
 */
const cliStarts = ref<CliVersionsAtStart[] | null>(null);
const cliError = ref<string | null>(null);

/** Every CLI any start named, in first-seen order, so a CLI added later gets a column too. */
const cliNames = computed(() => {
  const names: string[] = [];
  for (const start of cliStarts.value ?? []) {
    for (const name of Object.keys(start.versions)) {
      if (!names.includes(name)) names.push(name);
    }
  }
  return names;
});

/** A version that differs from the start before it (the next row down) - what an upgrade looks like. */
function cliChanged(index: number, name: string): boolean {
  const older = cliStarts.value?.[index + 1];
  return older !== undefined && older.versions[name] !== cliStarts.value?.[index]?.versions[name];
}

/**
 * WHICH BUILD THE HOST IS, beside the CLI versions: the answer to "what was this instance
 * running" starts with the instance itself. The Host's own answer, not the bundle's - the two are
 * one build in the container, and when they are not (a dev server against another host) the
 * bundle's is named too.
 */
const hostBuild = ref<BuildInfo | null>(null);
const hostBuildError = ref<string | null>(null);

async function loadHostBuild() {
  hostBuildError.value = null;
  try {
    hostBuild.value = await readVersion();
  } catch (cause) {
    hostBuild.value = null;
    hostBuildError.value = cause instanceof Error ? cause.message : String(cause);
  }
}

async function loadCliVersions() {
  cliError.value = null;
  try {
    cliStarts.value = await readCliVersions();
  } catch (cause) {
    cliStarts.value = null;
    cliError.value = cause instanceof Error ? cause.message : String(cause);
  }
}

watch(open, (showing) => {
  if (!showing) return;

  void loadHostBuild();
  void loadCliVersions();

  // Back to the newest rows on every opening, the way the tenant log is. Somebody reopening this is
  // asking "what just happened", not resuming where they left off. The FILTER is kept: it is what
  // they were investigating, and throwing it away would make a second look start from nothing.
  void list.reset();
});
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="diagnostics-card os-dialog-xl">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Diagnostics</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        What this instance was doing when it went wrong, newest first — failures, and the startup
        facts that say what it was configured as. Not the tenant log: nobody did these.
      </q-card-section>

      <q-card-section class="q-pt-none">
        <div class="d-build q-mb-sm os-body">
          <div v-if="hostBuildError" class="text-negative">
            The version could not be read: {{ hostBuildError }}
          </div>
          <dl v-else-if="hostBuild" class="d-build-list">
            <dt>Version</dt>
            <dd class="d-build-value">{{ hostBuild.version }}</dd>
            <dt>Commit</dt>
            <dd class="d-build-value">{{ hostBuild.commit }}</dd>
            <dt>Built</dt>
            <dd>{{ builtWhen(hostBuild.builtAt) }}</dd>
            <template v-if="bundleBuild.version !== hostBuild.version">
              <dt>This page</dt>
              <dd class="d-build-value">{{ bundleBuild.version }}</dd>
            </template>
          </dl>
        </div>

        <q-expansion-item
          dense
          icon="terminal"
          label="Agent CLI versions"
          caption="What each start of this volume had installed, newest first"
          class="d-cli q-mb-sm"
        >
          <div v-if="cliError" class="os-body text-negative q-pa-sm">
            The CLI versions could not be read: {{ cliError }}
          </div>
          <div v-else-if="cliStarts && cliStarts.length === 0" class="os-body os-text-muted q-pa-sm">
            No start has recorded its CLI versions. The container's start script records them; a
            host started outside the container does not.
          </div>
          <div v-else-if="cliStarts" class="d-cli-scroll">
            <table class="d-cli-table">
              <thead>
                <tr>
                  <th>Started</th>
                  <th v-for="name in cliNames" :key="name">{{ name }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(start, index) in cliStarts" :key="start.at">
                  <td class="diagnostics-when">{{ new Date(start.at).toLocaleString() }}</td>
                  <td
                    v-for="name in cliNames"
                    :key="name"
                    :class="{ 'd-cli-changed': cliChanged(index, name) }"
                  >
                    <span v-if="start.versions[name]">{{ start.versions[name] }}</span>
                    <span v-else class="os-text-muted">not installed</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </q-expansion-item>

        <div class="d-filters">
          <q-select
            :model-value="filter.kinds ?? []"
            :options="kindOptions"
            :disable="kinds.length === 0"
            dense
            outlined
            multiple
            clearable
            emit-value
            map-options
            label="Kind"
            class="d-filter d-filter--wide"
            @update:model-value="(value) => narrow({ kinds: value ?? [] })"
          />

          <q-select
            :model-value="filter.severities ?? []"
            :options="severityOptions"
            dense
            outlined
            multiple
            clearable
            emit-value
            map-options
            label="Severity"
            class="d-filter"
            @update:model-value="(value) => narrow({ severities: value ?? [] })"
          />

          <!-- Debounced rather than fetched per keystroke, the way the Kanban filter bar's are: a
               date is typed, and a read per character is four wasted pages for every useful one. -->
          <q-input
            :model-value="filter.from ?? ''"
            type="date"
            dense
            outlined
            label="From"
            :debounce="400"
            class="d-filter"
            stack-label
            @update:model-value="(value) => narrow({ from: String(value ?? '') })"
          />

          <q-input
            :model-value="filter.to ?? ''"
            type="date"
            dense
            outlined
            label="To"
            :debounce="400"
            class="d-filter"
            stack-label
            :error="rangeProblem !== null"
            :error-message="rangeProblem ?? ''"
            @update:model-value="(value) => narrow({ to: String(value ?? '') })"
          />

          <!-- FOUR COLUMNS AND NOT SIX. This searches message, detail, route and exception type -
               never kind or severity, which have their own filters beside it. A box that matched
               those too would make the box and the filter disagree about one word, and `%` and `_`
               are taken literally so a typed wildcard finds what was typed. -->
          <q-input
            :model-value="filter.search ?? ''"
            dense
            outlined
            clearable
            label="Search"
            hint="Message, detail, route and exception type"
            :debounce="400"
            class="d-filter d-filter--wide"
            @update:model-value="(value) => narrow({ search: String(value ?? '') })"
          >
            <template #prepend><q-icon name="search" /></template>
          </q-input>

          <q-btn
            v-if="filterCount"
            flat
            dense
            no-caps
            icon="filter_alt_off"
            :label="`Clear (${filterCount})`"
            @click="clearFilters"
          />
        </div>

        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-my-md">
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>

        <!-- THE THREE EMPTY STATES, AND THEY ARE THREE. Rendered above the grid rather than as its
             `no-data-label`, because the sentence is the answer here and a grey line inside an empty
             table is the shape this screen exists not to be. -->
        <q-banner
          v-if="!loading && !error && !rangeProblem && emptyState !== 'rows'"
          dense
          class="q-my-md"
          :class="emptyIsFault ? 'os-bg-tint-warn text-warning' : 'os-bg-quiet os-text-muted'"
        >
          <template #avatar>
            <q-icon :name="emptyIsFault ? 'warning' : 'inbox'" />
          </template>
          {{ emptyMessage }}
        </q-banner>

        <div v-if="rows.length" class="os-body os-text-muted q-mb-xs">
          {{ rows.length }} of {{ total }} shown
        </div>

        <!-- STACKED BELOW `sm`: one card per row, one label and value per line. -->
        <q-virtual-scroll
          v-if="stacked"
          class="diagnostics-scroll"
          :items="rows"
          :virtual-scroll-item-size="160"
        >
          <template #default="{ item: row }">
            <div :key="row.seq" class="diagnostics-stacked">
              <template v-for="column in visibleColumns" :key="column.name">
                <div v-if="cell(row, column)" class="diagnostics-stacked-line">
                  <span class="os-text-muted">{{ column.label }}</span>
                  <q-badge
                    v-if="column.name === 'severity'"
                    :color="severityColor(row.severity)"
                    :label="row.severity"
                  />
                  <span v-else>{{ cell(row, column) }}</span>
                </div>
              </template>
            </div>
          </template>

          <template #after>
            <CursorSentinel
              v-if="rows.length"
              :loading="loading"
              :exhausted="exhausted"
              :error="error"
              @more="list.loadMore"
            />
          </template>
        </q-virtual-scroll>

        <!-- A VIRTUAL SCROLL, NOT A PAGED TABLE. The header row is sticky so a column can
             still be named forty rows down; the sentinel in the footer asks for older rows. -->
        <q-virtual-scroll
          v-else
          type="table"
          class="diagnostics-scroll"
          dense
          flat
          bordered
          :items="rows"
          :virtual-scroll-item-size="33"
          :virtual-scroll-sticky-size-start="33"
        >
          <template #before>
            <thead class="diagnostics-head">
              <tr>
                <th v-for="column in visibleColumns" :key="column.name" class="text-left">
                  {{ column.label }}
                </th>
              </tr>
            </thead>
          </template>

          <template #default="{ item: row }">
            <tr :key="row.seq">
              <td
                v-for="column in visibleColumns"
                :key="column.name"
                :class="{
                  'diagnostics-when': column.name === 'occurredAt',
                  'diagnostics-detail': column.name === 'detail',
                  'diagnostics-message': column.name === 'message',
                }"
              >
                <q-badge
                  v-if="column.name === 'severity'"
                  :color="severityColor(row.severity)"
                  :label="row.severity"
                />
                <template v-else>
                  <span v-if="cell(row, column)">{{ cell(row, column) }}</span>
                  <!-- The dotted name in a tooltip under the human words: the words are what makes
                       the page readable, and the raw kind is what somebody types into the filter or
                       quotes in an issue. Detail and message are truncated in the cell with the
                       whole of them in a tooltip: a column that wraps to six lines makes every
                       other row unreadable. -->
                  <q-tooltip v-if="column.name === 'kind'">{{ row.kind }}</q-tooltip>
                  <q-tooltip
                    v-else-if="(column.name === 'detail' || column.name === 'message') && cell(row, column)"
                    max-width="480px"
                  >{{ cell(row, column) }}</q-tooltip>
                </template>
              </td>
            </tr>
          </template>

          <template #after>
            <tfoot v-if="rows.length">
              <tr>
                <td :colspan="visibleColumns.length">
                  <CursorSentinel
                    :loading="loading"
                    :exhausted="exhausted"
                    :error="error"
                    @more="list.loadMore"
                  />
                </td>
              </tr>
            </tfoot>
          </template>
        </q-virtual-scroll>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat label="Close" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.d-build-list {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 2px 12px;
  margin: 0;
}

.d-build-list dt {
  color: var(--os-ink-muted);
}

.d-build-list dd {
  margin: 0;
  overflow-wrap: anywhere;
}

.d-build-value {
  font-family: 'IBM Plex Mono', monospace;
}

.d-cli-scroll {
  max-height: 14rem;
  overflow: auto;
}

.d-cli-table {
  border-collapse: collapse;
  width: 100%;
}

.d-cli-table th,
.d-cli-table td {
  padding: 4px 8px;
  text-align: left;
  white-space: nowrap;
  border-bottom: 1px solid var(--os-rule);
}

.d-cli-table th {
  position: sticky;
  top: 0;
  background: var(--os-surface);
}

/* Differs from the start before it: an upgrade, an install or a removal. */
.d-cli-changed {
  font-weight: 600;
}

.d-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 8px;
}

.d-filter {
  min-width: 8.5rem;
}

.d-filter--wide {
  min-width: 14rem;
}

/* Bounded, so the dialog body scrolls rather than the page; the footer sentinel is at its bottom. */
.diagnostics-scroll {
  max-height: min(58vh, 620px);
}

.diagnostics-head th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: var(--os-surface);
}

.diagnostics-when {
  white-space: nowrap;
}

.diagnostics-stacked {
  padding: 8px 4px;
  border-bottom: 1px solid var(--os-rule);
}

.diagnostics-stacked-line {
  display: flex;
  gap: 8px;
  overflow-wrap: anywhere;
}

.diagnostics-detail,
.diagnostics-message {
  max-width: 260px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
