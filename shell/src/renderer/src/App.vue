<script setup lang="ts">
import TeamsList from './components/TeamsList.vue';
import TeamManageDialog from './components/TeamManageDialog.vue';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ConversationSummary, DetectedCli, LinkStatus, YawbleApi, Overview, TeamSummary } from '../../shared/api';
import { errorText, resolveApi } from './api';
import { cliLabel } from './agentClis';
import HostChecksBanner from './components/HostChecksBanner.vue';
import CompanionAsks from './components/CompanionAsks.vue';
import MemberSettingsDialog from './components/MemberSettingsDialog.vue';
import NewTeamDialog from './components/NewTeamDialog.vue';
import TeamView from './components/TeamView.vue';
import LinkDialog from './components/LinkDialog.vue';
import ConciergeView from './components/ConciergeView.vue';
import MonitorView from './monitor/MonitorView.vue';
import BoardView from './board/BoardView.vue';
import DocumentsView from './components/DocumentsView.vue';
import MenuBar, { type Menu } from './components/MenuBar.vue';
import TerminalPane from './components/TerminalPane.vue';
import { terminalPane, toggleTerminalPane } from './terminalPane';
import BacklogView from './board/BacklogView.vue';
import Sidebar, { type View } from './components/Sidebar.vue';
import SettingsView, { type SettingsTab } from './components/SettingsView.vue';
import Icon from './components/Icon.vue';
import LoadingNote from './components/LoadingNote.vue';
import GitBanner from './components/GitBanner.vue';
import LogsDialog from './components/LogsDialog.vue';
import type { AgentOption } from './components/Composer.vue';

// The main window: the sidebar (New chat, the places, the teams, the Concierge chats) and one place at a time. The
// Concierge is home: a chat with an agent that knows every team. The same screens run in a browser through the
// Cloud Link (remote.html), where the computer-only parts are hidden.
const props = defineProps<{ api?: YawbleApi; pollMs?: number }>();
const api = resolveApi(props.api);

// ---- where the person is ----
// A fresh start opens a new message to the Concierge. The screen the person is on is kept in sessionStorage, which a
// reload of the window (Ctrl+R) keeps and a new start of the app clears; only the sidebar's state lasts across starts.
interface Place { view: View; conversationId: string | null; teamId: number | null; collapsed: boolean }
const read = (s: () => Storage): Partial<Place> => { try { return JSON.parse(s().getItem('yawble.place') ?? '{}'); } catch { return {}; } };
const kept = read(() => localStorage);
const saved = read(() => sessionStorage);
const view = ref<View>(saved.view ?? 'concierge');
const prefTab = ref<SettingsTab>('general');
const conversationId = ref<string | null>(saved.view ? saved.conversationId ?? null : null);
const selected = ref<number | null>(saved.view ? saved.teamId ?? null : null);
const collapsed = ref(kept.collapsed ?? false);
watch([view, conversationId, selected, collapsed], () => {
  const place = { view: view.value, conversationId: conversationId.value, teamId: selected.value, collapsed: collapsed.value };
  try { sessionStorage.setItem('yawble.place', JSON.stringify(place)); } catch { /* storage blocked: a reload starts at home */ }
  try { localStorage.setItem('yawble.place', JSON.stringify({ collapsed: collapsed.value })); } catch { /* likewise */ }
});

const overview = ref<Overview | null>(null);
const conversations = ref<ConversationSummary[]>([]);
// The Concierge chats have had their first answer (the sidebar shows the loading state until then).
const chatsLoaded = ref(false);
const agents = ref<AgentOption[]>([]);
/** Logs (sidebar): every log the app keeps, in one dialog. */
const logsOpen = ref(false);
const error = ref<string | null>(null);
const dialog = ref<null | 'newTeam' | 'link'>(null);
const link = ref<LinkStatus | null>(null);
const settingsFor = ref<number | null>(null);
const settingsTab = ref<'general' | 'policy' | 'guardrails'>('general');
const teamView = ref<InstanceType<typeof TeamView> | null>(null);
// The team a new chat starts with in focus: the one the person last looked at (or none).
const focusTeamId = ref<number | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;
const offs: (() => void)[] = [];

