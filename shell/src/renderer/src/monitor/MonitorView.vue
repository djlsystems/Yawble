<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { workflowTime, type AgentCard, type NetworkAsk, type ToolAsk, type YawbleApi, type Overview, type WorkflowItem, type WorkflowSummary } from '../../../shared/api';
import { workflowItems } from '../workflows/page';
import { groupAsks, hostsLabel } from '../networkAskGroups';
import { FOLDER_ASKS_CHANGED, folderAskMember, pendingFolderAsks, type FolderAsk } from '../folderAsks';
import { resolveApi } from '../api';
import MemberAvatar from '../faces/MemberAvatar.vue';
import { MemberStateLabel, memberStateOf, type MemberState } from '../faces/faces';
import {
  memberKey, MonitorModel, needYouSummary, needYouWord, spaced, teamKey, type ActivityRow, type FeedRow, type AskKind, type NeedYouItem, type NeedYouKind, type TeamSnapshot,
} from './model';
import { youNodeAt, type KeepOut } from './rhythm';
import { openCommit } from '../commitView';
import { MonitorScene, Palette, type SceneHover } from './scene';
import {
  clampLayout, Densities, DefaultDensity, isDefaultLayout, LayoutDefaults, LayoutFields, LayoutMax, LayoutMin, LayoutStep, readDensity, readLayout,
  writeDensity, writeLayout, type Density, type LayoutKey, type LayoutSettings,
} from './layout';
import { MonitorSound, readSound, writeSound } from './sound';
import { readStep, StepLook, type StepKind, type StepRow } from './steps';
import Timeline from './Timeline.vue';
import LoadingNote from '../components/LoadingNote.vue';
import CommitText from '../components/CommitText.vue';
import { heavyLine } from '../heavyLease';
import { useNarrow } from '../narrowScreen';
import {
  DefaultSpeed, DefaultWindowMinutes, inTimeOrder, MaxWindowMinutes, mergeByTime, ReplayClock, ReplayStates, Speeds, timeOf, trimByTime, Windows,
} from './timeline';
import { codeEventKey, readCodeEvents, type CodeEvent } from './codemap';
import { entryTime, workflowLabel, workflowLook, WorkflowIconPath, WorkflowLooks, WorkflowsShown } from './workflows';

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
 * The code events (monitor.codeEvents: commits, merges into the team branch, landings on main, pushes) are polled
 * the same way and build each team's code map with the steps (codemap.ts); a core without the read leaves the map to
 * the steps alone. The replay plays them at their times with the rest.
 *
 * Each team's newest workflows (bus.workflows) sit in one panel hung off its hub, live only: they are fetched when the bus brings that
 * team something and otherwise every WorkflowPollMs, and a replay shows none (it would be faking their history).
 * Clicking one opens the team at its Workflows dialog.
 *
 * A click on a team's hub focuses it (the scene zooms to it and dims the rest); Esc, a click on the empty wall or the
 * chip's × shows every team again. A click on the You node opens Needs you (emit openNeedsYou). Beside the camera:
 * the sound (off unless turned on, kept; sound.ts). The cog opens the Monitor's settings: the density (calm / normal /
 * lively, lively by default, kept) at the top, then the network forces; on a phone the sound sits there too.
 */
const props = defineProps<{ api?: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{
  openTeam: [teamId: number, panel?: 'workflows']; openWorkflow: [teamId: number, workflow: number]; openTerminal: [teamId: number, memberId: number];
  /** The You node was clicked: the app opens what needs the person (the Board's Needs You). */
  openNeedsYou: [];
}>();
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
// The camera: Auto fit (fits everything, except for 12 s after the person moves the wall) or Pinned (the zoom and
// position stay where the person left them; Fit fits once and stays pinned). The choice is remembered.
const pinned = ref((() => { try { return localStorage.getItem('yawble.monitor.camera') === 'pinned'; } catch { return false; } })());
function setPinned(on: boolean): void {
  pinned.value = on;
  scene?.setPinned(on);
  try { localStorage.setItem('yawble.monitor.camera', on ? 'pinned' : 'auto'); } catch { /* not kept */ }
}
// The layout forces (the cog beside Pinned): multipliers on the wall's link distance, link strength, repulsion,
// gravity and friction, applied as they change and remembered in this app's storage.
const layout = ref<LayoutSettings>(readLayout());
const layoutOpen = ref(false);
const layoutChanged = computed(() => !isDefaultLayout(layout.value));
function setLayoutValue(key: LayoutKey, value: unknown): void {
  layout.value = { ...layout.value, [key]: clampLayout(value, LayoutDefaults[key]) };
  scene?.setLayout(layout.value);
  writeLayout(layout.value);
}
function resetLayout(): void {
  layout.value = { ...LayoutDefaults };
  scene?.setLayout(layout.value);
  writeLayout(layout.value);
}
function onLayoutKey(e: KeyboardEvent): void {
  if (e.key === 'Escape') { e.stopPropagation(); layoutOpen.value = false; }
}
/** Fit: everything, once; from Auto fit it pins the camera there. */
function fitOnce(): void {
  if (!pinned.value) setPinned(true);
  scene?.fit();
}
/**
 * What the view asks of the scene beyond the camera (DeveloperTomas's scene.ts, card 373): each one optional, so a
 * scene without it leaves the view's control doing nothing on the wall (the view's own state is kept all the same).
 */
interface SceneExtras {
  setFocus?: (teamId: number | null) => void;
  setDensity?: (level: Density) => void;
  hitYouNode?: (x: number, y: number) => boolean;
}
const extras = (): SceneExtras | null => scene as unknown as SceneExtras | null;

// Focus: a click on a team zooms the wall to it and dims the rest; Esc, a click on the empty wall or the chip's ×
// leaves. Not kept: the Monitor opens on everything.
const focus = ref<{ id: number; name: string } | null>(null);
function setFocus(team: { id: number; name: string } | null): void {
  if ((focus.value?.id ?? null) === (team?.id ?? null)) return;
  focus.value = team ? { id: team.id, name: team.name } : null;
  try { extras()?.setFocus?.(team ? team.id : null); } catch { /* the scene could not; the view stays as it was */ }
}
function leaveFocus(): void { setFocus(null); }
/** A pick from the wall: a team's hub focuses that team (a member or a file leaves the focus as it is). */
function onScenePick(key: string | null): void {
  pickSeq++;
  if (key === null || key.startsWith('you')) return;
  picked.value = key;
  if (!key.startsWith('t:')) return;
  const t = teams.value.find((x) => `t:${x.name.toLowerCase()}` === key);
  if (t) setFocus(t);
}
function onKey(e: KeyboardEvent): void {
  if (e.key !== 'Escape' || e.defaultPrevented || !focus.value) return;
  const el = e.target as HTMLElement | null;
  if (el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName))) return;
  leaveFocus();
}

