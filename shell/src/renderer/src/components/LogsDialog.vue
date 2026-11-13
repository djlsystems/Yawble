<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import type { LogColumn, LogPage, LogPrunePreview, LogSourceInfo, LogWho, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import Modal from './Modal.vue';
import BusyLabel from './BusyLabel.vue';
import LoadingNote from './LoadingNote.vue';

/**
 * Logs: every log the app keeps, one at a time, as a table. The source is chosen from a list; the team, the member,
 * the source's own columns, a time range and free text filter it; any column sorts it; pages load as the table
 * scrolls. On this computer, Prune deletes old entries after saying exactly what goes, and Retention sets how long
 * each log is kept. A cloud viewer reads only. Opened from the sidebar, or from a member's card already filtered to
 * that member's activity.
 */
const props = defineProps<{ api: YawbleApi; source?: string; team?: string; member?: string }>();
const emit = defineEmits<{ close: [] }>();

const pageSize = 100;
const sources = ref<LogSourceInfo[]>([]);
const who = ref<LogWho>({ teams: [], members: [] });
const sourceId = ref(props.source ?? 'activity');
const team = ref(props.member ? props.member.slice(0, props.member.indexOf('/')) : props.team ?? '');
const member = ref(props.member ?? '');
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
const columns = computed<LogColumn[]>(() => source.value?.columns ?? []);
const filterColumns = computed(() => columns.value.filter((c) => c.filter && !(c.key === 'team' && source.value?.team)));
const groups = computed(() => {
  const by = new Map<string, LogSourceInfo[]>();
  for (const s of sources.value) by.set(s.group, [...(by.get(s.group) ?? []), s]);
  return [...by.entries()];
});
const membersOfTeam = computed(() => who.value.members.filter((m) => !team.value || m.team === team.value));
const local = computed(() => !props.api.remote);
const filtered = computed(() => !!(team.value || member.value || fromDay.value || toDay.value || text.value.trim() || Object.values(filters).some((v) => v)));

function query(offset: number) {
  const f: Record<string, string> = {};
  for (const [k, v] of Object.entries(filters)) if (v && columns.value.some((c) => c.key === k)) f[k] = v;
  return {
    source: sourceId.value,
    team: source.value?.team && !member.value ? team.value || undefined : undefined,
    member: source.value?.member ? member.value || undefined : undefined,
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
    try { values[c.key] = await props.api.call<string[]>('logs.values', { source: sourceId.value, column: c.key, team: member.value ? undefined : team.value || undefined, member: member.value || undefined }); }
    catch { values[c.key] = []; }
  }
}

function clearFilters() {
  team.value = ''; member.value = ''; fromDay.value = ''; toDay.value = ''; text.value = '';
  for (const k of Object.keys(filters)) filters[k] = '';
}

// A new source: its own column filters start empty; sort by its first column, newest first.
watch(sourceId, () => {
  for (const k of Object.keys(filters)) delete filters[k];
  sort.value = columns.value[0]?.key ?? 'at';
  descending.value = true;
  pruning.value = null;
  void loadValues();
  void reload();
});
watch(team, () => { if (member.value && !member.value.startsWith(`${team.value}/`)) member.value = ''; });
watch([team, member], () => { void loadValues(); });
watch([team, member, fromDay, toDay, () => ({ ...filters })], () => { void reload(); }, { deep: true });
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
  <Modal title="Logs" full @close="emit('close')">
    <div class="logs" data-test="logs">
      <div class="bar">
        <label class="source">Log
          <select v-model="sourceId" data-test="logs-source">
            <optgroup v-for="[group, list] in groups" :key="group" :label="group">
              <option v-for="s in list" :key="s.id" :value="s.id">{{ s.title }}</option>
            </optgroup>
          </select>
        </label>
        <input v-model="text" class="search grow" type="search" placeholder="Search these entries" data-test="logs-text" />
        <template v-if="local">
          <button v-if="source?.prunable" type="button" :class="{ on: panel === 'prune' }" data-test="logs-prune-open" @click="panel = panel === 'prune' ? null : 'prune'; pruning = null">Prune…</button>
          <button type="button" :class="{ on: panel === 'retention' }" data-test="logs-retention-open" @click="panel = panel === 'retention' ? null : 'retention'">Retention…</button>
        </template>
      </div>
      <p v-if="source" class="about muted small" data-test="logs-about">{{ source.about }}</p>

      <div class="filters">
        <label v-if="source?.team">Team
          <select v-model="team" data-test="logs-team">
            <option value="">All teams</option>
            <option v-for="t in who.teams" :key="t" :value="t">{{ t }}</option>
          </select>
        </label>
        <label v-if="source?.member">Member
          <select v-model="member" data-test="logs-member">
            <option value="">All members</option>
            <option v-for="m in membersOfTeam" :key="m.key" :value="m.key">{{ team ? m.name : m.key }}</option>
            <option v-if="member && !membersOfTeam.some((m) => m.key === member)" :value="member">{{ memberLabel(member) }}</option>
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

      <div ref="scroller" class="scroll" data-test="logs-scroll" @scroll.passive="onScroll">
        <table class="grid">
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
                <td v-for="c in columns" :key="c.key" :class="c.kind" :title="r[c.key] === null ? '' : String(r[c.key])">{{ cell(r, c) }}</td>
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
  </Modal>
</template>

<style scoped>
.logs { display: flex; flex-direction: column; min-height: 0; flex: 1 1 0; gap: 8px; }
.bar, .filters, .row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.bar .source select { margin-left: 6px; min-width: 200px; }
.bar .search { min-width: 200px; }
.bar button.on { background: var(--hover); }
.filters label { display: inline-flex; align-items: center; gap: 4px; font-size: 13px; }
.filters select { max-width: 220px; }
.about { margin: 0; }
.panel { border: 1px solid var(--line); border-radius: 6px; padding: 8px 10px; margin: 0; }
.confirm { margin-top: 8px; }
.confirm p { margin: 4px 0; }
.keep td { padding: 3px 8px 3px 0; vertical-align: top; }
.keep .name { white-space: nowrap; font-weight: 600; }
.count { margin: 0; min-height: 1.2em; }
.ok { color: var(--ok, #1d6b2f); margin: 0; }
.scroll { flex: 1 1 0; min-height: 120px; overflow: auto; border: 1px solid var(--line); border-radius: 6px; }
.grid { width: 100%; border-collapse: collapse; font-size: 12.5px; }
.grid thead th { position: sticky; top: 0; background: var(--panel, #f6f7f9); z-index: 1; text-align: left; border-bottom: 1px solid var(--line); padding: 0; white-space: nowrap; }
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
</style>
