<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import type { ChoiceState, DiscoverChecklistData, DiscoverJobData, DiscoverNode, YawbleApi, RowState, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';

/**
 * Discover: the checklist of one CLI's commands (or one MCP server's tools). The app reads the help (or the tool list),
 * classifies each row, and the person sets each one to allowed, ask first or off; Save turns the changed rows into ordinary
 * rules. A row that would reveal the sign-in shows "never" and is locked (the server refuses it too, whatever is sent here).
 * A team can override the default list ("Every team") and reset the override to the default.
 */
const props = defineProps<{
  api?: YawbleApi; cli: string; kind?: 'cli' | 'mcp' | 'api'; teams: TeamSummary[]; teamIds?: number[]; all?: boolean; initialScope?: number | null; autoStart?: boolean;
}>();
const emit = defineEmits<{ (e: 'discovered'): void }>();
const api = resolveApi(props.api);
const kind = computed(() => props.kind ?? 'cli');
/** An API's spec is fetched by the computer itself: a remote view does not offer Discover for it (the core refuses it too). */
const canDiscover = computed(() => !(kind.value === 'api' && api.remote));

type Shown = RowState | 'undecided';

const scope = ref<number | null>(props.initialScope !== undefined ? props.initialScope : (props.teamIds?.[0] ?? props.teams[0]?.id ?? null));
const data = ref<DiscoverChecklistData | null>(null);
const job = ref<DiscoverJobData | null>(null);
const error = ref<string | null>(null);
const saved = ref<string | null>(null);
/** Only the rows the person (or the first-discover pre-fill) changed: path -> the state to save. */
const choice = ref(new Map<string, ChoiceState>());
const collapsed = ref(new Set<string>());
const query = ref('');
const onlyTicked = ref(false);
const onlyFresh = ref(false);
const saving = ref(false);
/** The checklist's first read has answered (until then: the loading state, not the empty "Discover" text). */
const loaded = ref(false);
/** Discover clicked and the core has not started the walk yet; Cancel clicked and not answered; a rule being removed. */
const starting = ref(false);
const cancelling = ref(false);
const removingRule = ref<number | null>(null);
let timer: ReturnType<typeof setTimeout> | null = null;
let disposed = false;

const running = computed(() => job.value?.state === 'running');
/** The checklist's rows. An API's actions are grouped by tag (one group per tag, "Other" for none) when the core sends tags. */
const nodes = computed<DiscoverNode[]>(() => {
  const raw = data.value?.nodes ?? [];
  if (kind.value !== 'api' || !raw.some((n) => n.tag)) return raw;
  const untagged = '\u0000untagged'; // its own key, so a tag literally named "Other" stays a separate group
  const groups = new Map<string, DiscoverNode[]>();
  for (const n of raw) { const t = n.tag || untagged; const l = groups.get(t) ?? []; l.push(n); groups.set(t, l); }
  const out: DiscoverNode[] = [];
  for (const [t, list] of [...groups].sort((a, b) => (a[0] === untagged ? 1 : b[0] === untagged ? -1 : a[0].localeCompare(b[0])))) {
    const gp = `tag: ${t}`;
    out.push({ ...list[0]!, path: gp, parent: '', name: t === untagged ? 'Other' : t, depth: 1, summary: '', hasChildren: true, class: 'group', state: 'free', ticked: false, gone: false, badge: null, tag: null, method: null, route: null });
    for (const n of list) out.push({ ...n, parent: gp, depth: 2 });
  }
  return out;
});
const unit = computed(() => (kind.value === 'mcp' ? 'tools' : kind.value === 'api' ? 'actions' : 'commands'));
/** "GET /pets" for an API action, from the core's method and route (not repeated when the action id already reads that way). */
function routeText(n: DiscoverNode) {
  if (kind.value !== 'api' || !n.method || !n.route) return null;
  const t = `${n.method} ${n.route}`;
  return t === n.path ? null : t;
}
const byPath = computed(() => new Map(nodes.value.map((n) => [n.path, n])));
const scopeName = computed(() => (scope.value === null ? 'every team' : props.teams.find((t) => t.id === scope.value)?.name ?? `team ${scope.value}`));
const offeredTeams = computed(() => (props.all || data.value?.offeredAll ? props.teams : props.teams.filter((t) => data.value?.offeredTeams.includes(t.id))));

function fresh(n: DiscoverNode) { return n.badge === 'new' || n.badge === 'changed'; }
const freshCount = computed(() => (data.value?.new ?? 0) + (data.value?.changed ?? 0));
const dirtyCount = computed(() => choice.value.size);
const empty = computed(() => !data.value || (nodes.value.length === 0 && (data.value.runs ?? 0) === 0));

// Lockability comes only from the suggestion / effective state (credential and floor rows), never from the legacy `state`.
const isLocked = (n: DiscoverNode) => n.suggested === 'never' || n.effective === 'never';
const fromDecision = (d: 'allow' | 'ask' | 'deny'): RowState => (d === 'deny' ? 'off' : d);
/** What the server says the row is now. */
function baseState(n: DiscoverNode): Shown {
  if (isLocked(n)) return 'never';
  if (n.effective === 'ask' && n.defaultDecision === null && n.teamDecision === null) return 'undecided';
  return fromDecision(n.effective as 'allow' | 'ask' | 'deny');
}
/** What the row is once a team's override is reset. */
function defaultState(n: DiscoverNode): Shown {
  return n.defaultDecision === null ? 'undecided' : fromDecision(n.defaultDecision);
}
function shown(n: DiscoverNode): Shown {
  if (isLocked(n)) return 'never';
  const c = choice.value.get(n.path);
  if (c === undefined) return baseState(n);
  return c === 'default' ? defaultState(n) : c;
}
const isOn = (n: DiscoverNode) => { const s = shown(n); return s === 'allow' || s === 'ask'; };
const pillText = (s: Shown) => ({ allow: 'allowed', ask: 'ask first', off: 'off', never: 'never', undecided: 'not decided (asks you)' })[s];
const hasOverride = (n: DiscoverNode) => scope.value !== null && n.teamDecision !== null && choice.value.get(n.path) !== 'default';
const fromEvery = (n: DiscoverNode) => scope.value !== null && n.teamDecision === null && !choice.value.has(n.path);
const key = (p: string) => p.replace(/ /g, '_');

function setChoice(n: DiscoverNode, st: ChoiceState) {
  if (isLocked(n) || n.hasChildren) return; // the server refuses it as well
  const m = new Map(choice.value);
  if (st === baseState(n) || (st === 'default' && n.teamDecision === null)) m.delete(n.path); else m.set(n.path, st);
  choice.value = m;
}

const children = computed(() => {
  const m = new Map<string, DiscoverNode[]>();
  for (const n of nodes.value) { const l = m.get(n.parent) ?? []; l.push(n); m.set(n.parent, l); }
  return m;
});

function leavesUnder(path: string): DiscoverNode[] {
  const out: DiscoverNode[] = [];
  for (const c of children.value.get(path) ?? []) { if (c.hasChildren) out.push(...leavesUnder(c.path)); else out.push(c); }
  return out;
}

const searching = computed(() => query.value.trim().length > 0);

/** The rows to show, depth first. Searching keeps the matches and their groups, and opens them. */
const rows = computed(() => {
  const q = query.value.trim().toLowerCase();
  const show = (n: DiscoverNode): boolean => {
    if (n.hasChildren) return (children.value.get(n.path) ?? []).some(show);
    if (q && !(n.path.toLowerCase().includes(q) || n.summary.toLowerCase().includes(q))) return false;
    if (onlyTicked.value && !isOn(n)) return false;
    if (onlyFresh.value && !fresh(n)) return false;
    return true;
  };
  const out: DiscoverNode[] = [];
  const walk = (parent: string) => {
    for (const n of children.value.get(parent) ?? []) {
      if (!show(n)) continue;
      out.push(n);
      if (n.hasChildren && (searching.value || !collapsed.value.has(n.path))) walk(n.path);
    }
  };
  walk('');
  return out;
});

const gone = computed(() => nodes.value.filter((n) => n.gone && !n.hasChildren));

const footer = computed(() => {
  let a = 0, k = 0;
  for (const n of nodes.value) {
    if (n.hasChildren || n.gone) continue;
    const s = shown(n);
    if (s === 'allow') a++; else if (s === 'ask') k++;
  }
  return `${a + k} offered: ${a} allowed, ${k} ask first`;
});

function label(n: DiscoverNode) {
  if (isLocked(n)) return 'Locked';
  return ({ readonly: 'Read-only', change: 'Changes', destructive: 'Destructive', unknown: 'Unclassified' } as Record<string, string>)[n.class] ?? '';
}
function why(n: DiscoverNode) {
  const bits = [`${label(n) || 'Group'}: ${n.reason || 'no reason recorded'}`];
  if (isLocked(n)) bits.push('This can never be allowed.');
  if (n.source === 'model') bits.push('Suggested by the model; check it.');
  if (n.summary) bits.push(n.summary);
  if (n.seenVersion) bits.push(`Last seen in version ${n.seenVersion}.`);
  return bits.join(' ');
}
const badgeClass = (n: DiscoverNode) => (isLocked(n) ? 'locked' : n.class);

function toggle(n: DiscoverNode, on: boolean) {
  if (!on) { setChoice(n, 'off'); return; }
  setChoice(n, n.suggested === 'allow' || n.suggested === 'ask' ? n.suggested : 'ask');
}
function tickReads(path: string | null, onlyFreshOnes = false) {
  const list = path === null ? nodes.value.filter((n) => !n.hasChildren) : leavesUnder(path);
  for (const n of list) if (n.class === 'readonly' && !isLocked(n) && !n.gone && (!onlyFreshOnes || fresh(n)) && (shown(n) === 'off' || shown(n) === 'undecided')) setChoice(n, 'allow'); // never loosens a row that is already on (ask first stays)
}
function untickAll(path: string | null) {
  const list = path === null ? nodes.value.filter((n) => !n.hasChildren) : leavesUnder(path);
  for (const n of list) if (!isLocked(n) && !n.gone && shown(n) !== 'off') setChoice(n, 'off');
}
function fold(path: string) {
  const s = new Set(collapsed.value);
  if (s.has(path)) s.delete(path); else s.add(path);
  collapsed.value = s;
}
const groupCount = (n: DiscoverNode) => leavesUnder(n.path).length;

async function load() {
  try {
    const d = await api.call<DiscoverChecklistData>('integrations.checklist', { kind: kind.value, name: props.cli, teamId: scope.value });
    data.value = d;
    // first discover: undecided rows start on what the app suggests, so Save writes them, in the default list ("Every team") only:
    // a team's override is never pre-filled. Later new rows stay undecided until touched.
    const m = new Map<string, ChoiceState>();
    if (scope.value === null && (d.runs === 1 || d.choices === 0)) {
      for (const n of d.nodes) {
        if (n.hasChildren || n.gone || isLocked(n) || n.badge === 'new' || baseState(n) !== 'undecided') continue;
        if (n.suggested && n.suggested !== 'never') m.set(n.path, n.suggested);
      }
    }
    choice.value = m;
    // a re-discover opens on what is new or changed
    if (d.runs > 1 && (d.new + d.changed) > 0) onlyFresh.value = true;
    error.value = null;
  } catch (e) {
    data.value = null; // not discovered, or the CLI is not set up yet
    error.value = null;
    if (!/not set up/i.test(errorText(e))) error.value = errorText(e);
  } finally {
    loaded.value = true;
  }
}

let watched = false; // a walk was seen running: its end is told to the dialog (which re-reads the sign-in status)
async function poll() {
  try {
    job.value = await api.call<DiscoverJobData>('integrations.discoverStatus', { kind: kind.value, name: props.cli });
  } catch (e) { error.value = errorText(e); return; }
  if (disposed) return;
  if (job.value?.state === 'running') { watched = true; timer = setTimeout(poll, 700); return; }
  if (job.value?.state === 'done' || job.value?.state === 'failed') await load();
  if (watched) { watched = false; emit('discovered'); }
}

/** Removes a rule the checklist has no row for (a group's prefix, a second rule for a row, a typed pattern), then reloads. */
async function removeRule(id: number) {
  if (removingRule.value !== null) return;
  error.value = null;
  removingRule.value = id;
  try {
    await api.call(kind.value === 'api' ? 'integrations.apiDeleteRule' : kind.value === 'mcp' ? 'integrations.mcpDeleteRule' : 'integrations.deleteRule', { id });
    await load();
  } catch (e) { error.value = errorText(e); } finally { removingRule.value = null; }
}

async function start() {
  if (starting.value) return;
  saved.value = null;
  error.value = null;
  starting.value = true;
  try {
    job.value = await api.call<DiscoverJobData>('integrations.discover', { kind: kind.value, name: props.cli });
    watched = true; // this walk's end is told even if it ends before the first poll
    starting.value = false; // from here the walk's own progress line shows it
    await poll();
  } catch (e) { error.value = errorText(e); } finally { starting.value = false; }
}
async function cancel() {
  if (cancelling.value) return;
  cancelling.value = true;
  try { await api.call('integrations.discoverCancel', { kind: kind.value, name: props.cli }); await poll(); } catch (e) { error.value = errorText(e); } finally { cancelling.value = false; }
}

/** Saves the changed rows; true when they were saved, false (with the error shown) when not. */
async function save(): Promise<boolean> {
  if (saving.value) return false;
  saving.value = true;
  error.value = null;
  try {
    const choices = [...choice.value].filter(([p]) => { const n = byPath.value.get(p); return n !== undefined && !isLocked(n); }).map(([path, state]) => ({ path, state }));
    const r = await api.call<{ allowed: number; asked: number; denied: number; removed: number }>('integrations.saveChecklist', { kind: kind.value, name: props.cli, teamId: scope.value, choices });
    saved.value = `${r.allowed + r.asked + r.denied} rules saved for ${scopeName.value}`;
    await load();
    return true;
  } catch (e) { error.value = errorText(e); return false; }
  finally { saving.value = false; }
}

function changeScope(v: string) {
  if (dirtyCount.value > 0 && !window.confirm('You have unsaved changes. Switch the scope and lose them?')) return;
  scope.value = v === '' ? null : Number(v);
  saved.value = null;
  void load();
}

const warn = (e: BeforeUnloadEvent) => { if (dirtyCount.value > 0) e.preventDefault(); };
onMounted(async () => {
  window.addEventListener('beforeunload', warn);
  await load();
  await poll(); // a walk may already be running
  if (props.autoStart && !disposed && empty.value && (job.value === null || job.value.state === 'idle')) await start();
});
onBeforeUnmount(() => { disposed = true; if (timer) clearTimeout(timer); window.removeEventListener('beforeunload', warn); });
watch(() => props.cli, () => { void load(); });
defineExpose({ dirtyCount, start, save });
</script>

<template>
  <div class="discover" :data-test="`discover-${cli}`">
    <div class="row head">
      <button v-if="canDiscover" :disabled="running || starting || !loaded" :data-test="`discover-start-${cli}`" :aria-busy="starting" @click="start"><BusyLabel :busy="starting" text="Starting…">{{ empty ? 'Discover' : 'Re-discover' }}</BusyLabel></button>
      <span v-else class="muted small" data-test="discover-remote">Discover runs on the computer itself.</span>
      <span v-if="running" class="small" data-test="discover-progress">
        {{ job?.found ?? 0 }} {{ unit }} found &middot; {{ Math.round(job?.seconds ?? 0) }} s
        <button class="mini" data-test="discover-cancel" :disabled="cancelling" :aria-busy="cancelling" @click="cancel"><BusyLabel :busy="cancelling" text="Cancelling…">Cancel</BusyLabel></button>
      </span>
      <span v-else-if="job?.state === 'failed' || job?.error" class="error small" data-test="discover-failed">{{ job?.error }}</span>
      <LoadingNote v-else-if="!loaded" text="Loading the checklist…" />
      <span v-else-if="empty" class="muted small">{{ kind === 'api' ? "Reads the service's description and lists what it can do, so you can choose what agents may do." : kind === 'mcp' ? "Lists the server's tools, so you can choose what agents may run." : "Reads the command's own help and lists what it can do, so you can choose what agents may run." }}</span>
    </div>
    <p v-if="error" class="error" data-test="discover-error">{{ error }}</p>

    <template v-if="data && !empty">
      <p v-if="data.error" class="warn small" data-test="discover-unreadable">Could not read its help: {{ data.error }}. Your earlier list is kept.</p>
      <p v-if="data.partial" class="warn small" data-test="discover-partial">The list is partial{{ data.partialReason ? ` (${data.partialReason})` : '' }}. Nothing was marked removed.</p>
      <p v-if="data.runs > 1" class="banner small" data-test="discover-banner">
        {{ data.new }} new, {{ data.changed }} changed, {{ data.gone }} removed. Your {{ data.choices }} choices are kept.
        <button v-if="freshCount" class="mini" data-test="discover-tick-new" @click="tickReads(null, true)">Allow the new read-only ones</button>
      </p>

      <div class="row bar">
        <label class="small">Scope
          <select :value="scope === null ? '' : String(scope)" data-test="discover-scope" @change="changeScope(($event.target as HTMLSelectElement).value)">
            <option value="">Every team (default)</option>
            <option v-for="t in offeredTeams" :key="t.id" :value="String(t.id)">Team: {{ t.name }}</option>
          </select>
        </label>
        <input v-model="query" class="grow" placeholder="Search" aria-label="Search" data-test="discover-search" />
        <label class="check small"><input v-model="onlyTicked" type="checkbox" data-test="discover-only-ticked" /> only offered</label>
        <label v-if="freshCount" class="check small"><input v-model="onlyFresh" type="checkbox" data-test="discover-only-fresh" /> new / changed ({{ freshCount }})</label>
        <button class="mini" data-test="tick-reads-all" @click="tickReads(null)">tick all read-only</button>
        <button class="mini" data-test="untick-all" @click="untickAll(null)">untick all</button>
        <button class="primary" :disabled="saving || dirtyCount === 0" data-test="discover-save" :aria-busy="saving" @click="save"><BusyLabel :busy="saving" text="Saving…">Save{{ dirtyCount ? ` (${dirtyCount} change${dirtyCount === 1 ? '' : 's'})` : '' }}</BusyLabel></button>
      </div>
      <p v-if="saved" class="ok small" data-test="discover-saved">{{ saved }}</p>

      <div class="list" role="tree" data-test="discover-list">
        <div v-for="n in rows" :key="n.path" class="node" :class="{ group: n.hasChildren, gone: n.gone, locked: !n.hasChildren && isLocked(n) }" :style="{ paddingLeft: `${(n.depth - 1) * 18 + 4}px` }"
          :data-test="`node-${key(n.path)}`" role="treeitem">
          <template v-if="n.hasChildren">
            <button class="fold" :aria-label="`${collapsed.has(n.path) && !searching ? 'Open' : 'Close'} ${n.path}`" @click="fold(n.path)">{{ collapsed.has(n.path) && !searching ? '+' : '-' }}</button>
            <b class="name">{{ n.name }}</b>
            <span class="muted small">({{ groupCount(n) }} {{ unit }})</span>
            <button class="mini" :data-test="`tick-reads-${key(n.path)}`" @click="tickReads(n.path)">tick all read-only</button>
            <button class="mini" :data-test="`untick-all-${key(n.path)}`" @click="untickAll(n.path)">untick all</button>
          </template>
          <template v-else>
            <input type="checkbox" :checked="isOn(n)" :disabled="isLocked(n)" :aria-describedby="`why-${key(n.path)}`"
              :aria-label="n.path" :title="why(n)" :data-test="`tick-${key(n.path)}`" @change="toggle(n, ($event.target as HTMLInputElement).checked)" />
            <span class="mono name" :title="why(n)">{{ n.path }}</span>
            <select v-if="isOn(n)" class="state" :value="shown(n)" :aria-label="`${n.path} state`" :data-test="`state-${key(n.path)}`"
              @change="setChoice(n, ($event.target as HTMLSelectElement).value as ChoiceState)">
              <option value="allow">allowed</option>
              <option value="ask">ask first</option>
            </select>
            <span v-else class="pill" :class="shown(n)" :data-test="`pill-${key(n.path)}`">{{ pillText(shown(n)) }}</span>
            <span class="badge" :class="badgeClass(n)" :title="why(n)" :data-test="`badge-${key(n.path)}`">{{ isLocked(n) ? '\u{1F512} ' : '' }}{{ label(n) }}</span>
            <span v-if="n.badge === 'new' || n.badge === 'changed'" class="mark" :class="n.badge" data-test="mark">{{ n.badge === 'new' ? 'New' : 'Changed' }}</span>
            <span v-if="n.origin === 'found'" class="tag" data-test="tag-found">found in use</span>
            <span v-else-if="n.origin === 'builtin'" class="tag" data-test="tag-builtin">built-in</span>
            <span v-if="hasOverride(n)" class="small override">
              set for {{ scopeName }}
              <button class="mini" :data-test="`reset-${key(n.path)}`" @click="setChoice(n, 'default')">reset to default</button>
            </span>
            <span v-else-if="scope !== null && n.teamDecision === null && choice.has(n.path)" class="small override" :data-test="`pending-${key(n.path)}`">will be set for {{ scopeName }}</span>
            <span v-else-if="fromEvery(n)" class="muted small">from every team</span>
            <span class="muted small summary">{{ n.summary }}</span>
            <span v-if="routeText(n)" class="muted small mono route" data-test="route">{{ routeText(n) }}</span>
            <span :id="`why-${key(n.path)}`" class="sr">{{ why(n) }}</span>
          </template>
        </div>
        <p v-if="rows.length === 0" class="muted small" data-test="discover-none">Nothing matches.</p>
      </div>
      <p class="small muted footer" data-test="discover-footer">{{ footer }}</p>

      <template v-if="gone.length">
        <h4>No longer in this version</h4>
        <p class="small mono" data-test="discover-gone"><span v-for="g in gone" :key="g.path" class="muted">{{ g.path }}&nbsp; </span></p>
      </template>
      <template v-if="data.otherRules.length">
        <h4>Other rules</h4>
        <ul class="small mono other" data-test="discover-other">
          <li v-for="r in data.otherRules" :key="r.id">
            <span :class="r.decision">{{ r.pattern }} &rarr; {{ r.decision }}</span>
            <span class="muted">{{ r.teamId === null ? 'every team' : (teams.find((t) => t.id === r.teamId)?.name ?? `team ${r.teamId}`) }}</span>
            <button class="mini" :data-test="`other-remove-${r.id}`" :aria-label="`Remove rule ${r.pattern}`" :disabled="removingRule !== null" @click="removeRule(r.id)"><BusyLabel :busy="removingRule === r.id" text="Removing…">Remove</BusyLabel></button>
          </li>
        </ul>
      </template>
    </template>
  </div>
</template>

<style scoped>
.discover { margin-top: 8px; border-top: 1px solid var(--line-soft); padding-top: 6px; }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.bar { margin: 8px 0; }
.grow { flex: 1; min-width: 120px; }
.list { max-height: 420px; overflow: auto; border: 1px solid var(--line-soft); border-radius: 8px; padding: 4px; }
.node { display: flex; gap: 8px; align-items: center; padding: 2px 4px; font-size: 13px; }
.node.gone { opacity: 0.55; }
.node.locked { opacity: 0.6; }
.name { white-space: nowrap; }
.summary { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.state { font-size: 12px; }
.pill { font-size: 11px; padding: 0 6px; border-radius: 8px; border: 1px solid var(--line); color: var(--muted); }
.pill.never { background: var(--line-soft); }
.pill.undecided { color: var(--warn); border-color: var(--warn); }
.badge { font-size: 11px; padding: 0 6px; border-radius: 8px; border: 1px solid var(--line); }
.badge.readonly { color: var(--ok); border-color: var(--ok); }
.badge.change { color: var(--warn); border-color: var(--warn); }
.badge.destructive { color: var(--bad); border-color: var(--bad); }
.badge.locked { color: var(--muted); background: var(--line-soft); }
.mark { font-size: 11px; font-weight: 600; color: var(--accent); }
.tag { font-size: 11px; color: var(--muted); border: 1px dashed var(--line); border-radius: 8px; padding: 0 6px; }
.override { color: var(--accent); }
.footer { margin: 6px 0 0; }
.banner { background: var(--line-soft); border-radius: 6px; padding: 4px 8px; }
.fold { border: 0; background: transparent; width: 18px; }
.sr { position: absolute; left: -9999px; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); } .small { font-size: 12px; } .ok { color: var(--ok); } .warn { color: var(--warn); } .error { color: var(--bad); }
.allow { color: var(--ok); } .ask { color: var(--warn); } .deny { color: var(--bad); }
.mini { border: 0; background: transparent; color: var(--accent); padding: 0 4px; }
.check { display: flex; gap: 4px; align-items: center; }
h4 { margin: 8px 0 4px; font-size: 13px; }
.other { list-style: none; margin: 0; padding: 0; }
.other li { display: flex; gap: 8px; align-items: center; }
</style>