// A plain click on the wall (pressed and let go within ClickSlopPx, one pointer, and the scene took no pick in
// between): on the You node it opens Needs you; anywhere else empty it leaves the focus.
const ClickSlopPx = 5;
let pickSeq = 0;
let press: { x: number; y: number; seq: number; id: number } | null = null;
let pointersDown = 0;
function canvasPoint(e: PointerEvent): [number, number] {
  const b = canvas.value!.getBoundingClientRect();
  return [e.clientX - b.left, e.clientY - b.top];
}
/** Runs in the capture phase, so before the scene's own pointerdown (which may pick). */
function onWallDown(e: PointerEvent): void {
  if (e.target !== canvas.value) return;
  pointersDown++;
  press = pointersDown === 1 ? { x: e.clientX, y: e.clientY, seq: pickSeq, id: e.pointerId } : null;
}
/** Runs after the scene's pointerup. */
function onWallUp(e: PointerEvent): void {
  if (e.target !== canvas.value) return;
  pointersDown = Math.max(0, pointersDown - 1);
  const p = press;
  press = null;
  if (!p || p.id !== e.pointerId || Math.hypot(e.clientX - p.x, e.clientY - p.y) >= ClickSlopPx) return;
  const [x, y] = canvasPoint(e);
  let you = false;
  try { you = extras()?.hitYouNode?.(x, y) === true; } catch { you = false; }
  // On a phone (no hover) a tap shows what is in it first; its button opens Needs you. A desktop click opens it.
  if (you) {
    if (phone.value) { publishCounts(); youOpen.value = !youOpen.value; }
    else emit('openNeedsYou');
    return;
  }
  youOpen.value = false;
  // B072: a click on a commit dot opens its Commit view.
  const h = hover.value;
  if (h?.kind === 'commit' && h.sha && h.team) { openCommit(h.team, h.sha); return; }
  if (p.seq === pickSeq) leaveFocus();
}
function onWallCancel(): void { pointersDown = Math.max(0, pointersDown - 1); press = null; }

// Density: how much of what happens moves on the wall (calm / normal / lively), kept. Reduced motion stands at any.
const density = ref<Density>(readDensity());
function setDensity(level: Density): void {
  density.value = level;
  try { extras()?.setDensity?.(level); } catch { /* the scene could not */ }
  writeDensity(level);
}

// Sound: a quiet tone per kind of event (sound.ts), off unless turned on; kept. Live only, never in a replay.
const sound = new MonitorSound();
const soundOn = ref(readSound());
sound.on = soundOn.value;
function toggleSound(): void {
  soundOn.value = !soundOn.value;
  sound.on = soundOn.value;
  if (soundOn.value) sound.unlock();
  writeSound(soundOn.value);
}

/**
 * A phone (B037): the wall fills the screen, the activity starts collapsed (a bar under the timeline; how it is left
 * there is not kept, so the desktop's choice stands), and the camera and legend controls sit in one compact row.
 */
const phone = useNarrow();
// The workflow nodes' labels go by the window being phone-wide, not by the wall's canvas (card 409).
watch(phone, (on) => scene?.setPhone?.(on));
const activityOpen = ref(phone.value ? false : (() => { try { return localStorage.getItem('yawble.monitor.activity') !== 'collapsed'; } catch { return true; } })());
function toggleActivity(): void {
  activityOpen.value = !activityOpen.value;
  if (phone.value) return;
  try { localStorage.setItem('yawble.monitor.activity', activityOpen.value ? 'open' : 'collapsed'); } catch { /* not kept */ }
}
const counts = ref({ running: 0, waiting: 0, needYou: 0, members: 0, perMinute: 0 });
/**
 * What waits for the person (B088): the live model's need-you list, the same one the You node counts, so the header's
 * "N need you" is always its length. Published with the counts.
 */
