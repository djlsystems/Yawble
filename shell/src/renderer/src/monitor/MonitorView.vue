<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';
import type { AgentCard, YawbleApi, Overview, WorkflowSummary } from '../../../shared/api';
import { resolveApi } from '../api';
import MemberAvatar from '../faces/MemberAvatar.vue';
import { MemberStateLabel, memberStateOf, type MemberState } from '../faces/faces';
import { memberKey, MonitorModel, spaced, teamKey, type ActivityRow, type FeedRow, type TeamSnapshot } from './model';
import { MonitorScene, Palette, type SceneHover } from './scene';
import { readStep, StepLook, type StepKind, type StepRow } from './steps';
import Timeline from './Timeline.vue';
import LoadingNote from '../components/LoadingNote.vue';
import { heavyLine } from '../heavyLease';
import {
  DefaultSpeed, DefaultWindowMinutes, MaxWindowMinutes, mergeByTime, ReplayClock, ReplayStates, Speeds, timeOf, trimByTime, Windows,
} from './timeline';
import { entryTime, workflowLabel, workflowLook, WorkflowLooks, WorkflowsShown, type WorkflowIcon } from './workflows';

/**
 * THE MONITOR: every team and its members live on one dark wall (scene.ts), the counts across the top, the state
 * legend along the bottom, the timeline under the wall, and the activity on the right, newest first, with each
 * member's face. Snapshots (the teams and their member cards) are polled; the bus feed (monitor.messages) brings what
 * happens between them, and the step feed (monitor.steps) what each member does inside its runs: the icons rising
 * off it, the files it changes beside it, the line under its name.
 *
 * The timeline holds the last MaxWindowMinutes of both feeds. Clicking it replays from that moment: a model of its
 * own (the live one keeps listening underneath) plays the messages and steps from there, the members' rings show
 * the states the replay works out (ReplayStates), and when it catches up with now, or the person presses Live, the
 * wall is live again.
 *
 * Each team's newest workflows (bus.workflows) sit in one panel hung off its hub, live only: they are fetched when the bus brings that
 * team something and otherwise every WorkflowPollMs, and a replay shows none (it would be faking their history).
 * Clicking one opens the team at its Workflows dialog.
 */