// The desktop window draws its own title bar: the top bar is where the window is dragged, and it leaves room for the
// window controls (top right on Windows and Linux, traffic lights top left on macOS). A browser view has neither.
const ownBar = !api.remote && !!api.platform;
const mac = api.platform === 'darwin';
const linkState = computed(() => link.value?.state ?? 'off');
const linkTitle = computed(() => {
  const l = link.value;
  switch (l?.state) {
    case 'connected': return `Cloud Link: connected to ${l.url}${l.viewers.length ? ` · viewing now: ${l.viewers.join(', ')}` : ''}`;
    case 'connecting': return 'Cloud Link: connecting…';
    case 'offline': return `Cloud Link: not connected, retrying${l.error ? ` (${l.error})` : ''}`;
    case 'pairing': return 'Cloud Link: waiting for the pairing code to be entered on the site';
    default: return 'Cloud Link: off. Attach this computer to a cloud site to see and drive it from a browser.';
  }
});

const allTeams = computed(() => overview.value?.teams ?? []);
/** Archived teams are put away: not in the sidebar or the grid, only under "Show archived". */
const teams = computed(() => allTeams.value.filter((t) => !t.archived));
const archivedTeams = computed(() => allTeams.value.filter((t) => t.archived));
const team = computed(() => allTeams.value.find((t) => t.id === selected.value) ?? null);
const defaultCli = computed(() => agents.value.find((a) => a.ready)?.id ?? 'claude');
const title = computed(() => {
  if (view.value === 'concierge') return conversations.value.find((c) => c.id === conversationId.value)?.title ?? 'New chat';
  if (view.value === 'teams') return team.value ? team.value.name : 'Teams';
  if (view.value === 'documents') return 'Documents';
  if (view.value === 'backlog') return 'Backlog';
  return view.value === 'board' ? 'Board' : view.value === 'settings' ? 'Settings' : 'Monitor';
});

async function refresh() {
  try {
    overview.value = await api.call<Overview>('app.overview');
    error.value = null;
    if (selected.value !== null && !overview.value.teams.some((t) => t.id === selected.value)) selected.value = null;
  } catch (e) {
    error.value = errorText(e);
  }
}

async function loadConversations() {
  try {
    conversations.value = await api.call<ConversationSummary[]>('concierge.list');
    if (conversationId.value && !conversations.value.some((c) => c.id === conversationId.value)) conversationId.value = null;
  } catch { /* an older core without the Concierge: the list stays empty */ } finally { chatsLoaded.value = true; }
}

async function loadAgents() {
  try {
    const found = await api.call<DetectedCli[]>('agents.detect');
    agents.value = found.filter((c) => c.found).map((c) => ({ id: c.cliId, label: cliLabel(c.cliId), ready: c.signIn?.status === 'signed-in' }));
  } catch { agents.value = []; }
}

function newChat() {
  view.value = 'concierge';
  conversationId.value = null;
}

function openConversation(id: string) {
  view.value = 'concierge';
  conversationId.value = id;
}

// A workflow chosen on the Monitor: its team's workflows, with that workflow selected.
function openWorkflow(teamId: number, workflow: number) { openTeam(teamId, 'workflows', workflow); }

function openTeam(id: number, panel?: 'workflows', workflow?: number) {
  view.value = 'teams';
  selected.value = id;
  focusTeamId.value = id;
  // The Monitor's workflow nodes open the team at its workflows.
  if (panel === 'workflows') void nextTick(() => teamView.value?.openWorkflows(workflow));
}

// The team settings opened from a card in the Teams list (the same dialog as the team page's Settings).
const manageTeam = ref<TeamSummary | null>(null);