const needYou = shallowRef<NeedYouItem[]>([]);
function publishCounts(): void {
  needYou.value = live.needYouItems();
  counts.value = { ...live.counts(), needYou: needYou.value.length, perMinute: live.perMinute(performance.now()) };
}
/** The heavy-work lease line under the counts (null: hidden, the lease is free and nobody waits). */
const heavy = ref<string | null>(null);
const hover = ref<SceneHover | null>(null);
// B098 (B104): a workflow node's panel names the backlog item it came from. Looked up (workflows.list) when a workflow
// is first hovered, once per workflow; an older core without workflows.list names none.
const itemOf = shallowRef(new Map<number, WorkflowItem | null>());
const hoverItem = computed(() => (hover.value?.kind === 'workflow' && hover.value.workflow ? itemOf.value.get(hover.value.workflow.id) ?? null : null));
watch(() => (hover.value?.kind === 'workflow' ? hover.value.workflow?.id ?? null : null), async (id) => {
  if (id == null || itemOf.value.has(id)) return;
  itemOf.value = new Map(itemOf.value).set(id, null);
  const got = await workflowItems(api, { q: `#${id}`, limit: 20 });
  if (got?.items.has(id)) itemOf.value = new Map(itemOf.value).set(id, got.items.get(id)!);
});
// The You node's breakdown: shown while it is hovered (a desktop) or after a tap on it (a phone, until the next tap).
const YouTipLines = 10;
const NeedYouLook: Record<NeedYouKind, 'decision' | 'blocked' | 'fail'> = { question: 'decision', network: 'decision', tool: 'decision', folder: 'decision', blocked: 'blocked', failed: 'fail' };
const youOpen = ref(false);
const youShown = computed(() => youOpen.value || hover.value?.kind === 'you');
/**
 * The wall's size as the panel is placed by (B088): measured each time the panel shows and whenever the canvas
 * resizes (Needs you banners, a shorter window), so the panel follows the You node; a computed over
 * getBoundingClientRect() alone would keep its first answer.
 */
const wallSize = ref({ w: 0, h: 0 });
/**
 * What the You node keeps clear of (card 416), in canvas CSS px: the controls (a phone's row along the bottom holds
 * Legend), the desktop's Legend button and the open legend. Measured with the wall's size and given to the scene, so
 * the node it draws and hits is where the panel is placed.
 */
const youKeepOut = shallowRef<KeepOut[]>([]);
// Most telling first (youNodeAt keeps clear of the first above all): the Legend button (a phone's sits in the
// controls row), the controls, the open legend.
const KeepOutSelectors = [':scope > .legend-toggle, :scope > .controls > .legend-toggle', ':scope > .controls', ':scope > .legend'];
function measureKeepOut(b: DOMRect): KeepOut[] {
  const stage = canvas.value?.parentElement;
  if (!stage) return [];
  const out: KeepOut[] = [];
  for (const sel of KeepOutSelectors) {
    stage.querySelectorAll<HTMLElement>(sel).forEach((el) => {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) out.push({ left: r.left - b.left, top: r.top - b.top, right: r.right - b.left, bottom: r.bottom - b.top });
    });
  }
  return out;
}
function measureWall(): void {
  const b = canvas.value?.getBoundingClientRect();
  const w = b?.width ?? 0, h = b?.height ?? 0;
  if (w !== wallSize.value.w || h !== wallSize.value.h) wallSize.value = { w, h };
  const keep = b ? measureKeepOut(b) : [];
  const same = keep.length === youKeepOut.value.length && keep.every((k, i) => {
    const o = youKeepOut.value[i]!;
    return k.left === o.left && k.top === o.top && k.right === o.right && k.bottom === o.bottom;
  });
  if (!same) {
    youKeepOut.value = keep;
    try { scene?.setYouKeepOut?.(keep); } catch { /* the scene could not; it keeps the node where it was */ }
  }
}
watch(youShown, (shown) => { if (shown) measureWall(); }, { flush: 'sync' });
// The legend opening or closing, and a phone's controls row coming or going, move what the node keeps clear of.
watch([legendOpen, phone], () => { void nextTick(measureWall); });
let wallObserver: ResizeObserver | null = null;
const youTipStyle = computed(() => {
  const { w, h } = wallSize.value;
  const n = youNodeAt(w, h, youKeepOut.value);
  return { right: `${Math.max(4, w - n.x + n.r + 10)}px`, top: `${n.y}px` };
});
function openNeedsYouFromTip(): void {
  youOpen.value = false;
  emit('openNeedsYou');
}
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
const events = shallowRef<CodeEvent[]>([]);
/** The code events already taken (the read is asked again from the newest one's time, so it overlaps). */
const eventKeys = new Set<string>();
let eventsSince = 0;
/** How the code read is asked: for every team at once, per team, or not at all (a core without it). */
let codeMode: 'all' | 'team' | 'off' = 'all';
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

const states: MemberState[] = ['running', 'waiting', 'decision', 'blocked', 'failed', 'stopped', 'idle'];
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
/** Each member's state now, by its scene key, so an activity row's face wears the same ring as the member on the wall. */
const memberStates = computed(() => {
  const states = new Map<string, MemberState>();
  for (const t of teams.value) for (const m of t.members) states.set(memberKey(t.name, m.name), m.state);
  return states;
});
/** A row's member state: the replay's state at the playhead in a replay, else the live one (idle if unknown). */
function rowState(team: string, member: string): MemberState {
  const key = memberKey(team, member);
  if (replayStates && playhead.value !== null) return replayStates.of(key, playhead.value);
  return memberStates.value.get(key) ?? 'idle';
}
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
  // A focused team that is gone (deleted, archived) leaves the focus.
  if (focus.value && !next.some((t) => t.id === focus.value!.id)) leaveFocus();
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

/**
 * The code feed: the commits, merges, landings and pushes since the newest one taken (from the start of the timeline
 * at first). A fresh one plays live; any joins the timeline and the map. The read is asked for all teams at once; if
 * that is refused it is asked per team, and if that is refused too (a core without it) the map keeps to the steps.
 */
