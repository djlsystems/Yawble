<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, reactive, ref, useId, watch } from 'vue';
import type { LogColumn, LogPage, LogPrunePreview, LogSourceInfo, LogWho, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import Modal from './Modal.vue';
import BusyLabel from './BusyLabel.vue';
import LoadingNote from './LoadingNote.vue';
import { useNarrow } from '../narrowScreen';
import { scriptsApi } from '../scripts/api';
import type { RunDetail } from '../scripts/types';
// Loaded when a run is opened: the run view brings the flowchart library with it.
const ScriptRunView = defineAsyncComponent(() => import('./scripts/ScriptRunView.vue').then((m) => m.default));

/**
 * Logs: every log the app keeps, one at a time, as a table. The source is chosen from a list; the team, the member,
 * the source's own columns, a time range and free text filter it; any column sorts it; pages load as the table
 * scrolls. On this computer, Prune deletes old entries after saying exactly what goes, and Retention sets how long
 * each log is kept. A cloud viewer reads only. Opened from the sidebar, or from a member's card already filtered to
 * that member's activity.
 */
// `page`: drawn as the app's Logs place (inline, filling it) instead of a dialog; a member's logs stay a dialog.
const props = defineProps<{ api: YawbleApi; source?: string; team?: string; member?: string; page?: boolean }>();
const emit = defineEmits<{ close: [] }>();

/** A phone (B037): each entry is a card of its columns' values (tap for them in full), sorted with a select. */
const phone = useNarrow();
const pageSize = 100;
const sources = ref<LogSourceInfo[]>([]);
const who = ref<LogWho>({ teams: [], members: [] });
const sourceId = ref(props.source ?? 'activity');
const team = ref(props.member ? props.member.slice(0, props.member.indexOf('/')) : props.team ?? '');
const member = ref(props.member ?? '');
/** All activity only (B101): the app's own rows (backlog, settings, triggers, link, backups...), no team or member. */
const app = ref(false);
/** The Level filter (a source with levels: All activity, Diagnostics): '' every level but debug, 'all', or one level. */
const level = ref('');
const filters = reactive<Record<string, string>>({});
const values = reactive<Record<string, string[]>>({});
const fromDay = ref('');
const toDay = ref('');
const text = ref('');
const sort = ref('at');
const descending = ref(true);

const rows = ref<LogPage['rows']>([]);
const total = ref<number | null>(null);
const next = ref<number | null>(null);
const asOf = ref<string | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const open = ref<string | null>(null);
let seq = 0;

const source = computed(() => sources.value.find((s) => s.id === sourceId.value) ?? null);
/** The source's columns; a source with levels shows each row's level after its time. */
const columns = computed<LogColumn[]>(() => {
  const list = source.value?.columns ?? [];
  if (!source.value?.levels) return list;
  return [...list.slice(0, 1), { key: 'level', label: 'Level', kind: 'text', filter: false }, ...list.slice(1)];
});
const filterColumns = computed(() => columns.value.filter((c) => c.filter && !(c.key === 'team' && source.value?.team)));
const groups = computed(() => {
  const by = new Map<string, LogSourceInfo[]>();
  for (const s of sources.value) by.set(s.group, [...(by.get(s.group) ?? []), s]);
  return [...by.entries()];
});
const membersOfTeam = computed(() => who.value.members.filter((m) => !team.value || m.team === team.value));
/** The teams the Member filter offers as a whole (all its members' rows plus its team's own rows): the chosen one, or each. */
const wholeTeams = computed(() => (team.value ? [team.value] : who.value.teams));
/** 'app' is a filter of All activity only (the per-source logs have no app rows of their own). */
const appOffered = computed(() => sourceId.value === 'activity');
const teamPrefix = 'team:';
/**
 * The Member filter (B101) picks one of: a member (member=team/Name), a team as a whole (team=X, no member) or the app
 * (app=true, no team or member). Picking one clears the others.
 */
const whoPick = computed<string>({
  get: () => (app.value ? 'app' : member.value ? member.value : team.value ? `${teamPrefix}${team.value}` : ''),
  set: (v) => {
    if (v === 'app') { member.value = ''; team.value = ''; app.value = true; return; }
    app.value = false;
    if (!v) { member.value = ''; return; }
    if (v.startsWith(teamPrefix) && !who.value.members.some((m) => m.key === v)) { member.value = ''; team.value = v.slice(teamPrefix.length); return; }
    team.value = v.slice(0, v.indexOf('/'));
    member.value = v;
  },
});
const local = computed(() => !props.api.remote);
/** On a phone the filters fold behind a Filters button (closed at first), so the entries get the screen. */
const filtersOpen = ref(false);
/** On a phone Prune… and Retention… fold behind a ⋯ (the bar is then one row in landscape). */
const actionsOpen = ref(false);
/** Where the filters go on a phone: the top of the list's scroller (B057). */
const phoneToolsId = `logs-phone-tools-${useId()}`;
/** How many filters are set (the search box apart): shown on the phone's Filters button. */
const activeFilters = computed(() => [team.value, member.value, app.value, level.value, fromDay.value, toDay.value, ...Object.values(filters)].filter((v) => !!v).length);
const filtered = computed(() => !!(team.value || member.value || app.value || level.value || fromDay.value || toDay.value || text.value.trim() || Object.values(filters).some((v) => v)));

function query(offset: number) {
  const f: Record<string, string> = {};
  for (const [k, v] of Object.entries(filters)) if (v && columns.value.some((c) => c.key === k)) f[k] = v;
  return {
    source: sourceId.value,
    team: source.value?.team && !member.value ? team.value || undefined : undefined,
    member: source.value?.member ? member.value || undefined : undefined,
    ...(app.value && appOffered.value ? { app: true } : {}),
    ...(level.value && source.value?.levels ? { level: level.value } : {}),
    from: fromDay.value ? `${fromDay.value}T00:00:00` : undefined,
    to: toDay.value ? `${toDay.value}T23:59:59.9999999` : undefined,
    text: text.value.trim() || undefined,
    filters: f,
    sort: sort.value,
    descending: descending.value,
    offset,
    limit: pageSize,
    asOf: offset > 0 ? asOf.value ?? undefined : undefined,
  };
}

/** The first page again, for the current source, filters and sort. */
async function reload() {
  const mine = ++seq;
  loading.value = true;
  error.value = null;
  open.value = null;
  try {
    const page = await props.api.call<LogPage>('logs.query', query(0));
    if (mine !== seq) return;
    rows.value = page.rows;
    total.value = page.total;
    next.value = page.next;
    asOf.value = page.asOf;
    await nextTick();
    void fill();
  } catch (e) {
    if (mine === seq) { error.value = errorText(e); rows.value = []; total.value = null; next.value = null; }
  } finally {
    if (mine === seq) loading.value = false;
  }
}

/** The next page, if there is one and none is loading. */
async function more() {
  if (next.value === null || loading.value) return;
  const mine = seq;
  loading.value = true;
  try {
    const page = await props.api.call<LogPage>('logs.query', query(next.value));
    if (mine !== seq) return;
    rows.value = [...rows.value, ...page.rows];
    next.value = page.next;
  } catch (e) {
    if (mine === seq) error.value = errorText(e);
  } finally {
    if (mine === seq) loading.value = false;
  }
}

const scroller = ref<HTMLElement | null>(null);
function onScroll() {
  const el = scroller.value;
  if (el && el.scrollTop + el.clientHeight >= el.scrollHeight - 240) void more();
}
/** Loads pages until the table fills its space (or the log ends), so scrolling has something to scroll. */
async function fill() {
  const el = scroller.value;
  if (el && next.value !== null && el.scrollHeight <= el.clientHeight + 40) { await more(); await nextTick(); void fill(); }
}

function sortBy(key: string) {
  if (sort.value === key) descending.value = !descending.value;
  else { sort.value = key; descending.value = columns.value.find((c) => c.key === key)?.kind !== 'text'; }
  void reload();
}

async function loadValues() {
  for (const c of filterColumns.value) {
    try {
      values[c.key] = await props.api.call<string[]>('logs.values', {
        source: sourceId.value, column: c.key,
        team: member.value ? undefined : team.value || undefined,
        member: member.value || undefined,
        // "app (the app itself)": only the values the app's own rows hold (no Runs, no teams).
        ...(app.value && appOffered.value ? { app: true } : {}),
      });
    }
    catch { values[c.key] = []; }
  }
}

function clearFilters() {
  team.value = ''; member.value = ''; app.value = false; level.value = ''; fromDay.value = ''; toDay.value = ''; text.value = '';
  for (const k of Object.keys(filters)) filters[k] = '';
}

// A new source: its own column filters start empty; sort by its first column, newest first.
watch(sourceId, () => {
  for (const k of Object.keys(filters)) delete filters[k];
  sort.value = columns.value[0]?.key ?? 'at';
  descending.value = true;
  pruning.value = null;
  if (!appOffered.value) app.value = false;
  void loadValues();
  void reload();
});
watch(team, () => {
  if (member.value && !member.value.startsWith(`${team.value}/`)) member.value = '';
  if (team.value) app.value = false;
});
watch([team, member, app], () => { void loadValues(); });
watch([team, member, app, level, fromDay, toDay, () => ({ ...filters })], () => { void reload(); }, { deep: true });
let typing: ReturnType<typeof setTimeout> | undefined;
watch(text, () => { clearTimeout(typing); typing = setTimeout(() => void reload(), 300); });
onBeforeUnmount(() => clearTimeout(typing));

onMounted(async () => {
  try {
    [sources.value, who.value] = await Promise.all([
      props.api.call<LogSourceInfo[]>('logs.sources'),
      props.api.call<LogWho>('logs.who').catch(() => ({ teams: [], members: [] })),
    ]);
    if (!sources.value.some((s) => s.id === sourceId.value)) sourceId.value = sources.value[0]?.id ?? 'activity';
    sort.value = columns.value[0]?.key ?? 'at';
    void loadValues();
    await reload();
  } catch (e) { error.value = errorText(e); }
});

// ------------------------------------------------------------------ cells

function cell(row: LogPage['rows'][number], c: LogColumn): string {
  const v = row[c.key];
  if (v === null || v === undefined || v === '') return '';
  if (c.kind === 'time') return when(String(v));
  if (c.kind === 'number' && typeof v === 'number') return v.toLocaleString();
  return String(v);
}

function when(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function memberLabel(key: string) { return key.slice(key.indexOf('/') + 1); }

// ------------------------------------------------------------------ a script run's view (Script runs)

/** The Script runs log: its 'run' column is the script run's id, which opens that run's view. */
const runLinks = computed(() => sourceId.value === 'scripts' && columns.value.some((c) => c.key === 'run'));
function runIdOf(row: LogPage['rows'][number], c: LogColumn): number | null {
  if (!runLinks.value || c.key !== 'run') return null;
  const id = Number(row[c.key]);
  return Number.isInteger(id) && id > 0 ? id : null;
}
const runView = ref<RunDetail | null>(null);
const runError = ref<string | null>(null);
async function openRun(id: number) {
  runError.value = null;
  try { runView.value = await scriptsApi(props.api).run(id); }
  catch (e) { runError.value = errorText(e); }
}
watch(sourceId, () => { runView.value = null; runError.value = null; });

// ------------------------------------------------------------------ prune and retention (this computer only)

const ages = [7, 14, 30, 90, 180, 365];
const pruneAge = ref<number | 'all'>(90);
const pruning = ref<LogPrunePreview | null>(null);
const pruneBusy = ref(false);
const pruneDone = ref<string | null>(null);
const panel = ref<'prune' | 'retention' | null>(null);

function cutoff() { return pruneAge.value === 'all' ? { all: true } : { olderThanDays: pruneAge.value }; }

async function preview() {
  pruneBusy.value = true;
  error.value = null;
  pruneDone.value = null;
  try { pruning.value = await props.api.call<LogPrunePreview>('logs.prunePreview', { source: sourceId.value, ...cutoff() }); }
  catch (e) { error.value = errorText(e); }
  finally { pruneBusy.value = false; }
}

async function prune() {
  if (!pruning.value) return;
  pruneBusy.value = true;
  try {
    const r = await props.api.call<{ deleted: number }>('logs.prune', { source: pruning.value.source, before: pruning.value.cutoff, confirm: true });
    pruneDone.value = `Deleted ${r.deleted.toLocaleString()} ${r.deleted === 1 ? 'entry' : 'entries'} from ${pruning.value.title}.`;
    pruning.value = null;
    panel.value = null;
    await reload();
  } catch (e) { error.value = errorText(e); }
  finally { pruneBusy.value = false; }
}

const keepOptions = [7, 14, 30, 90, 180, 365, 730];
const prunable = computed(() => sources.value.filter((s) => s.prunable));
const saving = ref<string | null>(null);

async function setRetention(s: LogSourceInfo, days: number) {
  saving.value = s.id;
  error.value = null;
  try {
    const r = await props.api.call<{ retentionDays: number }>('logs.setRetention', { source: s.id, days });
    s.retentionDays = r.retentionDays;
  } catch (e) { error.value = errorText(e); }
  finally { saving.value = null; }
}

function keepLabel(days: number | null) { return !days ? 'Keep forever' : days % 365 === 0 ? `${days / 365} year${days === 365 ? '' : 's'}` : `${days} days`; }
</script>

<template>
  <component :is="page ? 'section' : Modal" v-bind="page ? { class: 'logs-page', 'aria-label': 'Logs' } : { title: 'Logs', full: true }" @close="emit('close')">
    <div class="logs" :class="{ page }" data-test="logs">
      <div class="bar">
        <label class="source">Log
          <select v-model="sourceId" data-test="logs-source">
            <optgroup v-for="[group, list] in groups" :key="group" :label="group">
              <option v-for="s in list" :key="s.id" :value="s.id">{{ s.title }}</option>
            </optgroup>
          </select>
        </label>
        <input v-model="text" class="search grow" type="search" placeholder="Search these entries" data-test="logs-text" />
        <button v-if="phone" type="button" class="filters-toggle" :class="{ on: filtersOpen }" :aria-expanded="filtersOpen" data-test="logs-filters-toggle"
          @click="filtersOpen = !filtersOpen">Filters{{ activeFilters ? ` (${activeFilters})` : '' }} {{ filtersOpen ? '▴' : '▾' }}</button>
        <template v-if="local">
          <button v-if="phone" type="button" class="actions-toggle" :class="{ on: actionsOpen }" aria-label="More: Prune, Retention" :aria-expanded="actionsOpen"
            data-test="logs-actions-toggle" @click="actionsOpen = !actionsOpen">⋯</button>
          <div v-show="!phone || actionsOpen" class="actions" :class="{ menu: phone }" data-test="logs-actions" @click="actionsOpen = false">
            <button v-if="source?.prunable" type="button" :class="{ on: panel === 'prune' }" data-test="logs-prune-open" @click="panel = panel === 'prune' ? null : 'prune'; pruning = null">Prune…</button>
            <button type="button" :class="{ on: panel === 'retention' }" data-test="logs-retention-open" @click="panel = panel === 'retention' ? null : 'retention'">Retention…</button>
          </div>
        </template>
      </div>
      <p v-if="source && !phone" class="about muted small" data-test="logs-about">{{ source.about }}</p>

      <!-- On a phone the filters (and the Sort by row) sit at the top of the list's scroller and scroll away with it, so
           the list keeps the sheet; at desktop width they stay here (the Teleport is off). -->
      <Teleport defer :to="`#${phoneToolsId}`" :disabled="!phone">
      <div v-show="!phone || filtersOpen" class="filters" data-test="logs-filters">
        <label v-if="source?.team">Team
          <select v-model="team" data-test="logs-team">
            <option value="">All teams</option>
            <option v-for="t in who.teams" :key="t" :value="t">{{ t }}</option>
          </select>
        </label>
        <label v-if="source?.member">Member
          <select v-model="whoPick" data-test="logs-member">
            <option value="">{{ appOffered ? 'Everyone' : 'All members' }}</option>
            <optgroup label="Members">
              <option v-for="m in membersOfTeam" :key="m.key" :value="m.key">{{ team ? m.name : m.key }}</option>
              <option v-if="member && !membersOfTeam.some((m) => m.key === member)" :value="member">{{ memberLabel(member) }}</option>
            </optgroup>
            <optgroup v-if="wholeTeams.length" label="Teams">
              <option v-for="t in wholeTeams" :key="t" :value="`${teamPrefix}${t}`" :data-test="`logs-member-team-${t}`">{{ t }} (whole team)</option>
            </optgroup>
            <option v-if="appOffered" value="app" data-test="logs-member-app">app (the app itself)</option>
          </select>
        </label>
        <label v-if="source?.levels">Level
          <select v-model="level" data-test="logs-level">
            <option value="">All but debug</option>
            <option value="error">Errors</option>
            <option value="warning">Warnings</option>
            <option value="info">Info</option>
            <option value="debug">Debug</option>
            <option value="all">All, debug too</option>
          </select>
        </label>
        <label v-for="c in filterColumns" :key="c.key">{{ c.label }}
          <select :value="filters[c.key] ?? ''" :data-test="`logs-filter-${c.key}`" @change="filters[c.key] = ($event.target as HTMLSelectElement).value">
            <option value="">All</option>
            <option v-for="v in values[c.key] ?? []" :key="v" :value="v">{{ v }}</option>
          </select>
        </label>
        <label>From <input v-model="fromDay" type="date" data-test="logs-from" /></label>
        <label>To <input v-model="toDay" type="date" data-test="logs-to" /></label>
        <button v-if="filtered" type="button" class="mini" data-test="logs-clear" @click="clearFilters">Clear filters</button>
      </div>
      <div v-if="phone" v-show="filtersOpen" class="phone-sort" data-test="logs-sort">
        <label>Sort by
          <select :value="sort" data-test="logs-sort-key" @change="sortBy(($event.target as HTMLSelectElement).value)">
            <option v-for="c in columns" :key="c.key" :value="c.key">{{ c.label }}</option>
          </select>
        </label>
        <button type="button" class="mini" :aria-label="descending ? 'Descending' : 'Ascending'" data-test="logs-sort-dir" @click="sortBy(sort)">{{ descending ? '▼' : '▲' }}</button>
      </div>
      </Teleport>

      <section v-if="panel === 'prune' && source?.prunable" class="panel" data-test="logs-prune">
        <div class="row">
          <span>Delete {{ source.title }} entries</span>
          <select v-model="pruneAge" data-test="logs-prune-age" @change="pruning = null">
            <option v-for="d in ages" :key="d" :value="d">older than {{ keepLabel(d) }}</option>
            <option value="all">all of them</option>
          </select>
          <button type="button" :disabled="pruneBusy" data-test="logs-prune-preview" @click="preview"><BusyLabel :busy="pruneBusy && !pruning" text="Checking…">Show what goes</BusyLabel></button>
        </div>
        <div v-if="pruning" class="confirm" data-test="logs-prune-confirm">
          <p v-if="pruning.count === 0">Nothing to delete: no {{ pruning.title }} entry is that old.</p>
          <template v-else>
            <p>
              This deletes <b>{{ pruning.count.toLocaleString() }}</b> {{ pruning.title }} {{ pruning.count === 1 ? 'entry' : 'entries' }},
              from {{ when(pruning.oldest!) }} to {{ when(pruning.newest!) }}<template v-if="pruning.files">, and {{ pruning.files.toLocaleString() }} {{ pruning.files === 1 ? 'file' : 'files' }}</template>.
              It cannot be undone.
            </p>
            <p><b>You lose</b> {{ pruning.loss }}</p>
            <div class="row">
              <button type="button" class="danger" :disabled="pruneBusy" data-test="logs-prune-yes" @click="prune"><BusyLabel :busy="pruneBusy" text="Deleting…">Delete {{ pruning.count.toLocaleString() }} {{ pruning.count === 1 ? 'entry' : 'entries' }}</BusyLabel></button>
              <button type="button" :disabled="pruneBusy" data-test="logs-prune-no" @click="pruning = null">Keep them</button>
            </div>
          </template>
        </div>
      </section>
      <p v-else-if="panel === 'prune' && source && !source.prunable" class="panel muted">{{ source.title }} is kept: {{ source.keptBecause }}.</p>

      <section v-if="panel === 'retention'" class="panel" data-test="logs-retention">
        <p class="muted small">Once a day the app deletes entries older than each log's retention. What a log loses is said beside it.</p>
        <table class="keep">
          <tbody>
            <tr v-for="s in prunable" :key="s.id" :data-test="`logs-keep-${s.id}`">
              <td class="name">{{ s.title }}</td>
              <td>
                <select :value="s.retentionDays ?? 0" :disabled="saving === s.id" :data-test="`logs-keep-select-${s.id}`" @change="setRetention(s, Number(($event.target as HTMLSelectElement).value))">
                  <option :value="0">Keep forever</option>
                  <option v-for="d in keepOptions" :key="d" :value="d">{{ keepLabel(d) }}</option>
                  <option v-if="s.retentionDays && !keepOptions.includes(s.retentionDays)" :value="s.retentionDays">{{ keepLabel(s.retentionDays) }}</option>
                </select>
              </td>
              <td class="muted small">Losing old entries loses {{ s.loss }}<template v-if="s.defaultDays"> Default: {{ keepLabel(s.defaultDays) }}.</template></td>
            </tr>
          </tbody>
        </table>
        <p class="muted small">Kept for good: {{ sources.filter((s) => !s.prunable).map((s) => `${s.title} (${s.keptBecause})`).join('; ') }}.</p>
      </section>

      <p v-if="pruneDone" class="ok small" data-test="logs-pruned">{{ pruneDone }}</p>
      <p v-if="error" class="error small" data-test="logs-error">{{ error }}</p>
      <p class="count muted small" data-test="logs-count">
        <template v-if="total !== null">{{ total.toLocaleString() }} {{ total === 1 ? 'entry' : 'entries' }}<template v-if="rows.length < total">, {{ rows.length.toLocaleString() }} shown</template></template>
      </p>

      <p v-if="runError" class="error small" data-test="logs-run-error">{{ runError }}</p>
      <section v-if="runView" class="run" data-test="logs-run">
        <div class="row"><button type="button" class="ghost" data-test="logs-run-back" @click="runView = null">← Script runs</button></div>
        <ScriptRunView :run="runView" :api="api" />
      </section>
      <div v-show="!runView" ref="scroller" class="scroll" data-test="logs-scroll" @scroll.passive="onScroll">
        <div :id="phoneToolsId" class="phone-tools" data-test="logs-phone-tools"></div>
        <template v-if="phone">
          <ol class="cards" data-test="logs-cards">
            <li v-for="r in rows" :key="String(r._id)" class="entry-card" :class="{ open: open === String(r._id) }" data-test="logs-row"
              role="button" tabindex="0" :aria-expanded="open === String(r._id)"
              @click="open = open === String(r._id) ? null : String(r._id)" @keydown.enter.prevent="open = open === String(r._id) ? null : String(r._id)">
              <dl>
                <template v-for="c in columns" :key="c.key">
                  <template v-if="open === String(r._id) || cell(r, c)">
                    <dt>{{ c.label }}</dt>
                    <dd :class="c.kind">
                      <button v-if="runIdOf(r, c)" type="button" class="link" :data-test="`logs-run-link-${runIdOf(r, c)}`" :aria-label="`Open script run ${runIdOf(r, c)}`"
                        @click.stop="openRun(runIdOf(r, c)!)" @keydown.enter.stop>{{ cell(r, c) }}</button>
                      <template v-else>{{ open === String(r._id) ? (r[c.key] ?? '') : cell(r, c) }}</template>
                    </dd>
                  </template>
                </template>
              </dl>
            </li>
          </ol>
        </template>
        <table v-else class="grid">
          <thead>
            <tr>
              <th v-for="c in columns" :key="c.key" :class="[c.kind, { sorted: sort === c.key }]" :aria-sort="sort === c.key ? (descending ? 'descending' : 'ascending') : 'none'">
                <button type="button" class="head" :data-test="`logs-sort-${c.key}`" @click="sortBy(c.key)">
                  {{ c.label }}<span class="arrow">{{ sort === c.key ? (descending ? '▼' : '▲') : '' }}</span>
                </button>
              </th>
            </tr>
          </thead>
          <tbody>
            <template v-for="r in rows" :key="String(r._id)">
              <tr class="entry" :class="{ open: open === String(r._id) }" data-test="logs-row" @click="open = open === String(r._id) ? null : String(r._id)">
                <td v-for="c in columns" :key="c.key" :class="c.kind" :title="r[c.key] === null ? '' : String(r[c.key])">
                  <button v-if="runIdOf(r, c)" type="button" class="link" :data-test="`logs-run-link-${runIdOf(r, c)}`" :aria-label="`Open script run ${runIdOf(r, c)}`"
                    @click.stop="openRun(runIdOf(r, c)!)">{{ cell(r, c) }}</button>
                  <template v-else>{{ cell(r, c) }}</template>
                </td>
              </tr>
              <tr v-if="open === String(r._id)" class="detail" data-test="logs-detail">
                <td :colspan="columns.length">
                  <dl>
                    <template v-for="c in columns" :key="c.key">
                      <dt>{{ c.label }}</dt><dd>{{ r[c.key] ?? '' }}</dd>
                    </template>
                  </dl>
                </td>
              </tr>
            </template>
          </tbody>
        </table>
        <LoadingNote v-if="loading" text="Loading entries…" />
        <p v-else-if="!rows.length && !error" class="empty muted" data-test="logs-empty">{{ filtered ? 'No entries match these filters.' : 'This log has no entries.' }}</p>
        <button v-else-if="next !== null" type="button" class="mini more" data-test="logs-more" @click="more">Load more</button>
      </div>
    </div>
  </component>
</template>

<style scoped>
.logs { display: flex; flex-direction: column; min-height: 0; flex: 1 1 0; gap: 8px; }
.logs-page { flex: 1 1 0; height: 100%; min-height: 0; display: flex; flex-direction: column; }
.logs.page { padding: 12px 16px; }
.bar, .filters, .row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.bar .source select { margin-left: 6px; min-width: 200px; }
.bar .search { min-width: 200px; }
.bar button.on { background: var(--hover); }
/* Prune… and Retention… are the bar's own buttons at desktop width (the wrapper takes no box); on a phone, the ⋯ menu. */
.actions { display: contents; }
.run { flex: 1 1 0; min-height: 0; overflow: auto; }
button.link { background: none; border: 0; padding: 0; color: var(--accent, inherit); text-decoration: underline; cursor: pointer; font: inherit; }
.filters label { display: inline-flex; align-items: center; gap: 4px; font-size: 13px; }
.filters select { max-width: 220px; }
.about { margin: 0; }
.panel { border: 1px solid var(--line); border-radius: 6px; padding: 8px 10px; margin: 0; }
.confirm { margin-top: 8px; }
.confirm p { margin: 4px 0; }
.keep td { padding: 3px 8px 3px 0; vertical-align: top; }
.keep .name { white-space: nowrap; font-weight: 600; }
.count { margin: 0; min-height: 1.2em; }
.ok { color: var(--ok); margin: 0; }
.scroll { flex: 1 1 0; min-height: 120px; overflow: auto; border: 1px solid var(--line); border-radius: 6px; }
.grid { width: 100%; border-collapse: collapse; font-size: 12.5px; }
.grid thead th { position: sticky; top: 0; background: var(--bg); z-index: 1; text-align: left; border-bottom: 1px solid var(--line); padding: 0; white-space: nowrap; }
.grid th .head { width: 100%; text-align: left; border: 0; background: transparent; padding: 6px 8px; font-weight: 600; cursor: pointer; }
.grid th.number .head, .grid td.number { text-align: right; }
.grid th.sorted .head { color: var(--accent); }
.arrow { margin-left: 4px; font-size: 10px; }
.grid td { padding: 4px 8px; border-bottom: 1px solid var(--line); max-width: 420px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.grid td.time { white-space: nowrap; }
.grid tr.entry { cursor: pointer; }
.grid tr.entry:hover, .grid tr.entry.open { background: var(--hover); }
.detail td { white-space: normal; max-width: none; background: var(--hover); }
.detail dl { display: grid; grid-template-columns: max-content 1fr; gap: 2px 12px; margin: 4px 0; }
.detail dt { font-weight: 600; }
.detail dd { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: var(--mono, ui-monospace, monospace); font-size: 12px; }
.empty { padding: 16px; margin: 0; }
.more { margin: 8px; }
.phone-sort { display: flex; align-items: center; gap: 6px; padding: 6px 8px; background: var(--bg); border-bottom: 1px solid var(--line); font-size: 13px; }
.phone-sort label { flex: 1; display: flex; align-items: center; gap: 6px; }
.phone-sort select { flex: 1; min-width: 0; }
.cards { list-style: none; margin: 0; padding: 6px; display: flex; flex-direction: column; gap: 6px; }
.entry-card { border: 1px solid var(--line); border-radius: 8px; padding: 8px 10px; background: var(--surface); cursor: pointer; font-size: 12.5px; }
.entry-card.open { background: var(--hover); }
.entry-card dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 2px 10px; margin: 0; }
.entry-card dt { color: var(--muted); font-weight: 600; }
.entry-card dd { margin: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.entry-card.open dd { white-space: pre-wrap; word-break: break-word; overflow: visible; font-family: var(--mono, ui-monospace, monospace); font-size: 12px; }
/* A phone (B037): the bars wrap into full-width rows, labels over their controls, tap targets 44 px. */
@media (max-width: 759.98px) {
  /* B057: one row in landscape (Log, Filters, ⋯, search), two in portrait (the search on its own row under them). */
  .bar { position: relative; }
  .bar .source { order: 0; flex: 1 1 180px; min-width: 0; }
  .filters-toggle { order: 1; flex: none; white-space: nowrap; }
  .actions-toggle { order: 2; flex: none; min-width: 44px; }
  .bar .search { order: 3; flex: 1 1 200px; min-width: 0; }
  .actions.menu { position: absolute; right: 0; top: calc(100% + 4px); z-index: 3; display: flex; flex-direction: column; align-items: stretch; gap: 4px;
    padding: 6px; background: var(--surface); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 6px 20px var(--shadow); }
  .bar .source { display: flex; align-items: center; gap: 6px; }
  .bar .source select { flex: 1; min-width: 0; margin-left: 0; }
  .filters label { flex: 1 1 calc(50% - 8px); flex-direction: column; align-items: stretch; }
  .filters select { max-width: none; }
  button, select, input { min-height: 44px; box-sizing: border-box; }
  .mini { min-width: 44px; }
  .keep td { display: block; padding: 2px 0; }
  /* B057: the list fills what the sheet has left (no fixed height); the filters and Sort by scroll away at its top,
     Load more is at its end. */
  .scroll { flex: 1 1 0; min-height: 0; }
  .phone-tools .filters { padding: 8px; border-bottom: 1px solid var(--line); }
  .phone-sort { border-bottom: 1px solid var(--line); }
}
</style>