/** "Discuss with the Concierge" on a team's question: a new chat about that team, the question already in the box. */
const conciergeDraft = ref<string | null>(null);
function discuss(teamId: number, d: { from: string; question: string }) {
  const name = allTeams.value.find((t) => t.id === teamId)?.name ?? 'the team';
  const quoted = d.question.trim().split('\n').map((l) => `> ${l}`).join('\n');
  conciergeDraft.value = `${name}'s ${d.from} asks me:\n\n${quoted}\n\nHelp me decide. What should I answer?`;
  askAbout(teamId);
}

function askAbout(teamId: number) {
  focusTeamId.value = teamId;
  newChat();
}

async function rename(id: string, t: string) {
  try { await api.call('concierge.rename', { id, title: t }); } catch (e) { error.value = errorText(e); }
  void loadConversations();
}

// The chat being deleted: its row in the sidebar keeps "Deleting…" until the core answers.
const removingChat = ref<string | null>(null);
async function remove(id: string) {
  if (removingChat.value) return;
  removingChat.value = id;
  try {
    try { await api.call('concierge.delete', { id }); } catch (e) { error.value = errorText(e); }
    if (conversationId.value === id) conversationId.value = null;
    await loadConversations();
  } finally { removingChat.value = null; }
}

/** A place in the sidebar. Teams always opens the list of teams, not the team last looked at. */
function go(place: View) {
  view.value = place;
  if (place === 'teams') selected.value = null;
}

/** Opens Settings at a tab (the hint "Settings → Agents", a member's link to the global guardrails). */
function menu(item: SettingsTab) {
  prefTab.value = item;
  integrationsTab.value = null;
  view.value = 'settings';
}
/** Which Integrations tab Settings opens at (Needs You's "reconnect" link opens Connections). */
const integrationsTab = ref<'cli' | 'api' | 'mcp' | 'connections' | null>(null);
function openConnections() {
  prefTab.value = 'integrations';
  integrationsTab.value = 'connections';
  view.value = 'settings';
}

function created(teamId: number) {
  dialog.value = null;
  openTeam(teamId);
  void refresh();
}

function changed() {
  void refresh();
  void teamView.value?.refresh();
}

onMounted(() => {
  if (!api.remote) {
    offs.push(api.onEvent((e) => { if (e.event === 'link.state') link.value = e.data as LinkStatus; }));
    api.call<LinkStatus>('link.status').then((s) => { link.value = s; }).catch(() => {});
  }
  // A turn that ends, a review turn the core starts for a watch, or an update posted changes the list (its dots, its order).
  offs.push(api.onEvent((e) => {
    if (e.event === 'concierge.update') void loadConversations();
    else if (e.event === 'concierge.turn') {
      const t = e.data as { conversationId: string; state: string };
      if (t.state !== 'running' || !conversations.value.find((c) => c.id === t.conversationId)?.running) void loadConversations();
    }
  }));
  void refresh();
  void loadConversations();
  void loadAgents();
  timer = setInterval(refresh, props.pollMs ?? 1000);
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); for (const off of offs) off(); window.removeEventListener('keydown', shortcut); });

// The window's own title (taskbar, Alt+Tab) follows the place; the page shows its own heading.
watch(title, (t) => { document.title = t ? `${t} · Yawble` : 'Yawble'; }, { immediate: true });