async function codeFeed(first: boolean): Promise<CodeEvent[]> {
  if (codeMode === 'off') return [];
  const since = new Date(first ? Date.now() - MaxWindowMinutes * 60_000 : eventsSince).toISOString();
  let got: CodeEvent[] = [];
  try {
    if (codeMode === 'all') {
      try { got = readCodeEvents(await api.call<unknown>('monitor.codeEvents', { since })); }
      catch { codeMode = 'team'; }
    }
    if (codeMode === 'team')
      for (const t of teams.value) got.push(...readCodeEvents(await api.call<unknown>('monitor.codeEvents', { team: t.name, since })));
  } catch {
    codeMode = 'off';
    return [];
  }
  const fresh = got.filter((e) => { const k = codeEventKey(e); if (eventKeys.has(k)) return false; eventKeys.add(k); return true; });
  fresh.sort((a, b) => timeOf(a) - timeOf(b));
  for (const e of fresh) eventsSince = Math.max(eventsSince, timeOf(e));
  if (!first) {
    const wall = Date.now();
    for (const e of fresh) live.applyCode(e, performance.now(), mode.value === 'live' && wall - timeOf(e) < FreshStepMs);
  }
  if (fresh.length) events.value = mergeByTime([...events.value], fresh);
  return fresh;
}

/**
 * The network, tool and folder asks waiting for the person (network.asks, pending; tools.asks, pending or installing;
 * folder.asks, pending, B111), read the
 * way Needs you reads them (NetworkAsks.vue, ToolAsks.vue): at the start, on network.asksChanged / tools.asksChanged and
 * on a slow poll. One You item per Needs-you item (a member's network asks on one domain are one, groupAsks), handed to
 * the live model's setAsks, which counts them on the You node and flies a new one to it. Those waiting at the start
 * do not fly. A read that fails (an older core, a remote view that may not read it) counts as none of that kind.
 */
const AsksPollMs = 5000;
let asksLoading = false;
let asksAgain = false;
let asksFirst = true;
let asksAt = 0;
async function loadAsks(): Promise<void> {
  if (asksLoading) { asksAgain = true; return; }
  asksLoading = true;
  try {
    do {
      asksAgain = false;
      asksAt = Date.now();
      const [net, tools, folders] = await Promise.all([
        api.call<NetworkAsk[]>('network.asks', {}).then((r) => (Array.isArray(r) ? r : []), () => [] as NetworkAsk[]),
        api.call<ToolAsk[]>('tools.asks', {}).then((r) => (Array.isArray(r) ? r : []), () => [] as ToolAsk[]),
        api.call<FolderAsk[]>('folder.asks', {}).then((r) => (Array.isArray(r) ? r : []), () => [] as FolderAsk[]),
      ]);
      const asks: { key: string; kind: AskKind; team: string; member: string | null; label: string | null }[] = [
        ...groupAsks(net.filter((a) => a && a.state === 'pending')).map((g) => ({
          key: `n:${g.key}`, kind: 'network' as const, team: g.asks[0].team, member: g.asks[0].memberName || null, label: hostsLabel(g.hosts),
        })),
        ...tools.filter((a) => a && (a.state === 'pending' || a.state === 'installing')).map((a) => ({
          key: `t:${a.id}`, kind: 'tool' as const, team: a.team, member: a.memberName || null, label: a.displayName || a.tool,
        })),
        ...pendingFolderAsks(folders).map((a) => ({
          key: `f:${a.id}`, kind: 'folder' as const, team: a.team, member: folderAskMember(a) || null, label: a.path,
        })),
      ];
      const setAsks = (live as Partial<Pick<MonitorModel, 'setAsks'>>).setAsks;
      if (typeof setAsks !== 'function') return;
      const before = live.effects.length;
      setAsks.call(live, asks, performance.now());
      // Those already waiting when the Monitor opens are counted, not flown in.
      if (asksFirst) live.effects.length = before;
      asksFirst = false;
      if (!asksAgain && mode.value === 'live') publishCounts();
    } while (asksAgain);
  } finally { asksLoading = false; }
}
let offAsks: (() => void) | null = null;

/** Applies rows, steps and code events to a model in the order they happened, without playing them (the start, a replay's past). */
function catchUp(m: MonitorModel, rs: readonly FeedRow[], ss: readonly StepRow[], es: readonly CodeEvent[] = [], into?: ReplayStates) {
  for (const p of inTimeOrder(rs, ss, es)) {
    if ('row' in p) { m.apply(p.row, 0, false); into?.row(p.row); }
    else if ('step' in p) { m.applyStep(p.step, 0, false); into?.step(p.step); }
    else m.applyCode(p.event, 0, false);
  }
}