const props = defineProps<{ api?: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{ openTeam: [teamId: number, panel?: 'workflows']; openWorkflow: [teamId: number, workflow: number] }>();
const api = resolveApi(props.api);

const canvas = ref<HTMLCanvasElement | null>(null);
const live = new MonitorModel();
/** The model the wall draws: the live one, or a replay's. */
let model = live;
let scene: MonitorScene | null = null;
const teams = shallowRef<TeamSnapshot[]>([]);
const activity = shallowRef<ActivityRow[]>([]);
// The activity pane folds to a thin rail to give the wall the room; how the person left it is kept.
// The legend along the bottom of the wall: collapsed by default (a "Legend" toggle), the choice remembered.
const legendOpen = ref((() => { try { return localStorage.getItem('yawble.monitor.legend') === 'open'; } catch { return false; } })());
function toggleLegend() {
  legendOpen.value = !legendOpen.value;
  try { localStorage.setItem('yawble.monitor.legend', legendOpen.value ? 'open' : 'collapsed'); } catch { /* not kept */ }
}
const activityOpen = ref((() => { try { return localStorage.getItem('yawble.monitor.activity') !== 'collapsed'; } catch { return true; } })());
function toggleActivity(): void {
  activityOpen.value = !activityOpen.value;
  try { localStorage.setItem('yawble.monitor.activity', activityOpen.value ? 'open' : 'collapsed'); } catch { /* not kept */ }
}
const counts = ref({ running: 0, waiting: 0, needYou: 0, members: 0, perMinute: 0 });
/** The heavy-work lease line under the counts (null: hidden, the lease is free and nobody waits). */
const heavy = ref<string | null>(null);
const hover = ref<SceneHover | null>(null);
const picked = ref<string | null>(null);
const clock = ref('');
const error = ref<string | null>(null);
const loaded = ref(false);
let after = 0;
let stepAfter = 0;
let stepsOff = false;
let timers: ReturnType<typeof setInterval>[] = [];
let busy = false;
let raf = 0;

// The timeline: what it holds, how far back it shows, and the replay.
const rows = shallowRef<FeedRow[]>([]);
const steps = shallowRef<StepRow[]>([]);
const WindowKey = 'yawble.monitor.windowMinutes';
const windowMinutes = ref(readWindow());
const now = ref(Date.now());
const mode = ref<'live' | 'replay'>('live');
const speed = ref<number>(DefaultSpeed);
const playhead = ref<number | null>(null);
let replay: ReplayClock | null = null;
let replayStates: ReplayStates | null = null;
/** A step read live counts as happening now when it is this recent (an older one only joins the timeline). */
const FreshStepMs = 20_000;

/** A team's workflows are fetched again after this long even when the bus brings it nothing. */
const WorkflowPollMs = 5000;
const workflowsAt = new Map<number, number>();
/** The workflows the wall shows, per team, for the list read aloud (and the tests): the canvas draws the same. */
const workflowRows = shallowRef<{ teamId: number; team: string; list: WorkflowSummary[] }[]>([]);
/** The legend's mark for each workflow state, as SVG paths on a 16-unit box. */
const WorkflowIconPath: Record<WorkflowIcon, string> = {
  check: 'M4.5 8.5l2.5 2.5 4.5-5', play: 'M6.5 5l4.5 3-4.5 3z', pause: 'M6.5 5v6M9.5 5v6', cross: 'M5.5 5.5l5 5M10.5 5.5l-5 5',
};

const states: MemberState[] = ['running', 'waiting', 'decision', 'blocked', 'failed', 'idle'];
const stateColor = (s: MemberState) => `color-mix(in srgb, var(--state-${s}) 60%, white)`;
const legendSteps: StepKind[] = ['read', 'edit', 'create', 'run', 'search', 'web', 'bus'];

function readWindow(): number {
  try {
    const saved = Number(localStorage.getItem(WindowKey));
    return Windows.some((w) => w.minutes === saved) ? saved : DefaultWindowMinutes;
  } catch {
    return DefaultWindowMinutes;
  }
}
function setWindow(minutes: number) {
  windowMinutes.value = minutes;
  try { localStorage.setItem(WindowKey, String(minutes)); } catch { /* remembered while the page is open */ }
}

const pickedTeam = computed(() => {
  if (!picked.value) return null;
  for (const t of teams.value) {
    if (picked.value === `t:${t.name.toLowerCase()}`) return { team: t, member: null };
    const m = t.members.find((x) => memberKey(t.name, x.name) === picked.value);
    if (m) return { team: t, member: m };
  }
  return null;
});
const shown = computed(() => {
  const p = pickedTeam.value;
  if (!p) return activity.value;
  return activity.value.filter((r) => r.team.toLowerCase() === p.team.name.toLowerCase() && (!p.member || r.member?.toLowerCase() === p.member.name.toLowerCase()));
});
const windowFrom = computed(() => now.value - windowMinutes.value * 60_000);
const replayClock = computed(() => (playhead.value === null ? '' : new Date(playhead.value).toLocaleTimeString()));

async function snapshot() {
  const overview = await api.call<Overview>('app.overview');
  heavy.value = heavyLine(overview.heavy);
  const next: TeamSnapshot[] = await Promise.all(overview.teams.map(async (t) => {
    const cards = await api.call<AgentCard[]>('teams.agents', { teamId: t.id }).catch(() => [] as AgentCard[]);
    return {
      id: t.id, name: t.name,
      members: cards.map((c) => ({
        id: c.member.id, name: c.member.name, role: c.member.role, cli: c.member.cli, state: memberStateOf(c),
        queued: c.queuedInstructions, statusLine: c.statusLine,
      })),
    };
  }));
  live.reconcile(next, performance.now());
  teams.value = next;
  scene?.setTeams(next);
}

/**
 * The workflows of each team the bus brought something (`news`, team names in lower case), or not fetched for
 * WorkflowPollMs, or all at the start. A team whose call fails keeps what it showed.
 */
async function workflowFeed(first: boolean, news: Set<string>): Promise<void> {
  const wall = Date.now();
  const due = teams.value.filter((t) => first || news.has(t.name.toLowerCase()) || wall - (workflowsAt.get(t.id) ?? -Infinity) >= WorkflowPollMs);
  await Promise.all(due.map(async (t) => {
    try {
      const list = await api.call<WorkflowSummary[]>('bus.workflows', { teamId: t.id, limit: WorkflowsShown });
      workflowsAt.set(t.id, Date.now());
      live.setWorkflows(t.name, Array.isArray(list) ? list : []);
    } catch { workflowsAt.set(t.id, Date.now()); }
  }));
  workflowRows.value = teams.value
    .map((t) => ({ teamId: t.id, team: t.name, list: live.workflows.get(teamKey(t.name)) ?? [] }))
    .filter((r) => r.list.length);
}

/** The panel's header opens the team's workflows; a row opens that workflow (the Backlog item it came from, if any). */
function openWorkflow(teamId: number, workflow?: number) {
  if (workflow !== undefined) emit('openWorkflow', teamId, workflow);
  else emit('openTeam', teamId, 'workflows');
}

/** The bus feed: new rows animate live (not those read at the start), and all join the timeline. */
async function feed(first: boolean): Promise<FeedRow[]> {
  const page = await api.call<{ end: number; messages: FeedRow[] }>('monitor.messages', { after, limit: first ? 500 : 200 });
  after = Math.max(after, page.end);
  if (!first) for (const row of page.messages) live.apply(row, performance.now(), mode.value === 'live');
  if (page.messages.length) rows.value = mergeByTime([...rows.value], page.messages);
  return page.messages;
}

/**
 * The step feed: what the core has read from the runs' transcripts since the last call, every page of it at the
 * start (a day of runs takes a few). A fresh step animates live; any step joins the timeline. A core without the
 * feed (older) leaves the Monitor without steps and nothing else.
 */
async function stepFeed(first: boolean): Promise<StepRow[]> {
  if (stepsOff) return [];
  const got: StepRow[] = [];
  try {
    for (let page = 0; page < (first ? 12 : 3); page++) {
      const res = await api.call<{ end: number; pending: boolean; steps: unknown[] }>('monitor.steps', { after: stepAfter, sinceMinutes: MaxWindowMinutes, limit: 3000 });
      for (const raw of res.steps ?? []) { const s = readStep(raw); if (s) got.push(s); }
      stepAfter = Math.max(stepAfter, res.end ?? 0);
      if (!res.pending) break;
    }
  } catch {
    stepsOff = true;
    return got;
  }
  got.sort((a, b) => timeOf(a) - timeOf(b));
  if (!first) {
    const wall = Date.now();
    for (const s of got) live.applyStep(s, performance.now(), mode.value === 'live' && wall - timeOf(s) < FreshStepMs);
  }
  if (got.length) steps.value = mergeByTime([...steps.value], got);
  return got;
}

/** Applies rows and steps to a model in the order they happened, without playing them (the start, a replay's past). */
function catchUp(m: MonitorModel, rs: readonly FeedRow[], ss: readonly StepRow[], into?: ReplayStates) {
  let i = 0, j = 0;
  while (i < rs.length || j < ss.length) {
    // (The index moves outside the optional call: `into?.row(rs[i++])` would skip the ++ without `into`.)
    if (j >= ss.length || (i < rs.length && timeOf(rs[i]!) <= timeOf(ss[j]!))) {
      const r = rs[i++]!;
      m.apply(r, 0, false);
      into?.row(r);
    } else {
      const s = ss[j++]!;
      m.applyStep(s, 0, false);
      into?.step(s);
    }
  }
}

async function tick(first = false) {
  if (busy) return;
  busy = true;
  try {
    await snapshot();
    const fr = await feed(first);
    const fs = await stepFeed(first);
    await workflowFeed(first, new Set(fr.map((r) => r.team.toLowerCase())));
    if (first) catchUp(live, [...fr].sort((a, b) => timeOf(a) - timeOf(b)), fs);
    const from = Date.now() - MaxWindowMinutes * 60_000;
    rows.value = trimByTime(rows.value, from, 4000);
    steps.value = trimByTime(steps.value, from, 30_000);
    replay?.extend(rows.value, steps.value);
    if (mode.value === 'live' && scene) scene.effects.push(...live.effects.splice(0));
    else live.effects.length = 0;
    if (model === live) activity.value = [...live.activity];
    error.value = null;
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy = false;
    loaded.value = true;
    counts.value = { ...live.counts(), perMinute: live.perMinute(performance.now()) };
  }
}

// ---------- the replay ----------

/** Replays from `at` (ms): what happened before it is applied quietly, then the wall plays on from there. */
function startReplay(at: number) {
  const wall = Date.now();
  if (wall - at < 3000) { goLive(); return; }
  const from = Math.max(at, wall - MaxWindowMinutes * 60_000);
  const m = new MonitorModel();
  m.reconcile(teams.value, performance.now());
  const st = new ReplayStates();
  // The half hour before the moment sets the states and the activity list; older rows would only fill the list.
  const lead = from - 30 * 60_000;
  catchUp(m, rows.value.filter((r) => timeOf(r) >= lead && timeOf(r) < from), steps.value.filter((s) => timeOf(s) >= lead && timeOf(s) < from), st);
  m.effects.length = 0;
  replay = new ReplayClock(rows.value, steps.value, from, speed.value);
  replayStates = st;
  model = m;
  mode.value = 'replay';
  playhead.value = from;
  activity.value = [...m.activity];
  if (scene) {
    scene.effects = [];
    scene.clearFiles();
    scene.model = m;
    scene.stateOf = (key) => st.of(key, replay?.at ?? Date.now());
  }
}

function goLive() {
  replay = null;
  replayStates = null;
  model = live;
  mode.value = 'live';
  playhead.value = null;
  activity.value = [...live.activity];
  if (scene) {
    scene.effects = [];
    scene.clearFiles();
    scene.model = live;
    scene.stateOf = null;
  }
}

function setSpeed(value: number) {
  speed.value = value;
  if (replay) replay.speed = value;
}

let lastFrame = 0;
let lastPublish = 0;
/** Each frame of a replay: the rows and steps now due, played in order; live again once it reaches now. */
function frame(t: number) {
  raf = requestAnimationFrame(frame);
  const dt = lastFrame ? Math.min(250, t - lastFrame) : 16;
  lastFrame = t;
  if (!replay || !replayStates) return;
  const wall = Date.now();
  const due = replay.step(dt, wall);
  let i = 0, j = 0;
  while (i < due.rows.length || j < due.steps.length) {
    if (j >= due.steps.length || (i < due.rows.length && timeOf(due.rows[i]!) <= timeOf(due.steps[j]!))) {
      model.apply(due.rows[i]!, t, true);
      replayStates.row(due.rows[i++]!);
    } else {
      model.applyStep(due.steps[j]!, t, true);
      replayStates.step(due.steps[j++]!);
    }
  }
  if (scene) scene.effects.push(...model.effects.splice(0));
  else model.effects.length = 0;
  if (t - lastPublish > 200) {
    lastPublish = t;
    playhead.value = replay.at;
    activity.value = [...model.activity];
  }
  if (replay.at >= wall - 1000) goLive();
}

function pick(key: string | null) {
  picked.value = key;
  scene?.setPicked(key);
}

function openPicked() {
  const p = pickedTeam.value;
  if (p) emit('openTeam', p.team.id);
}

function ago(at: string): string {
  const s = Math.max(0, ((playhead.value ?? Date.now()) - new Date(at).getTime()) / 1000);
  if (s < 60) return 'now';
  if (s < 3600) return `${Math.floor(s / 60)}m`;
  if (s < 86400) return `${Math.floor(s / 3600)}h`;
  return new Date(at).toLocaleDateString();
}

onMounted(async () => {
  if (canvas.value?.getContext('2d')) {
    scene = new MonitorScene(canvas.value);
    scene.model = live;
    scene.onHover = (h) => { hover.value = h; };
    scene.onPick = (k) => { picked.value = k; };
    scene.onOpenWorkflow = (team, id) => {
      const t = teams.value.find((x) => x.name.toLowerCase() === team.toLowerCase());
      if (t) openWorkflow(t.id, id);
    };
  }
  await tick(true);
  timers = [
    setInterval(() => void tick(), props.pollMs ?? 1200),
    setInterval(() => { clock.value = new Date().toLocaleTimeString(); now.value = Date.now(); }, 1000),
  ];
  clock.value = new Date().toLocaleTimeString();
  if (typeof requestAnimationFrame === 'function') raf = requestAnimationFrame(frame);
});
onBeforeUnmount(() => { for (const t of timers) clearInterval(t); cancelAnimationFrame(raf); scene?.destroy(); });
</script>

<template>
  <div class="monitor" data-test="monitor">
    <div class="main">
      <div class="stage">
        <canvas ref="canvas" class="wall" data-test="monitor-canvas"></canvas>
        <div class="hud">
          <div v-if="mode === 'live'" class="title"><span class="live" />LIVE <span class="clock">{{ clock }}</span></div>
          <div v-else class="title replay" data-test="monitor-replaying"><span class="rec" />REPLAY {{ speed }}× <span class="clock">{{ replayClock }}</span></div>
          <div class="figures" data-test="monitor-counts">
            <span><b :style="{ color: stateColor('running') }">{{ counts.running }}</b> running</span>
            <span><b :style="{ color: stateColor('waiting') }">{{ counts.waiting }}</b> waiting</span>
            <span><b :style="{ color: stateColor('decision') }">{{ counts.needYou }}</b> need you</span>
            <span><b>{{ counts.perMinute }}</b> events/min</span>
          </div>
          <div class="sub">{{ teams.length }} team{{ teams.length === 1 ? '' : 's' }} · {{ counts.members }} member{{ counts.members === 1 ? '' : 's' }}<template v-if="mode === 'replay'"> · the counts are now; the wall is the replay</template></div>
          <div v-if="heavy" class="heavy" data-test="monitor-heavy" title="The heavy-work lease: a full test suite, build or image build runs a few at a time">{{ heavy }}</div>
        </div>
        <div class="controls">
          <button class="ctl" title="Fit everything (double-click the wall)" data-test="monitor-fit" @click="scene?.fit()">Fit</button>
        </div>
        <button class="ctl legend-toggle" :class="{ open: legendOpen }" type="button" data-test="monitor-legend-toggle"
          :aria-expanded="legendOpen" :title="legendOpen ? 'Hide the legend' : 'Show the legend'" @click="toggleLegend">{{ legendOpen ? 'Legend ▾' : 'Legend ▸' }}</button>
        <div v-if="legendOpen" class="legend" data-test="monitor-legend">
          <span v-for="s in states" :key="s"><i class="ring" :style="{ borderColor: stateColor(s) }" />{{ MemberStateLabel[s] }}</span>
          <span class="sep" />
          <span><i class="tone" :style="{ background: Palette.tell }" />told</span>
          <span><i class="tone" :style="{ background: Palette.done }" />finished</span>
          <span><i class="tone" :style="{ background: Palette.handback }" />handed back</span>
          <span><i class="tone" :style="{ background: Palette.fail }" />failed</span>
          <span class="sep" />
          <span v-for="k in legendSteps" :key="k"><i class="dot" :style="{ background: StepLook[k].color }" />{{ StepLook[k].done }}</span>
          <template v-if="mode === 'live'">
            <span class="sep" />
            <span class="wfl" data-test="monitor-legend-workflows">
              <span>workflows:</span>
              <span v-for="l in WorkflowLooks" :key="l.status">
                <svg class="wfi" :class="l.status" viewBox="0 0 16 16" aria-hidden="true" :style="{ color: Palette[l.tone] }">
                  <circle cx="8" cy="8" r="6.6" fill="none" stroke="currentColor" stroke-width="1.4" :stroke-dasharray="l.status === 'working' ? '10 4.5' : undefined" />
                  <path :d="WorkflowIconPath[l.icon]" :fill="l.icon === 'play' ? 'currentColor' : 'none'" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
                </svg>{{ l.label.toLowerCase() }}
              </span>
            </span>
          </template>
        </div>
        <ul v-if="mode === 'live' && workflowRows.length" class="sr-only" aria-label="Workflows" data-test="monitor-workflows">
          <template v-for="r in workflowRows" :key="r.teamId">
            <li>
              <button type="button" data-test="monitor-workflows-team" @click="openWorkflow(r.teamId)">{{ r.team }}: workflows, last updated first</button>
            </li>
            <li v-for="w in r.list" :key="w.id">
              <button type="button" data-test="monitor-workflow" :data-id="w.id" @click="openWorkflow(r.teamId, w.id)">
                {{ r.team }}: {{ workflowLabel(w) }} · {{ workflowLook(w).label }} · last entry {{ entryTime(w.lastAt) }}
              </button>
            </li>
          </template>
        </ul>
        <div v-if="hover" class="tip" :style="{ left: `${hover.x}px`, top: `${hover.y}px` }" data-test="monitor-tip">
          <template v-if="hover.kind === 'file'">
            <template v-if="hover.path">
              <strong class="path">{{ hover.path }}</strong>
              <span :style="{ color: StepLook[hover.fileKind ?? 'edit'].color }">{{ hover.fileKind === 'create' ? 'written' : 'edited' }}{{ (hover.touches ?? 1) > 1 ? ` ${hover.touches}×` : '' }} by {{ spaced(hover.name) }}</span>
            </template>
            <template v-else>
              <strong>{{ hover.more?.length }} more files by {{ spaced(hover.name) }}</strong>
              <span v-for="p in (hover.more ?? []).slice(0, 8)" :key="p" class="dim path">{{ p }}</span>
            </template>
          </template>
          <template v-else-if="hover.kind === 'workflow' && hover.workflow">
            <strong>#{{ hover.workflow.id }} {{ hover.workflow.title }}</strong>
            <span :style="{ color: Palette[workflowLook(hover.workflow).tone] }">{{ workflowLook(hover.workflow).label }}</span>
            <span v-if="hover.workflow.busy.length" class="dim">{{ hover.workflow.busy.join(' · ') }}</span>
            <span v-else-if="hover.workflow.ending" class="dim">{{ hover.workflow.state === 'closed' ? 'Closed' : 'Delivered' }}: {{ hover.workflow.ending }}</span>
            <span class="dim">started {{ entryTime(hover.workflow.startedAt) }} · last entry {{ entryTime(hover.workflow.lastAt) }}</span>
            <span class="dim">{{ hover.workflow.messages }} message{{ hover.workflow.messages === 1 ? '' : 's' }} · {{ hover.team }} · click to open</span>
          </template>
          <template v-else-if="hover.kind === 'workflow'">
            <strong>{{ hover.team }} · workflows</strong>
            <span class="dim">the last {{ WorkflowsShown }}, last updated first · click to open them all</span>
          </template>
          <template v-else>
            <strong>{{ hover.kind === 'team' ? hover.name : spaced(hover.name) }}</strong>
            <span v-if="hover.kind === 'member' && hover.state" :style="{ color: stateColor(hover.state) }">{{ MemberStateLabel[hover.state] }}</span>
            <span v-if="hover.kind === 'member'" class="dim">{{ hover.team }}<template v-if="hover.queued"> · {{ hover.queued }} queued</template></span>
            <span v-if="hover.statusLine" class="dim">"{{ hover.statusLine }}"</span>
          </template>
        </div>
        <div v-if="!loaded" class="empty" data-test="monitor-loading"><LoadingNote text="Loading the Monitor (teams, workflows and the last hour)…" /></div>
        <div v-if="loaded && !error && !teams.length" class="empty">No teams yet. Create one from Teams, and its members appear here as they work.</div>
        <div v-if="error" class="empty err">{{ error }}</div>
      </div>

      <div class="timebar" data-test="monitor-timebar">
        <div class="bar-controls">
          <span class="group" role="group" aria-label="How far back">
            <button v-for="w in Windows" :key="w.minutes" class="seg" :class="{ on: windowMinutes === w.minutes }" :data-test="`monitor-window-${w.label}`" @click="setWindow(w.minutes)">{{ w.label }}</button>
          </span>
          <template v-if="mode === 'replay'">
            <span class="group" role="group" aria-label="Replay speed">
              <button v-for="s in Speeds" :key="s" class="seg" :class="{ on: speed === s }" @click="setSpeed(s)">{{ s }}×</button>
            </span>
            <button class="ctl live-btn" data-test="monitor-live" @click="goLive">● Back to live</button>
          </template>
          <span v-else class="hint">click or drag the timeline to replay from a moment</span>
        </div>
        <Timeline :from="windowFrom" :to="now" :rows="rows" :steps="steps" :playhead="playhead" :live="mode === 'live'" @seek="startReplay" />
      </div>
    </div>

    <aside class="activity" :class="{ collapsed: !activityOpen }" data-test="monitor-activity">
      <button v-if="!activityOpen" class="rail" title="Show the activity" aria-label="Show the activity" aria-expanded="false"
        data-test="monitor-activity-toggle" @click="toggleActivity">
        <span class="chev">‹</span><span class="rail-label">Activity</span>
      </button>
      <header v-if="activityOpen">
        <template v-if="pickedTeam">
          <span class="pick">{{ pickedTeam.member ? spaced(pickedTeam.member.name) : pickedTeam.team.name }}</span>
          <button class="ctl" data-test="monitor-open-team" @click="openPicked">Open team</button>
          <button class="ctl" title="Show everything" data-test="monitor-unpick" @click="pick(null)">×</button>
        </template>
        <span v-else class="pick">Activity<template v-if="mode === 'replay'"> · replay</template></span>
        <button class="ctl" title="Hide the activity" aria-label="Hide the activity" aria-expanded="true" data-test="monitor-activity-toggle" @click="toggleActivity">›</button>
      </header>
      <ol v-if="activityOpen">
        <li v-for="r in shown" :key="r.key" :style="{ '--tone': Palette[r.tone] }" data-test="monitor-row">
          <MemberAvatar v-if="r.member" :team="r.team" :member="r.member" :size="22" wall />
          <span v-else class="noface" />
          <div class="line">
            <div><b>{{ r.verb }}</b> <template v-if="r.verb.endsWith('told')">{{ r.who }}</template><template v-else>· {{ r.who }}</template> <span class="dim">· {{ r.team }} · {{ ago(r.at) }}</span></div>
            <div v-if="r.text" class="text">{{ r.text }}</div>
          </div>
        </li>
        <li v-if="!loaded" class="none"><LoadingNote text="Loading activity…" /></li>
        <li v-else-if="!shown.length" class="dim none">Nothing yet.</li>
      </ol>
    </aside>
  </div>
</template>

<style scoped>
.monitor { display: flex; height: 100%; min-height: 0; background: #070b14; color: #e2e8f0; }
.main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.stage { position: relative; flex: 1; min-height: 0; }
.wall { position: absolute; inset: 0; width: 100%; height: 100%; display: block; touch-action: none; }
.hud { position: absolute; left: 0; right: 0; top: 0; padding: 12px 16px 26px; pointer-events: none;
  background: linear-gradient(rgba(7, 11, 20, .92), rgba(7, 11, 20, 0)); }
.title { font-size: 11px; letter-spacing: .14em; font-weight: 700; display: flex; align-items: center; gap: 8px; }
.title.replay { color: #fcd34d; }
.live { width: 9px; height: 9px; border-radius: 50%; background: #f43f5e; box-shadow: 0 0 8px #f43f5e; animation: blink 1.4s ease-in-out infinite; }
.rec { width: 0; height: 0; border-left: 9px solid #fcd34d; border-top: 5px solid transparent; border-bottom: 5px solid transparent; }
.clock { font-family: ui-monospace, Consolas, monospace; letter-spacing: 0; color: #94a3b8; font-weight: 500; }
.figures { display: flex; gap: 18px; margin-top: 6px; font-size: 12px; color: #94a3b8; }
.figures b { font-size: 20px; color: #e2e8f0; margin-right: 4px; font-variant-numeric: tabular-nums; }
.sub { font-size: 11px; color: #64748b; margin-top: 2px; }
.heavy { font-size: 11px; color: #cbd5e1; margin-top: 2px; max-width: 520px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.controls { position: absolute; top: 12px; right: 12px; display: flex; gap: 6px; }
.ctl { background: rgba(12, 19, 34, .85); color: #e2e8f0; border: 1px solid #1e293b; border-radius: 6px; padding: 3px 10px; font-size: 12px; }
.ctl:hover:not(:disabled) { border-color: #38bdf8; }
.legend { position: absolute; left: 0; right: 0; bottom: 0; padding: 8px 16px 8px 92px; display: flex; flex-wrap: wrap; gap: 12px; font-size: 11px; color: #94a3b8;
  background: linear-gradient(rgba(7, 11, 20, 0), rgba(7, 11, 20, .9)); pointer-events: none; }
.legend-toggle { position: absolute; left: 12px; bottom: 8px; z-index: 2; font-size: 11px; padding: 2px 8px; }
.legend span { display: inline-flex; align-items: center; gap: 5px; }
.ring { width: 10px; height: 10px; border-radius: 50%; border: 2px solid; display: inline-block; }
.tone { width: 8px; height: 8px; border-radius: 2px; display: inline-block; }
.dot { width: 7px; height: 7px; border-radius: 50%; display: inline-block; }
.sep { width: 1px; background: #1e293b; }
.legend .wfl { gap: 12px; }
.wfi { width: 12px; height: 12px; flex: none; }
.wfi.working circle { transform-origin: 8px 8px; animation: wf-turn 3s linear infinite; }
.sr-only { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0; }
.tip { position: absolute; transform: translate(-50%, calc(-100% - 12px)); max-width: 22rem; pointer-events: none; display: flex; flex-direction: column; gap: 2px;
  background: rgba(12, 19, 34, .95); border: 1px solid #334155; border-radius: 8px; padding: 6px 10px; font-size: 12px; }
.path { font-family: ui-monospace, Consolas, monospace; font-size: 11px; word-break: break-all; }
.dim { color: #94a3b8; }
.empty { position: absolute; inset: 0; display: grid; place-items: center; color: #94a3b8; padding: 24px; text-align: center; pointer-events: none; }
.err { color: #fb7185; }
.timebar { flex: none; border-top: 1px solid #1e293b; background: #0a1020; padding: 6px 14px 4px; }
.bar-controls { display: flex; align-items: center; gap: 10px; margin-bottom: 4px; font-size: 11px; color: #64748b; }
.group { display: inline-flex; border: 1px solid #1e293b; border-radius: 6px; overflow: hidden; }
.seg { background: transparent; color: #94a3b8; border: 0; padding: 2px 8px; font-size: 11px; }
.seg + .seg { border-left: 1px solid #1e293b; }
.seg.on { background: #1e293b; color: #e2e8f0; }
.seg:hover:not(.on) { color: #e2e8f0; }
.live-btn { color: #fda4af; padding: 2px 10px; font-size: 11px; }
.hint { opacity: .8; }
.activity { width: 22rem; flex: none; display: flex; flex-direction: column; border-left: 1px solid #1e293b; background: #0c1322; min-height: 0; }
.activity.collapsed { width: 2.25rem; }
.rail { flex: 1; display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 10px 0; border: 0; background: transparent; color: #94a3b8; cursor: pointer; }
.rail:hover { color: #e2e8f0; background: #111a2e; }
.chev { font-size: 16px; line-height: 1; }
.rail-label { writing-mode: vertical-rl; transform: rotate(180deg); font-size: 12px; font-weight: 600; letter-spacing: .04em; }
.activity header { display: flex; align-items: center; gap: 6px; padding: 10px 12px; border-bottom: 1px solid #1e293b; }
.pick { flex: 1; font-weight: 600; font-size: 13px; }
.activity ol { list-style: none; margin: 0; padding: 4px 0; overflow-y: auto; overflow-x: hidden; flex: 1; }
.activity li { display: flex; gap: 6px; padding: 6px 12px 6px 9px; border-left: 3px solid var(--tone); font-size: 12px; animation: row-in .5s ease-out; }
.activity li + li { border-top: 1px solid rgba(30, 41, 59, .6); }
.line { min-width: 0; flex: 1; }
.text { color: #cbd5e1; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.noface { width: 30px; flex: none; }
.none { border-left-color: transparent !important; }
@keyframes blink { 50% { opacity: .25; } }
@keyframes wf-turn { to { transform: rotate(360deg); } }
@keyframes row-in { from { opacity: 0; transform: translateX(12px); } }
@media (prefers-reduced-motion: reduce) { .live, .activity li, .wfi.working circle { animation: none; } }
@media (max-width: 900px) { .monitor { flex-direction: column; } .activity { width: auto; height: 40%; border-left: 0; border-top: 1px solid #1e293b; } .activity.collapsed { width: auto; height: 2.25rem; } .rail { flex-direction: row; justify-content: center; } .rail-label { writing-mode: horizontal-tb; transform: none; } }
</style>