// ---- the menu bar (File, Edit, View, Help) and its shortcuts
const desktop = typeof api.windowCommand === 'function';
const isMac = api.platform === 'darwin';
const mod = isMac ? '⌘' : 'Ctrl+';
const menus = computed<Menu[]>(() => [
  { label: 'File', items: [
    { id: 'newChat', label: 'New chat', keys: `${mod}N` },
    { id: 'newTeam', label: 'New team…' },
    { separator: true },
    { id: 'settings', label: 'Settings', keys: `${mod},` },
    ...(desktop ? [{ separator: true } as const, { id: 'quit', label: isMac ? 'Quit Yawble' : 'Exit', keys: isMac ? '⌘Q' : undefined }] : []),
  ] },
  ...(desktop ? [{ label: 'Edit', items: [
    { id: 'undo', label: 'Undo', keys: `${mod}Z` }, { id: 'redo', label: 'Redo', keys: isMac ? '⇧⌘Z' : 'Ctrl+Y' },
    { separator: true } as const,
    { id: 'cut', label: 'Cut', keys: `${mod}X` }, { id: 'copy', label: 'Copy', keys: `${mod}C` }, { id: 'paste', label: 'Paste', keys: `${mod}V` },
    { id: 'selectAll', label: 'Select all', keys: `${mod}A` },
  ] }] : []),
  { label: 'View', items: [
    { id: 'terminal', label: 'Terminal', keys: `${mod}\``, checked: terminalPane.open },
    { id: 'sidebar', label: 'Sidebar', keys: `${mod}B`, checked: !collapsed.value },
    { separator: true },
    { id: 'go:concierge', label: 'Concierge' }, { id: 'go:documents', label: 'Documents' }, { id: 'go:backlog', label: 'Backlog' },
    { id: 'go:teams', label: 'Teams' }, { id: 'go:board', label: 'Board' }, { id: 'go:monitor', label: 'Monitor' }, { id: 'go:logs', label: 'Logs' },
    ...(desktop ? [
      { separator: true } as const,
      { id: 'zoomIn', label: 'Zoom in', keys: `${mod}=` }, { id: 'zoomOut', label: 'Zoom out', keys: `${mod}-` }, { id: 'zoomReset', label: 'Actual size', keys: `${mod}0` },
      { separator: true } as const,
      { id: 'reload', label: 'Reload', keys: `${mod}R` }, { id: 'devtools', label: 'Developer tools', keys: isMac ? '⌥⌘I' : 'Ctrl+Shift+I' },
    ] : []),
  ] },
  { label: 'Help', items: [
    { id: 'about', label: 'About Yawble' },
    { id: 'go:settings-agents', label: 'Agents and sign-ins' },
  ] },
]);

function command(id: string) {
  if (id.startsWith('go:')) {
    const place = id.slice(3);
    if (place === 'settings-agents') { menu('agents'); return; }
    if (place === 'logs') { logsOpen.value = true; return; }
    go(place as View);
    return;
  }
  switch (id) {
    case 'newChat': newChat(); break;
    case 'newTeam': dialog.value = 'newTeam'; break;
    case 'settings': menu('general'); break;
    case 'terminal': toggleTerminalPane(); break;
    case 'sidebar': collapsed.value = !collapsed.value; break;
    case 'about': menu('system'); break;
    default: if (api.windowCommand) api.windowCommand(id as Parameters<NonNullable<typeof api.windowCommand>>[0]);
  }
}

/** The window's own shortcuts (the menu's); typing in a field keeps its own Ctrl+Z/C/V/A. */
function shortcut(e: KeyboardEvent) {
  const m = isMac ? e.metaKey : e.ctrlKey;
  if (!m || e.altKey) return;
  const k = e.key.toLowerCase();
  const map: Record<string, string> = { '`': 'terminal', b: 'sidebar', n: 'newChat', ',': 'settings', '=': 'zoomIn', '+': 'zoomIn', '-': 'zoomOut', '0': 'zoomReset', r: 'reload' };
  let id = e.shiftKey ? (k === 'i' ? 'devtools' : null) : map[k] ?? null;
  if (id && !desktop && ['zoomIn', 'zoomOut', 'zoomReset', 'reload', 'devtools'].includes(id)) id = null; // the browser's own
  if (!id) return;
  e.preventDefault();
  command(id);
}
window.addEventListener('keydown', shortcut);
watch([view, prefTab], ([v, t], [vBefore, tBefore]) => { if (vBefore === 'settings' && tBefore === 'agents' && (v !== 'settings' || t !== 'agents')) void loadAgents(); });
</script>