async function tick(first = false) {
  if (busy) return;
  busy = true;
  try {
    await snapshot();
    const fr = await feed(first);
    const fs = await stepFeed(first);
    const fe = await codeFeed(first);
    await workflowFeed(first, new Set(fr.map((r) => r.team.toLowerCase())));
    if (first) catchUp(live, [...fr].sort((a, b) => timeOf(a) - timeOf(b)), fs, fe);
    // The need-you items new since the last look fly to the You node and sound, once each (B088); those there when the
    // Monitor opens are counted, not flown.
    live.syncNeedYou(performance.now(), !first);
    const from = Date.now() - MaxWindowMinutes * 60_000;
    rows.value = trimByTime(rows.value, from, 4000);
    steps.value = trimByTime(steps.value, from, 30_000);
    events.value = trimByTime(events.value, from, 5000);
    if (eventKeys.size > 20_000) { eventKeys.clear(); for (const e of events.value) eventKeys.add(codeEventKey(e)); }
    replay?.extend(rows.value, steps.value, events.value);
    if (mode.value === 'live') {
      const fx = live.effects.splice(0);
      sound.hear(fx);
      scene?.effects.push(...fx);
    } else live.effects.length = 0;
    if (model === live) activity.value = [...live.activity];
    error.value = null;
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy = false;
    loaded.value = true;
    publishCounts();
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
  m.logNow = () => replay?.at ?? from;
  const st = new ReplayStates();
  // The half hour before the moment sets the states, the map (files idle two minutes have left it) and the activity list.
  const lead = from - 30 * 60_000;
  const before = (x: { at: string }) => timeOf(x) >= lead && timeOf(x) < from;
  catchUp(m, rows.value.filter(before), steps.value.filter(before), events.value.filter(before), st);
  m.effects.length = 0;
  replay = new ReplayClock(rows.value, steps.value, from, speed.value, undefined, events.value);
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
  for (const p of inTimeOrder(due.rows, due.steps, due.events)) {
    if ('row' in p) { model.apply(p.row, t, true); replayStates.row(p.row); }
    else if ('step' in p) { model.applyStep(p.step, t, true); replayStates.step(p.step); }
    else model.applyCode(p.event, t, true);
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
/** A picked member's terminal: on its team page, in that member's pane (App, memberTerminal.ts). */
function openPickedTerminal() {
  const p = pickedTeam.value;
  if (p?.member) emit('openTerminal', p.team.id, p.member.id);
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
    scene.setPinned(pinned.value);
    scene.setPhone?.(phone.value); // a test's stub scene may have none
    if (layoutChanged.value) scene.setLayout(layout.value);
    scene.onHover = (h) => { hover.value = h; };
    scene.onPick = onScenePick;
    scene.onOpenWorkflow = (team, id) => {
      pickSeq++;
      const t = teams.value.find((x) => x.name.toLowerCase() === team.toLowerCase());
      if (t) openWorkflow(t.id, id);
    };
    if (density.value !== DefaultDensity) setDensity(density.value);
  }
  window.addEventListener('keydown', onKey);
  measureWall();
  if (canvas.value && typeof ResizeObserver === 'function') {
    wallObserver = new ResizeObserver(() => measureWall());
    wallObserver.observe(canvas.value);
    const controls = canvas.value.parentElement?.querySelector(':scope > .controls');
    if (controls) wallObserver.observe(controls);
  }
  await tick(true);
  offAsks = api.onEvent?.((e) => { if (e.event === 'network.asksChanged' || e.event === 'tools.asksChanged' || e.event === FOLDER_ASKS_CHANGED) void loadAsks(); }) ?? null;
  await loadAsks();
  timers = [
    setInterval(() => void tick(), props.pollMs ?? 1200),
    // The asks' slow poll (a repeated refusal's count changes without an event), as Needs you polls them.
    setInterval(() => { if (Date.now() - asksAt >= AsksPollMs) void loadAsks(); }, 1000),
    setInterval(() => { clock.value = new Date().toLocaleTimeString(); now.value = Date.now(); }, 1000),
  ];
  clock.value = new Date().toLocaleTimeString();
  if (typeof requestAnimationFrame === 'function') raf = requestAnimationFrame(frame);
});
onBeforeUnmount(() => {
  for (const t of timers) clearInterval(t);
  offAsks?.();
  cancelAnimationFrame(raf);
  window.removeEventListener('keydown', onKey);
  wallObserver?.disconnect();
  sound.destroy();
  scene?.destroy();
});
</script>

<template>
  <div class="monitor" :class="{ phone }" data-test="monitor">
    <div class="main">
      <div class="stage" @pointerdown.capture="onWallDown" @pointerup="onWallUp" @pointercancel="onWallCancel">
        <canvas ref="canvas" class="wall" :class="{ focused: !!focus }" data-test="monitor-canvas"></canvas>
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
          <div v-if="focus" class="focus-chip" data-test="monitor-focus">
            <span class="focus-name">Focused on <b>{{ focus.name }}</b></span>
            <button class="ctl" type="button" data-test="monitor-focus-leave" :title="phone ? 'Show every team' : 'Show every team (Esc, or click the empty wall)'"
              aria-label="Show every team" @click="leaveFocus">×</button>
          </div>
        </div>
        <div class="controls">
          <div class="seg" role="group" aria-label="Camera" data-test="monitor-camera">
            <button class="ctl" :class="{ on: !pinned }" :aria-pressed="!pinned" data-test="monitor-autofit"
              title="Auto fit: the wall keeps everything in view, except for 12 s after you pan or zoom" @click="setPinned(false)">Auto fit</button>
            <button class="ctl" data-test="monitor-fit" title="Fit everything once, then stay pinned (or double-click the wall)" @click="fitOnce">Fit</button>
            <button class="ctl" :class="{ on: pinned }" :aria-pressed="pinned" data-test="monitor-pin"
              title="Pinned: the zoom and position stay where you put them" @click="setPinned(true)">Pinned</button>
          </div>
          <button class="ctl cog" :class="{ on: layoutOpen, changed: layoutChanged }" type="button" data-test="monitor-layout-toggle"
            :aria-expanded="layoutOpen" aria-controls="monitor-layout" aria-label="Monitor settings"
            :title="layoutChanged ? 'Monitor settings: activity and network forces (forces changed from the defaults)' : 'Monitor settings: activity and network forces'" @click="layoutOpen = !layoutOpen">
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M8 5.6a2.4 2.4 0 1 0 0 4.8 2.4 2.4 0 0 0 0-4.8z" fill="none" stroke="currentColor" stroke-width="1.3" />
              <path d="M6.9 1.5h2.2l.35 1.7 1.15.5 1.45-.95 1.55 1.55-.95 1.45.5 1.15 1.7.35v2.2l-1.7.35-.5 1.15.95 1.45-1.55 1.55-1.45-.95-1.15.5-.35 1.7H6.9l-.35-1.7-1.15-.5-1.45.95-1.55-1.55.95-1.45-.5-1.15-1.7-.35V6.9l1.7-.35.5-1.15-.95-1.45L3.95 2.4l1.45.95 1.15-.5z"
                fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round" />
            </svg>
          </button>
          <template v-if="!phone">
            <button class="ctl sound" :class="{ on: soundOn }" type="button" data-test="monitor-sound" :aria-pressed="soundOn"
              :aria-label="soundOn ? 'Sound on' : 'Sound off'" :title="soundOn ? 'Sound on: a quiet tone for needs you, failures, handbacks, commits and merges' : 'Sound off'"
              @click="toggleSound">
              <svg viewBox="0 0 16 16" aria-hidden="true">
                <path d="M2.5 6h2.5l3.5-3v10l-3.5-3H2.5z" fill="currentColor" />
                <path v-if="soundOn" d="M10.6 5.4a3.6 3.6 0 0 1 0 5.2M12.4 3.6a6.2 6.2 0 0 1 0 8.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" />
                <path v-else d="M10.5 6l4 4M14.5 6l-4 4" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" />
              </svg>
            </button>
          </template>
          <button v-if="phone" class="ctl legend-toggle" :class="{ open: legendOpen }" type="button" data-test="monitor-legend-toggle"
            :aria-expanded="legendOpen" :title="legendOpen ? 'Hide the legend' : 'Show the legend'" @click="toggleLegend">{{ legendOpen ? 'Legend ▾' : 'Legend ▸' }}</button>
        </div>
        <div v-if="layoutOpen" id="monitor-layout" class="forces" role="dialog" aria-label="Monitor settings" data-test="monitor-layout" @keydown="onLayoutKey">
          <header>
            <strong>Monitor settings</strong>
            <button class="ctl close" type="button" aria-label="Close" data-test="monitor-layout-close" @click="layoutOpen = false">×</button>
          </header>
          <!-- The density heads the settings; a phone has no room beside the cog, so its sound sits here too. -->
          <div class="wallprefs">
            <span class="name">Activity</span>
            <div class="seg" role="group" aria-label="Density" data-test="monitor-density">
              <button v-for="d in Densities" :key="d.level" class="ctl" :class="{ on: density === d.level }" type="button" :aria-pressed="density === d.level"
                :data-test="`monitor-density-${d.level}`" :title="d.help" @click="setDensity(d.level)">{{ d.label }}</button>
            </div>
            <button v-if="phone" class="ctl sound" :class="{ on: soundOn }" type="button" data-test="monitor-sound" :aria-pressed="soundOn" @click="toggleSound">{{ soundOn ? 'Sound on' : 'Sound off' }}</button>
          </div>
          <label v-for="f in LayoutFields" :key="f.key" class="force" :title="f.help">
            <span class="name">{{ f.label }}</span>
            <input type="range" :min="LayoutMin" :max="LayoutMax" :step="LayoutStep" :value="layout[f.key]" :data-test="`monitor-layout-${f.key}`"
              @input="setLayoutValue(f.key, ($event.target as HTMLInputElement).value)" />
            <span class="val">{{ layout[f.key].toFixed(2) }}×</span>
            <span class="help">{{ f.help }}</span>
          </label>
          <footer>
            <span class="dim">Saved for this app.</span>
            <button class="ctl" type="button" :disabled="!layoutChanged" data-test="monitor-layout-reset" @click="resetLayout">Reset</button>
          </footer>
        </div>
        <button v-if="!phone" class="ctl legend-toggle" :class="{ open: legendOpen }" type="button" data-test="monitor-legend-toggle"
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
          <span class="sep" />
          <span data-test="monitor-legend-code"><i class="dot" :style="{ background: Palette.commit }" />commit</span>
          <span><i class="tone" :style="{ background: Palette.handback }" />merged</span>
          <span><i class="tone" :style="{ background: Palette.done }" />landed</span>
          <span><i class="tone" :style="{ background: Palette.tell }" />pushed</span>
          <template v-if="mode === 'live'">
            <span class="sep" />
            <span class="wfl" data-test="monitor-legend-workflows">
              <span>workflows:</span>
              <span v-for="l in WorkflowLooks" :key="l.status">
                <svg class="wfi" :class="l.status" viewBox="0 0 16 16" aria-hidden="true" :style="{ color: Palette[l.tone] }">
                  <circle cx="8" cy="8" r="6.6" fill="none" stroke="currentColor" stroke-width="1.4" :stroke-dasharray="l.status === 'working' ? '10 4.5' : undefined" />
                  <path :d="WorkflowIconPath[l.icon]" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
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
                {{ r.team }}: {{ workflowLabel(w) }} · {{ workflowLook(w).label }} · {{ w.state === 'open' ? 'last entry' : 'ended' }} {{ entryTime(workflowTime(w)) }}
              </button>
            </li>
          </template>
        </ul>
        <!-- The You node's breakdown (B088): hovered on a desktop, tapped on a phone; the same list as the header's count. -->
        <div v-if="youShown" class="you-tip" :class="{ open: youOpen }" :style="youTipStyle" data-test="monitor-you-tip"
          :role="youOpen ? 'dialog' : undefined" :aria-label="youOpen ? 'What needs you' : undefined">
          <strong data-test="monitor-you-summary">{{ needYouSummary(needYou) }}</strong>
          <span v-for="i in needYou.slice(0, YouTipLines)" :key="i.id" class="you-line" data-test="monitor-you-item" :data-kind="i.kind">
            <b :style="{ color: Palette[NeedYouLook[i.kind]] }">{{ needYouWord(i.kind) }}</b>
            {{ i.team }} / {{ i.member ? spaced(i.member) : 'the team' }}<span v-if="i.label" class="dim"> · {{ i.label }}</span>
          </span>
          <span v-if="needYou.length > YouTipLines" class="dim">+{{ needYou.length - YouTipLines }} more</span>
          <button v-if="youOpen" type="button" class="ctl" data-test="monitor-you-open" @click="openNeedsYouFromTip">Open Needs you</button>
          <span v-else class="dim">click to open Needs you</span>
        </div>
        <div v-if="hover && hover.kind !== 'you'" class="tip" :style="{ left: `${hover.x}px`, top: `${hover.y}px` }" data-test="monitor-tip">
          <template v-if="hover.kind === 'file'">
            <strong class="path" data-test="monitor-tip-path">{{ hover.path }}</strong>
            <span :style="{ color: StepLook[hover.fileKind ?? 'edit'].color }">{{ hover.fileKind === 'create' ? 'new' : hover.fileKind === 'read' ? 'read' : 'changed' }}{{ (hover.touches ?? 1) > 1 ? ` · ${hover.touches} touches` : '' }}</span>
            <span v-if="hover.members?.length" class="dim">by {{ hover.members.map(spaced).join(', ') }}</span>
            <span class="dim">{{ hover.sha ? `in commit ${hover.sha}` : 'not committed yet (from the steps)' }} · {{ hover.team }}</span>
          </template>
          <template v-else-if="hover.kind === 'folder'">
            <strong class="path">{{ hover.name }}</strong>
            <span v-if="hover.more?.length" class="dim">{{ hover.more.length }} file{{ hover.more.length === 1 ? '' : 's' }} touched</span>
            <span v-for="p in (hover.more ?? []).slice(0, 8)" :key="p" class="dim path">{{ p }}</span>
            <span v-if="(hover.more?.length ?? 0) > 8" class="dim">+{{ hover.more!.length - 8 }} more</span>
          </template>
          <template v-else-if="hover.kind === 'commit'">
            <strong data-test="monitor-tip-commit">{{ hover.message || '(no message)' }}</strong>
            <span :style="{ color: Palette.commit }">{{ hover.sha }} · {{ hover.touches }} file{{ hover.touches === 1 ? '' : 's' }}</span>
            <span class="dim">{{ spaced(hover.name) }} · {{ hover.team }}</span>
          </template>
          <template v-else-if="hover.kind === 'workflow' && hover.workflow">
            <strong>#{{ hover.workflow.id }} {{ hover.workflow.title }}</strong>
            <span v-if="hoverItem" class="dim" data-test="monitor-tip-item">Backlog item <b>{{ hoverItem.label }}</b>{{ hoverItem.archived ? ' (archived)' : '' }}</span>
            <span :style="{ color: Palette[workflowLook(hover.workflow).tone] }">{{ workflowLook(hover.workflow).label }}</span>
            <span v-if="hover.workflow.busy.length" class="dim">{{ hover.workflow.busy.join(' · ') }}</span>
            <span v-else-if="hover.workflow.ending" class="dim">{{ hover.workflow.state === 'closed' ? 'Closed' : hover.workflow.state === 'expired' ? 'Expired' : 'Delivered' }}: {{ hover.workflow.ending }}</span>
            <span class="dim">started {{ entryTime(hover.workflow.startedAt) }} · last entry {{ entryTime(hover.workflow.lastAt) }}</span>
            <span class="dim">{{ hover.workflow.messages }} message{{ hover.workflow.messages === 1 ? '' : 's' }} · {{ hover.team }} · click to open</span>
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
        <Timeline :from="windowFrom" :to="now" :rows="rows" :steps="steps" :events="events" :playhead="playhead" :live="mode === 'live'" @seek="startReplay" />
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
          <button v-if="pickedTeam.member" class="ctl" data-test="monitor-open-terminal" :title="`Open ${spaced(pickedTeam.member.name)}'s terminal on the team page`" @click="openPickedTerminal">Terminal</button>
          <button class="ctl" title="Show everything" data-test="monitor-unpick" @click="pick(null)">×</button>
        </template>
        <span v-else class="pick">Activity<template v-if="mode === 'replay'"> · replay</template></span>
        <button class="ctl" title="Hide the activity" aria-label="Hide the activity" aria-expanded="true" data-test="monitor-activity-toggle" @click="toggleActivity">›</button>
      </header>
      <ol v-if="activityOpen">
        <li v-for="r in shown" :key="r.key" :style="{ '--tone': Palette[r.tone] }" data-test="monitor-row">
          <MemberAvatar v-if="r.member" :team="r.team" :member="r.member" :state="rowState(r.team, r.member)" :size="22" wall />
          <span v-else class="noface" />
          <div class="line">
            <div><b>{{ r.verb }}</b> <template v-if="r.verb.endsWith('told')">{{ r.who }}</template><template v-else>· {{ r.who }}</template> <span class="dim">· {{ r.team }} · {{ ago(r.at) }}</span></div>
            <div v-if="r.text" class="text"><CommitText :text="r.text" :team="r.team" /></div>
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
.controls { position: absolute; top: 12px; right: 12px; display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 6px; max-width: calc(100% - 24px); }
/* The speaker beside the density: lit when on. */
.sound { display: inline-flex; align-items: center; padding: 3px 6px; color: #94a3b8; }
.sound svg { width: 15px; height: 15px; }
.sound.on { color: #e0f2fe; border-color: #38bdf8; background: rgba(56, 189, 248, .18); }
/* The focused team's chip under the counts: its × shows every team again. */
.focus-chip { pointer-events: auto; display: inline-flex; align-items: center; gap: 8px; margin-top: 6px; max-width: 100%;
  background: rgba(56, 189, 248, .12); border: 1px solid #38bdf8; border-radius: 999px; padding: 2px 4px 2px 10px; font-size: 12px; color: #e0f2fe; }
.focus-name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.focus-chip .ctl { border-radius: 999px; padding: 0 8px; }
.wallprefs { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.wallprefs .name { color: #e2e8f0; }
.ctl { background: rgba(12, 19, 34, .85); color: #e2e8f0; border: 1px solid #1e293b; border-radius: 6px; padding: 3px 10px; font-size: 12px; }
.ctl:hover:not(:disabled) { border-color: #38bdf8; }
/* Auto fit | Fit | Pinned: one joined control; the camera's mode is lit. */
.seg { display: flex; }
.seg .ctl { border-radius: 0; margin-left: -1px; }
.seg .ctl:first-child { border-radius: 6px 0 0 6px; margin-left: 0; }
.seg .ctl:last-child { border-radius: 0 6px 6px 0; }
.seg .ctl:hover { position: relative; }
.seg .ctl.on { background: rgba(56, 189, 248, .18); border-color: #38bdf8; color: #e0f2fe; position: relative; }
/* The cog beside Pinned, and the forces panel it opens under the controls. */
.cog { display: inline-flex; align-items: center; padding: 3px 6px; color: #94a3b8; }
.cog svg { width: 15px; height: 15px; }
.cog:hover, .cog.on { color: #e0f2fe; }
.cog.changed { color: #7dd3fc; }
.forces { position: absolute; top: 44px; right: 12px; z-index: 3; width: 19rem; display: flex; flex-direction: column; gap: 10px;
  background: rgba(12, 19, 34, .96); border: 1px solid #334155; border-radius: 8px; padding: 10px 12px; font-size: 12px; }
.forces header, .forces footer { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.forces .close { padding: 0 8px; }
.force { display: grid; grid-template-columns: 1fr 3.4rem; grid-template-areas: 'name val' 'range range' 'help help'; gap: 2px 8px; }
.force .name { grid-area: name; color: #e2e8f0; }
.force .val { grid-area: val; text-align: right; color: #94a3b8; font-variant-numeric: tabular-nums; }
.force input { grid-area: range; width: 100%; accent-color: #38bdf8; }
.force .help { grid-area: help; color: #64748b; font-size: 11px; }
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
.you-tip { position: absolute; transform: translateY(-50%); max-width: min(22rem, calc(100% - 80px)); pointer-events: none; display: flex; flex-direction: column; gap: 3px;
  background: rgba(12, 19, 34, .96); border: 1px solid #334155; border-radius: 8px; padding: 8px 10px; font-size: 12px; z-index: 3; }
.you-tip.open { pointer-events: auto; }
.you-tip .you-line { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.you-tip .ctl { align-self: flex-start; margin-top: 4px; }
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
/* A phone (B037): the wall takes the screen, the HUD is compact, the camera, forces cog and legend controls are one row
   of 44 px buttons under it (the forces panel opens above that row), the legend scrolls inside the wall, and the activity is a 44 px bar (open: half the height). Still dark. */
@media (max-width: 759.98px) {
  .hud { padding: 8px 10px 18px; }
  .figures { gap: 10px; flex-wrap: wrap; }
  .figures b { font-size: 16px; }
  .heavy { max-width: 100%; }
  .controls { top: auto; bottom: 8px; left: 8px; right: 8px; justify-content: space-between; z-index: 2; }
  .controls .ctl { min-height: 44px; padding: 0 12px; font-size: 12px; }
  .controls .legend-toggle { position: static; }
  .controls .cog { min-width: 44px; justify-content: center; }
  .controls .cog svg { width: 20px; height: 20px; }
  /* The forces panel sits above the compact row, the screen's width less the margins, scrolling when it is taller. */
  .forces { top: auto; bottom: 60px; left: 8px; right: 8px; width: auto; max-height: calc(100% - 76px); overflow-y: auto; }
  .forces .ctl { min-height: 44px; min-width: 44px; }
  .wallprefs .seg .ctl { padding: 0 10px; }
  .focus-chip .ctl { min-height: 44px; min-width: 44px; }
  .force input { min-height: 44px; }
  .legend { bottom: 60px; padding: 8px 10px; max-height: 40%; overflow-y: auto; pointer-events: auto; }
  .tip { max-width: calc(100vw - 24px); }
  .timebar { padding: 6px 8px 4px; }
  .bar-controls { flex-wrap: wrap; }
  .bar-controls .hint { display: none; }
  .bar-controls .seg { min-height: 44px; min-width: 44px; }
  .live-btn { min-height: 44px; }
  .activity { height: 50%; }
  .activity.collapsed { height: 44px; }
  .activity header .ctl { min-height: 44px; min-width: 44px; }
}
</style>