<template>
  <div class="app" :class="{ collapsed, 'own-bar': ownBar, mac }">
    <Sidebar :rail="collapsed" :view="view" :conversations="conversations" :teams="teams" :conversation-id="conversationId"
      :team-id="selected" :slots="overview?.slots ?? null" :remote="!!api.remote" :loading="{ teams: !overview && !error, chats: !chatsLoaded }" :removing="removingChat"
      @new-chat="newChat" @open-conversation="openConversation" @open-team="openTeam" @go="go"
      @rename="rename" @remove="remove" @new-team="dialog = 'newTeam'" @collapse="collapsed = true" @expand="collapsed = false" @logs="logsOpen = true" />

    <main class="main">
      <header class="bar">
        <MenuBar :menus="menus" @command="command" />
        <span class="grow" />
        <button v-if="view === 'teams' && team" class="pill" data-test="ask-concierge" @click="askAbout(team.id)"><Icon name="spark" :size="14" />Ask the Concierge</button>
        <button v-if="!api.remote" class="ghost cloud-ind" :class="linkState" data-test="cloud-link-indicator" :title="linkTitle"
          :aria-label="linkTitle" @click="dialog = 'link'">
          <Icon name="cloud" :size="18" :filled="linkState === 'connected'" />
          <span class="cdot" />
          <span v-if="link?.state === 'connected' && link.viewers.length" class="cnum" data-test="cloud-link-viewers">{{ link.viewers.length }}</span>
        </button>
      </header>
      <p v-if="terminalPane.stopError" class="error banner" data-test="terminal-stop-error">{{ terminalPane.stopError }} <button class="mini" @click="terminalPane.stopError = null">Dismiss</button></p>
      <p v-if="error" class="error banner" data-test="error">Core unavailable: {{ error }}</p>
      <HostChecksBanner :api="api" />
      <LogsDialog v-if="logsOpen" :api="api" @close="logsOpen = false" />
      <GitBanner :api="api" />
      <CompanionAsks v-if="!api.remote" :api="api" :poll-ms="pollMs" />

      <div class="content">
      <div class="place">
        <ConciergeView v-if="view === 'concierge'" :conversation-id="conversationId" :teams="teams" :agents="agents"
          :default-cli="defaultCli" :focus-team-id="focusTeamId" :api="api" :draft="conciergeDraft" @drafted="conciergeDraft = null"
          @opened="conversationId = $event; loadConversations()" @changed="loadConversations" />

        <section v-else-if="view === 'teams'" class="teams-place">
          <TeamView v-if="team" :key="team.id" ref="teamView" :team="team" :api="api" :poll-ms="pollMs" @settings="settingsTab = 'general'; settingsFor = $event"
            @guardrail="settingsTab = 'guardrails'; settingsFor = $event" @changed="refresh" @deleted="selected = null; refresh()"
            @discuss="discuss(team.id, $event)" />
          <TeamsList v-else-if="overview" class="team-grid-wrap" :teams="teams" :archived="archivedTeams" :api="api" :poll-ms="pollMs" :refresh="refresh"
            @open="openTeam" @settings="manageTeam = $event" @new-team="dialog = 'newTeam'" />
          <p v-else-if="error" class="error pad" data-test="teams-error">The teams could not be loaded: {{ error }}</p>
          <div v-else class="pad" data-test="loading"><LoadingNote text="Loading teams (starting the core)…" /></div>
        </section>

        <DocumentsView v-else-if="view === 'documents'" :teams="teams" :archived="archivedTeams" :api="api" :loading="!overview && !error" />
        <MonitorView v-else-if="view === 'monitor'" :api="api" @open-team="openTeam" @open-workflow="openWorkflow" />
        <BoardView v-else-if="view === 'board'" :api="api" :poll-ms="pollMs" :team-id="focusTeamId" @open-team="openTeam" @backlog="go('backlog')" @connections="openConnections" />
        <BacklogView v-else-if="view === 'backlog'" :teams="teams" :teams-loading="!overview && !error" :api="api" :poll-ms="pollMs" @open-team="openTeam"
          @board="focusTeamId = $event; go('board')" />
        <SettingsView v-else-if="view === 'settings'" :api="api" :tab="prefTab" :teams="teams" :poll-ms="pollMs" :integrations-tab="integrationsTab" @tab="prefTab = $event; integrationsTab = null" @connections="openConnections" @changed="changed" @link="link = $event as LinkStatus" />

      </div>
      <TerminalPane v-if="terminalPane.open" :api="api" :poll-ms="pollMs" />
      </div>
    </main>

    <NewTeamDialog v-if="dialog === 'newTeam'" :api="api" @close="dialog = null" @created="created" />
    <TeamManageDialog v-if="manageTeam" :api="api" :team="manageTeam" @close="manageTeam = null" @changed="refresh"
      @deleted="manageTeam = null; refresh()" />
    <LinkDialog v-if="dialog === 'link'" :api="api" @close="dialog = null" @changed="link = $event" />
    <MemberSettingsDialog v-if="settingsFor !== null" :key="`${settingsFor}-${settingsTab}`" :member-id="settingsFor" :initial-tab="settingsTab" :api="api" :poll-ms="pollMs" @close="settingsFor = null" @changed="changed" />
  </div>
</template>

<style scoped>
.app { display: grid; grid-template-columns: 268px 1fr; height: 100vh; background: var(--surface); }
.app.collapsed { grid-template-columns: 56px 1fr; }
.main { display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.bar { display: flex; align-items: center; gap: 6px; padding: 10px 16px; min-height: 52px; }
.title { font-weight: 600; font-size: 15px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.grow { flex: 1; }
.ghost { border: 0; background: transparent; }
.icon-btn { padding: 6px; border-radius: 8px; color: var(--muted); display: grid; place-items: center; }
.icon-btn:hover { background: var(--hover); color: var(--ink); }
.pill { display: inline-flex; align-items: center; gap: 6px; border-radius: 16px; padding: 5px 12px; font-size: 13px; }
.banner { padding: 6px 18px; margin: 0; }
/* The Cloud Link indicator: a cloud with a state dot (none when off), and how many people are viewing. */
.cloud-ind { position: relative; display: inline-flex; align-items: center; gap: 4px; padding: 6px 8px; border-radius: 8px; color: var(--muted); }
.cloud-ind:hover { background: var(--hover); color: var(--ink); }
.cloud-ind.off { opacity: .7; }
.cloud-ind.connected { color: #2e9d4f; } /* connected: the cloud itself is filled green, no dot */
.cdot { position: absolute; left: 21px; top: 17px; width: 8px; height: 8px; border-radius: 50%; border: 2px solid var(--surface); display: none; }
.cloud-ind.connecting .cdot, .cloud-ind.pairing .cdot { display: block; background: #e0a400; }
.cloud-ind.offline .cdot { display: block; background: #c9473d; }
.cnum { font-size: 12px; font-weight: 600; }
/* Own title bar: drag by the top bar and the sidebar's top row; keep clear of the window controls. */
.own-bar .bar { -webkit-app-region: drag; height: 52px; box-sizing: border-box;
  padding-right: calc(10px + 100vw - env(titlebar-area-x, 0px) - env(titlebar-area-width, 100vw)); }
.own-bar .bar button, .own-bar .bar input, .own-bar .bar select { -webkit-app-region: no-drag; }
.own-bar :deep(.brand) { -webkit-app-region: drag; }
.own-bar :deep(.brand button) { -webkit-app-region: no-drag; }
.own-bar.mac :deep(.brand) { padding-left: 76px; }
.own-bar.mac :deep(.brand .logo) { display: none; }
.own-bar.mac.collapsed :deep(.rail) { padding-top: 34px; } /* the traffic lights sit over the rail's top */
.content { flex: 1; min-height: 0; display: flex; }
.place { flex: 1; min-width: 0; min-height: 0; overflow: hidden; }
.teams-place { height: 100%; overflow: auto; padding: 4px 24px 24px; }
.team-grid-wrap { max-width: none; }
.small { font-size: 12.5px; }
.tiny { font-size: 11px; }
.pad { padding: 24px; }
</style>
