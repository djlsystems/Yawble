<script setup lang="ts">
import TeamsList from './components/TeamsList.vue';
import TeamManageDialog from './components/TeamManageDialog.vue';
import { skillLink } from './skillLink';
import WorkflowsDialog from './components/WorkflowsDialog.vue';
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import type { AgentProfileRow, ConversationSummary, DetectedCli, LinkStatus, YawbleApi, Overview, TeamSummary, WorkflowSummary } from '../../shared/api';
import { errorText, resolveApi } from './api';
import { cliLabel } from './agentClis';
import HostChecksBanner from './components/HostChecksBanner.vue';
import CompanionAsks from './components/CompanionAsks.vue';
import NetworkAsks from './components/NetworkAsks.vue';
import ToolAsks from './components/ToolAsks.vue';
import FolderAsks from './components/FolderAsks.vue';
import MemberSettingsDialog from './components/MemberSettingsDialog.vue';
import NewTeamDialog from './components/NewTeamDialog.vue';
import TeamView from './components/TeamView.vue';
import LinkDialog from './components/LinkDialog.vue';
import ConciergeView from './components/ConciergeView.vue';
import MonitorView from './monitor/MonitorView.vue';
import BoardView from './board/BoardView.vue';
import { discussRequest } from './conciergeDiscuss';
import DocumentsView from './components/DocumentsView.vue';
import MenuBar, { type Menu, type MenuItem as MenuBarItem } from './components/MenuBar.vue';
import TerminalPane from './components/TerminalPane.vue';
import { terminalPane } from './terminalPane';
import { openMemberTerminal, registerTeamPage } from './memberTerminal';
import BacklogView from './board/BacklogView.vue';
// The Workflows page is loaded when first opened: the window's first paint does not wait for it.
// A page loaded on first use: when its file is gone (the app was rebuilt or updated while this window stayed open), it
// says so with a Reload button instead of leaving the page blank.
const WorkflowsView = defineAsyncComponent({ loader: () => import('./workflows/WorkflowsView.vue'), errorComponent: PageLoadFailed });
import { backlogRequest, isWorkflowsHash, workflowsRequest } from './workflows/page';
import { WORKFLOWS_LIST, type WorkflowsListAnswer } from '../../shared/api';
import Sidebar, { type View } from './components/Sidebar.vue';
import SettingsView, { type SettingsTab } from './components/SettingsView.vue';
import Icon from './components/Icon.vue';
import LoadingNote from './components/LoadingNote.vue';
import GitBanner from './components/GitBanner.vue';
import LogsDialog from './components/LogsDialog.vue';
import ScriptsDialog from './components/scripts/ScriptsDialog.vue';
import UpdateBanner from './components/UpdateBanner.vue';
import ThemeToggle from './components/ThemeToggle.vue';
import ConfirmDialog from './components/ConfirmDialog.vue';
import TimelineDialog from './timeline/TimelineDialog.vue';
import CommitDialog from './components/CommitDialog.vue';
import { commitTeams, gitRequest, installCommitLinks, teamsFor } from './commitView';
import { installCommitResolve } from './commitResolve';
import CommitChooser from './components/CommitChooser.vue';
import { theme } from './theme';
import type { AgentOption } from './components/Composer.vue';
import { installTapTitles, usePhone } from './phone';
import { pushEscape } from './escapeClose';
import { findWorkflowTeam, parseRemoteHash } from './remoteHash';
import { loadConciergeDefault, newChatAgent, savedConciergeAgent } from './conciergeDefault';
import { isEmbed, startEmbedBridge, type EmbedNavState } from './embed';
import { readOnly } from './remoteAccess';
import EmbedRenameDialog from './components/EmbedRenameDialog.vue';
import PageLoadFailed from './components/PageLoadFailed.vue';

// The main window: the sidebar (New chat, the places, the teams, the Concierge chats) and one place at a time. The
// Concierge is home: a chat with an agent that knows every team. The same screens run in a browser through the
// Cloud Link (remote.html), where the computer-only parts are hidden.
const props = defineProps<{ api?: YawbleApi; pollMs?: number }>();
const api = resolveApi(props.api);
/** The remote view embedded in the cloud page (?embed=1): no sidebar and no theme switch; the cloud draws the
 *  sidebar from yawble.nav and sends yawble.navigate / yawble.theme back (embed.ts, the bridge started below). */
const embed = isEmbed();

// ---- where the person is ----
// A fresh start opens a new message to the Concierge. The screen the person is on is kept in sessionStorage, which a
// reload of the window (Ctrl+R) keeps and a new start of the app clears; only the sidebar's state lasts across starts.
interface Place { view: View; conversationId: string | null; teamId: number | null; collapsed: boolean }
const read = (s: () => Storage): Partial<Place> => { try { return JSON.parse(s().getItem('yawble.place') ?? '{}'); } catch { return {}; } };
const kept = read(() => localStorage);
const saved = read(() => sessionStorage);
// A #workflows hash (a link to the Workflows page) opens that page.
const view = ref<View>(typeof location !== 'undefined' && isWorkflowsHash(location.hash) ? 'workflows' : saved.view ?? 'concierge');
const prefTab = ref<SettingsTab>('general');
const conversationId = ref<string | null>(saved.view ? saved.conversationId ?? null : null);
const selected = ref<number | null>(saved.view ? saved.teamId ?? null : null);
const collapsed = ref(kept.collapsed ?? false);
watch([view, conversationId, selected, collapsed], () => {
  const place = { view: view.value, conversationId: conversationId.value, teamId: selected.value, collapsed: collapsed.value };
  try { sessionStorage.setItem('yawble.place', JSON.stringify(place)); } catch { /* storage blocked: a reload starts at home */ }
  try { localStorage.setItem('yawble.place', JSON.stringify({ collapsed: collapsed.value })); } catch { /* likewise */ }
});

// ---- the phone layout (phone.ts): below 760 px wide the sidebar is a drawer, opened by the top bar's menu button
// and closed by a tap on the scrim, Escape, or going anywhere. The folded rail is a desktop thing: a phone never shows it.
const phone = usePhone();
const drawerOpen = ref(false);
let releaseDrawerEscape: (() => void) | null = null;
watch(drawerOpen, (open) => {
  releaseDrawerEscape?.();
  releaseDrawerEscape = open ? pushEscape(() => { drawerOpen.value = false; }) : null;
});
watch(phone, (p) => { if (!p) drawerOpen.value = false; });
watch([view, conversationId, selected], () => { drawerOpen.value = false; });
/** Settings opened from the gear at the top right starts at its list of sections on a phone (a new key: a fresh list each time). */
const settingsLanding = ref(true);
const settingsKey = ref(0);

const overview = ref<Overview | null>(null);
const conversations = ref<ConversationSummary[]>([]);
// The Concierge chats have had their first answer (the sidebar shows the loading state until then).
const chatsLoaded = ref(false);
const agents = ref<AgentOption[]>([]);
const customAgents = ref<{ name: string; baseCli: string }[]>([]);
/** Whether the agent CLIs have been looked for yet. */
const agentsLoaded = ref(false);
/** Logs (sidebar): every log the app keeps, in one dialog. */
/** The update line (Help > Check for updates asks it to check now). */
const updateBanner = ref<InstanceType<typeof UpdateBanner> | null>(null);
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
/** The team whose page is open: on a phone the drawer offers its Documents and Workflows. */
const openTeamName = computed(() => (view.value === 'teams' && team.value ? team.value.name : null));
/** The drawer's Documents / Workflows of the open team: the drawer closes and the team page opens that dialog. */
function openTeamDialog(which: 'documents' | 'workflows') {
  drawerOpen.value = false;
  if (which === 'documents') teamView.value?.openDocuments();
  else teamView.value?.openWorkflows();
}
// A new chat starts on the saved Concierge default when that agent is ready, else on the first ready agent.
const defaultCli = computed(() => newChatAgent(agents.value, savedConciergeAgent.value, customAgents.value));
const title = computed(() => {
  if (view.value === 'concierge') return conversations.value.find((c) => c.id === conversationId.value)?.title ?? 'New chat';
  if (view.value === 'teams') return team.value ? team.value.name : 'Teams';
  if (view.value === 'documents') return 'Documents';
  if (view.value === 'backlog') return 'Backlog';
  if (view.value === 'workflows') return 'Workflows';
  if (view.value === 'logs') return 'Logs';
  if (view.value === 'scripts') return 'Scripts';
  return view.value === 'board' ? 'Kanban board' : view.value === 'settings' ? 'Settings' : 'Monitor';
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
  } catch { agents.value = []; } finally { agentsLoaded.value = true; }
  try {
    const rows = await api.call<AgentProfileRow[]>('agentProfiles.list');
    customAgents.value = rows.filter((r) => r.custom).map((r) => ({ name: r.name, baseCli: r.baseCli }));
  } catch { customAgents.value = []; /* a cloud viewer or an older core: built-ins only */ }
}

function newChat() {
  view.value = 'concierge';
  conversationId.value = null;
  // Read again: the default may have been changed elsewhere (another window, the remote view).
  void loadConciergeDefault(api);
}

function openConversation(id: string) {
  view.value = 'concierge';
  conversationId.value = id;
}

// A workflow chosen on the Monitor (or its team's workflows header): the team's workflows dialog opens over the
// Monitor, with that workflow selected; closing it stays on the Monitor.
const monitorWorkflows = ref<{ team: TeamSummary; id: number | null } | null>(null);
function openWorkflow(teamId: number, workflow?: number) {
  const t = allTeams.value.find((x) => x.id === teamId);
  if (t) monitorWorkflows.value = { team: t, id: workflow ?? null };
  else openTeam(teamId, 'workflows', workflow);
}
function openFromMonitor(teamId: number, panel?: 'workflows') {
  if (panel === 'workflows') openWorkflow(teamId);
  else openTeam(teamId);
}

function openTeam(id: number, panel?: 'workflows', workflow?: number) {
  view.value = 'teams';
  selected.value = id;
  focusTeamId.value = id;
  // The Monitor's workflow nodes open the team at its workflows.
  if (panel === 'workflows') void nextTick(() => teamView.value?.openWorkflows(workflow));
}

// The Workflows page opened from elsewhere (the board's button, a team's "All workflows", a Backlog item's
// "workflow #N"), and the Backlog opened at an item (an Item link). The page / the Backlog take the request as a prop.
watch(workflowsRequest, (r) => { if (!r) return; monitorWorkflows.value = null; view.value = 'workflows'; });
watch(backlogRequest, (r) => { if (!r) return; monitorWorkflows.value = null; view.value = 'backlog'; });
/** The sidebar's Workflows badge: how many are open, across the teams (an older core without workflows.list: none). */
const openWorkflows = ref<number | null>(null);
async function loadOpenWorkflows() {
  try { openWorkflows.value = (await api.call<WorkflowsListAnswer>(WORKFLOWS_LIST, { state: 'open', limit: 1 }))?.counts?.open ?? null; }
  catch { openWorkflows.value = null; }
}
let workflowsTimer: ReturnType<typeof setInterval> | null = null;
onMounted(() => { void loadOpenWorkflows(); workflowsTimer = setInterval(loadOpenWorkflows, Math.max(5000, props.pollMs ?? 5000)); });
onBeforeUnmount(() => { if (workflowsTimer) clearInterval(workflowsTimer); });

/**
 * A link into the remote view (remoteHash.ts): #team={id} opens that team, #workflow={wf} that workflow's thread in its
 * team (found by asking each team's newest 200 workflows, core's most). An unknown or malformed id leaves the view where it
 * is, no error; a well-formed workflow no team lists is named in a short note (in its hinted team's Workflows, if any).
 */
const hashNote = ref<string | null>(null);
async function openFromHash() {
  hashNote.value = null;
  const target = parseRemoteHash(window.location.hash);
  if (!target) return;
  const ids = allTeams.value.map((t) => t.id);
  if (target.kind === 'team') { if (ids.includes(target.team)) openTeam(target.team); return; }
  const teamId = await findWorkflowTeam(target.workflow, ids, async (t) => {
    const list = await api.call<WorkflowSummary[]>('bus.workflows', { teamId: t, limit: 200 });
    return Array.isArray(list) ? list.map((w) => w.id) : [];
  }, target.team);
  // Straight to the team's page with its workflows open (not openWorkflow: that is the Monitor's dialog over the
  // Monitor, shown only while the Monitor is the place, and a link may arrive on any place).
  if (teamId !== null) openTeam(teamId, 'workflows', target.workflow);
  else if (target.team !== null && ids.includes(target.team)) openTeam(target.team, 'workflows', target.workflow);
  else hashNote.value = `Workflow #${target.workflow} was not found in any team's newest 200 workflows.`;
}

// A member's terminal (memberTerminal.ts: the Monitor, View > Terminal, the remote view, an agent's >_) opens here: its
// team's page, where that member's pane switches to Terminal. The pane is keyed by team and member NAME: the member is
// looked up in its team (or, known only by id, in each team in turn).
async function findMember(memberId: number, teamId?: number): Promise<{ teamId: number; team: string; member: string } | null> {
  const known = allTeams.value.find((t) => t.id === teamId);
  for (const t of known ? [known] : allTeams.value) {
    const cards = await api.call<{ member: { id: number; name: string } }[]>('teams.agents', { teamId: t.id }).catch(() => []);
    const c = Array.isArray(cards) ? cards.find((x) => x.member?.id === memberId) : undefined;
    if (c) return { teamId: t.id, team: t.name, member: c.member.name };
  }
  return null;
}
const unregisterTeamPage = registerTeamPage((teamId) => { monitorWorkflows.value = null; openTeam(teamId); }, findMember);
onBeforeUnmount(unregisterTeamPage);

// The team settings opened from a card in the Teams list (the same dialog as the team page's Settings).
const manageTeam = ref<TeamSummary | null>(null);

/** "Discuss with the Concierge" on a team's question: a new chat about that team, the question already in the box. */
const conciergeDraft = ref<string | null>(null);
function discuss(teamId: number, d: { from: string; question: string; summary?: string | null; options?: { label: string }[] | null; details?: string | null }) {
  const name = allTeams.value.find((t) => t.id === teamId)?.name ?? 'the team';
  // A decision with a summary quotes the ask, the answers offered and the details (not the old question text).
  const text = d.summary?.trim()
    ? [d.summary.trim(), d.options?.length ? `Answers offered: ${d.options.map((o) => `"${o.label}"`).join(', ')}.` : '', (d.details ?? '').trim()]
      .filter(Boolean).join('\n\n')
    : d.question.trim();
  const quoted = text.split('\n').map((l) => `> ${l}`).join('\n');
  conciergeDraft.value = `${name}'s ${d.from} asks me:\n\n${quoted}\n\nHelp me decide. What should I answer?`;
  askAbout(teamId);
}

// "Discuss with the Concierge" from Needs You or a Kanban card (conciergeDiscuss.ts): the same new chat, its text ready.
watch(discussRequest, (r) => {
  if (!r) return;
  conciergeDraft.value = r.text;
  if (r.teamId !== null) askAbout(r.teamId);
  else newChat();
});

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
  monitorWorkflows.value = null;
  // A place chosen here opens as it was left, not at what a link last asked for.
  workflowsRequest.value = null;
  backlogRequest.value = null;
  if (place === 'settings') { settingsLanding.value = true; if (phone.value) settingsKey.value++; }
  view.value = place;
  if (place === 'teams') selected.value = null;
}

/** Opens Settings at a tab (the hint "Settings → Agents", a member's link to the global guardrails). */
function menu(item: SettingsTab) {
  // File > Settings opens the list of sections on a phone; a link to one section (Agents, About) opens that section.
  settingsLanding.value = item === 'general';
  prefTab.value = item;
  integrationsTab.value = null;
  view.value = 'settings';
}
// Team settings' "Lessons" link opens Settings at Skills, where the linked skill is selected (SkillsDialog).
watch(skillLink, (l) => { if (l) { manageTeam.value = null; menu('skills'); } });
/** Which Integrations tab Settings opens at (Needs You's "reconnect" link opens Connections). */
const integrationsTab = ref<'cli' | 'api' | 'mcp' | 'connections' | null>(null);
function openConnections() {
  settingsLanding.value = false;
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
  // The remote view opened from the cloud site's org dashboard (#workflow= / #team=, remoteHash.ts) opens at that place
  // once the teams are known; a later change of the hash (the dashboard reusing the tab) opens the new place.
  if (api.remote) {
    void refresh().then(openFromHash);
    window.addEventListener('hashchange', openFromHash);
  } else void refresh();
  void loadConversations();
  void loadAgents();
  void loadConciergeDefault(api);
  timer = setInterval(refresh, props.pollMs ?? 1000);
  // Back in the window (an agent CLI may have been installed or signed in to meanwhile): the agents again.
  window.addEventListener('focus', loadAgentsOnFocus);
});
function loadAgentsOnFocus() { void loadAgents(); }
let offTapTitles: (() => void) | null = null;
onMounted(() => { offTapTitles = installTapTitles(() => phone.value); });
onBeforeUnmount(() => { offTapTitles?.(); releaseDrawerEscape?.(); });

// A commit ID in rendered text opens the Commit view, looked for in the team on screen first, then every team.
// Its Compare with main / Show its branch open that team's page, whose Git dialog takes the request (TeamView).
let offCommitLinks: (() => void) | null = null;
// An ID is a link only once git.commitsResolve confirms it; every rendered candidate on screen is asked in one call.
let offCommitResolve: (() => void) | null = null;
onMounted(() => { offCommitLinks = installCommitLinks(); offCommitResolve = installCommitResolve(teamsFor); });
onBeforeUnmount(() => { offCommitLinks?.(); offCommitResolve?.(); });
watch([team, allTeams], () => { commitTeams.value = { current: team.value?.name ?? null, all: allTeams.value.map((t) => t.name) }; }, { immediate: true });
watch(gitRequest, (r) => {
  const t = r ? allTeams.value.find((x) => x.name === r.team) : null;
  if (t && (view.value !== 'teams' || selected.value !== t.id)) openTeam(t.id);
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); for (const off of offs) off(); window.removeEventListener('keydown', shortcut); window.removeEventListener('focus', loadAgentsOnFocus); window.removeEventListener('hashchange', openFromHash); });

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
    // No Terminal here: a member's terminal is in its pane on the team page, and Settings > Agents opens its Terminal /
    // Test sessions in the right-hand pane itself (terminalPane.ts showChannelTerminal).
    // Embedded: the cloud page has the sidebar and the theme, so neither is offered here.
    ...(embed ? [] : [
      { id: 'sidebar', label: 'Sidebar', keys: `${mod}B`, checked: !collapsed.value },
      { separator: true } as const,
      // The theme (theme.ts): the three modes as a radio, and the title-bar button's flip as a command.
      { id: 'theme:system', label: 'Theme: Use system setting', radio: true, checked: theme.state.mode === 'system' },
      { id: 'theme:light', label: 'Theme: Light', radio: true, checked: theme.state.mode === 'light' },
      { id: 'theme:dark', label: 'Theme: Dark', radio: true, checked: theme.state.mode === 'dark' },
      { id: 'toggleTheme', label: 'Toggle theme' },
    ]),
    { separator: true },
    { id: 'go:concierge', label: 'Concierge' }, { id: 'go:teams', label: 'Teams' }, { id: 'go:backlog', label: 'Backlog' }, { id: 'go:workflows', label: 'Workflows' },
    { id: 'go:board', label: 'Kanban board' }, { id: 'go:documents', label: 'Documents' }, { id: 'go:monitor', label: 'Monitor' }, { id: 'go:scripts', label: 'Scripts' }, { id: 'go:logs', label: 'Logs' },
    ...(desktop ? [
      { separator: true } as const,
      { id: 'zoomIn', label: 'Zoom in', keys: `${mod}=` }, { id: 'zoomOut', label: 'Zoom out', keys: `${mod}-` }, { id: 'zoomReset', label: 'Actual size', keys: `${mod}0` },
      // Developer tools is not on the menu; Ctrl+Shift+I / ⌥⌘I still opens them (shortcut below), for diagnosing.
      { separator: true } as const,
      { id: 'reload', label: 'Reload', keys: `${mod}R` },
    ] : []),
  ] },
  { label: 'Help', items: [
    { id: 'checkUpdates', label: 'Check for updates' },
    { id: 'about', label: 'About Yawble' },
    { id: 'go:settings-agents', label: 'Agents and sign-ins' },
  ] },
]);

function command(id: string) {
  // Embedded: no sidebar to fold or open (Ctrl+B) and no theme of its own (the cloud's yawble.theme sets it).
  if (embed && (id === 'sidebar' || id === 'toggleTheme' || id.startsWith('theme:'))) return;
  if (id !== 'sidebar') drawerOpen.value = false;
  if (id.startsWith('go:')) {
    const place = id.slice(3);
    if (place === 'settings-agents') { menu('agents'); return; }
    go(place as View);
    return;
  }
  switch (id) {
    case 'newChat': newChat(); break;
    case 'newTeam': dialog.value = 'newTeam'; break;
    case 'settings': menu('general'); break;
    case 'sidebar': if (phone.value) drawerOpen.value = !drawerOpen.value; else collapsed.value = !collapsed.value; break;
    case 'about': menu('system'); break;
    case 'checkUpdates': void updateBanner.value?.checkNow(); break;
    case 'theme:system': theme.setMode('system'); break;
    case 'theme:light': theme.setMode('light'); break;
    case 'theme:dark': theme.setMode('dark'); break;
    case 'toggleTheme': theme.toggle(); break;
    default: if (api.windowCommand) api.windowCommand(id as Parameters<NonNullable<typeof api.windowCommand>>[0]);
  }
}

// A remote view on a phone has no menu bar (no room, and most of it is the desktop window's): what a viewer needs from
// it (the theme and Help) is at the bottom of the drawer instead. New chat, New team, Logs and the places are in
// the drawer already, and Settings is the gear in the top bar.
const menuInDrawer = computed(() => phone.value && !!api.remote);
const DRAWER_COMMANDS = ['theme:system', 'theme:light', 'theme:dark', 'checkUpdates', 'about', 'go:settings-agents'];
const drawerMenu = computed<MenuBarItem[]>(() => (menuInDrawer.value
  ? menus.value.flatMap((m) => m.items).filter((i): i is MenuBarItem => !i.separator && DRAWER_COMMANDS.includes(i.id))
  : []));

/** The window's own shortcuts (the menu's); typing in a field keeps its own Ctrl+Z/C/V/A. */
function shortcut(e: KeyboardEvent) {
  const m = isMac ? e.metaKey : e.ctrlKey;
  if (!m || e.altKey) return;
  const k = e.key.toLowerCase();
  const map: Record<string, string> = { b: 'sidebar', n: 'newChat', ',': 'settings', '=': 'zoomIn', '+': 'zoomIn', '-': 'zoomOut', '0': 'zoomReset', r: 'reload' };
  let id = e.shiftKey ? (k === 'i' ? 'devtools' : null) : map[k] ?? null;
  if (id && !desktop && ['zoomIn', 'zoomOut', 'zoomReset', 'reload', 'devtools'].includes(id)) id = null; // the browser's own
  if (!id) return;
  e.preventDefault();
  command(id);
}
window.addEventListener('keydown', shortcut);

// ---- Embedded in the cloud page. The bridge publishes what the sidebar would draw (yawble.ready, then yawble.nav
// on load and on every change) and follows the parent's yawble.navigate through the sidebar's own handlers below, and its
// yawble.theme through theme.ts, which keeps nothing in embed mode (pageThemeEnv).
// shallowRef: resolve compares the request itself (a ref would hold a proxy of it, never === r, and the dialog stayed open).
const embedRename = shallowRef<{ title: string; resolve: (t: string | null) => void } | null>(null);
function askEmbedTitle(current: string): Promise<string | null> {
  embedRename.value?.resolve(null);
  return new Promise((resolve) => {
    const r = { title: current, resolve: (t: string | null) => { if (embedRename.value === r) embedRename.value = null; resolve(t); } };
    embedRename.value = r;
  });
}
const embedState = (): EmbedNavState => ({
  view: view.value, conversations: conversations.value, teams: teams.value, conversationId: conversationId.value, teamId: selected.value,
  slots: overview.value?.slots ?? null, readOnly: readOnly.value, removing: removingChat.value, openWorkflows: openWorkflows.value,
});
if (embed) {
  let bridge: { dispose(): void } | null = null;
  onMounted(() => {
    bridge = startEmbedBridge({
      state: embedState,
      handlers: {
        newChat, go, openTeam: (id) => openTeam(id), openConversation, rename, remove,
        logs: () => go('logs'),
        newTeam: () => { dialog.value = 'newTeam'; },
      },
      dialogs: { askTitle: askEmbedTitle },
      applyTheme: (t) => theme.setMode(t),
    });
  });
  onBeforeUnmount(() => { bridge?.dispose(); embedRename.value?.resolve(null); });
}
watch([view, prefTab], ([v, t], [vBefore, tBefore]) => { if (vBefore === 'settings' && tBefore === 'agents' && (v !== 'settings' || t !== 'agents')) void loadAgents(); });
</script>

<template>
  <div class="app" :class="{ collapsed: collapsed && !phone && !embed, 'own-bar': ownBar, mac, phone, embed, 'drawer-open': phone && drawerOpen && !embed }" :data-embed="embed ? '1' : undefined">
    <div v-if="phone && drawerOpen && !embed" class="scrim" data-test="drawer-scrim" @click="drawerOpen = false" />
    <!-- Embedded in the cloud page: no sidebar at all, wide or phone; the cloud draws it from yawble.nav. -->
    <Sidebar v-if="!embed" id="app-drawer":rail="collapsed && !phone" :drawer="phone" :open="drawerOpen" :menu="drawerMenu" @command="command":view="view" :conversations="conversations" :teams="teams" :conversation-id="conversationId"
      :team-id="selected" :slots="overview?.slots ?? null" :remote="!!api.remote" :loading="{ teams: !overview && !error, chats: !chatsLoaded }" :removing="removingChat"
      @new-chat="newChat" @open-conversation="openConversation" @open-team="openTeam" @go="go"
      @rename="rename" @remove="remove" @new-team="drawerOpen = false; dialog = 'newTeam'" @collapse="phone ? (drawerOpen = false) : (collapsed = true)" @expand="collapsed = false"
      @logs="drawerOpen = false; go('logs')" :open-team-name="openTeamName" :open-workflows="openWorkflows" @team-documents="openTeamDialog('documents')" @team-workflows="openTeamDialog('workflows')" />

    <main class="main">
      <header class="bar">
        <button v-if="phone && !embed" class="ghost icon-btn menu-btn" data-test="drawer-toggle" :aria-label="drawerOpen ? 'Close the menu' : 'Open the menu'" title="Menu"
          :aria-expanded="drawerOpen" aria-controls="app-drawer" @click="drawerOpen = !drawerOpen">
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16" /></svg>
        </button>
        <MenuBar v-if="!menuInDrawer" :menus="menus" @command="command" />
        <span v-if="phone" class="title" data-test="phone-title">{{ title }}</span>
        <span class="grow" />
        <button v-if="view === 'teams' && team" class="pill" data-test="ask-concierge" :aria-label="phone ? 'Ask the Concierge' : undefined" @click="askAbout(team.id)"><Icon name="spark" :size="14" /><span class="pill-text">Ask the Concierge</span></button>
        <button v-if="!api.remote" class="ghost cloud-ind" :class="linkState" data-test="cloud-link-indicator" :title="linkTitle"
          :aria-label="linkTitle" @click="dialog = 'link'">
          <Icon name="cloud" :size="18" :filled="linkState === 'connected'" />
          <span class="cdot" />
          <span v-if="link?.state === 'connected' && link.viewers.length" class="cnum" data-test="cloud-link-viewers">{{ link.viewers.length }}</span>
        </button>
        <!-- Embedded: the cloud page's theme is shown (yawble.theme), so there is no switch here; the gear stays. -->
        <ThemeToggle v-if="!embed" />
        <!-- Settings is this gear at the top right, just right of the theme switch (desktop window, remote view, phone). -->
        <button class="ghost gear" :class="{ on: view === 'settings' }" type="button" data-testid="settings-gear" data-test="settings-gear" title="Settings" aria-label="Settings"
          :aria-current="view === 'settings' ? 'page' : undefined" @click="go('settings')"><Icon name="settings" :size="18" /></button>
      </header>
      <!-- The banners above every view, in one scrolling box no taller than half the window (40% on a phone), so the view
           below keeps its share and the Concierge's composer stays on screen however many banners there are. -->
      <div class="banners" data-test="banners">
      <p v-if="error" class="error banner" data-test="error">Core unavailable: {{ error }}</p>
      <p v-if="hashNote" class="muted banner" data-test="hash-note">{{ hashNote }}
        <button data-test="hash-note-dismiss" @click="hashNote = null">Dismiss</button></p>
      <HostChecksBanner :api="api" />
      <GitBanner :api="api" />
      <UpdateBanner ref="updateBanner" :api="api" />
      <CompanionAsks v-if="!api.remote" :api="api" :poll-ms="pollMs" />
      <!-- Hosts agents were refused, waiting for the person (in a remote view too); the Board shows them in its Needs You lane instead. -->
      <NetworkAsks v-if="view !== 'board'" banner :api="api" :poll-ms="pollMs" />
      <!-- Developer tools agents asked for (request_tool): Install in the window only, Not now anywhere; on the Board, its Needs You lane. -->
      <ToolAsks v-if="view !== 'board'" banner :api="api" :poll-ms="pollMs" />
      <!-- Folders outside a clone agents asked for: Approve / Deny, in a remote view too; on the Board, its Needs You lane. -->
      <FolderAsks v-if="view !== 'board'" banner :api="api" :poll-ms="pollMs" />
      </div>

      <div class="content">
      <div class="place">
        <ConciergeView v-if="view === 'concierge'" :conversation-id="conversationId" :teams="teams" :agents="agents" :agents-loaded="agentsLoaded"
          :default-cli="defaultCli" :focus-team-id="focusTeamId" :api="api" :draft="conciergeDraft" @drafted="conciergeDraft = null"
          @opened="conversationId = $event; loadConversations()" @changed="loadConversations" />

        <section v-else-if="view === 'teams'" class="teams-place">
          <TeamView v-if="team" :key="team.id" ref="teamView" :team="team" :api="api" :poll-ms="pollMs" @settings="settingsTab = 'general'; settingsFor = $event"
@changed="refresh" @deleted="selected = null; refresh()"
            @discuss="discuss(team.id, $event)" />
          <TeamsList v-else-if="overview" class="team-grid-wrap" :teams="teams" :archived="archivedTeams" :api="api" :poll-ms="pollMs" :refresh="refresh"
            @open="openTeam" @settings="manageTeam = $event" @new-team="dialog = 'newTeam'" />
          <p v-else-if="error" class="error pad" data-test="teams-error">The teams could not be loaded: {{ error }}</p>
          <div v-else class="pad" data-test="loading"><LoadingNote text="Loading teams (starting the core)…" /></div>
        </section>

        <DocumentsView v-else-if="view === 'documents'" :teams="teams" :archived="archivedTeams" :api="api" :loading="!overview && !error" />
        <LogsDialog v-else-if="view === 'logs'" page :api="api" />
        <ScriptsDialog v-else-if="view === 'scripts'" page :api="api" :teams="teams" />
        <MonitorView v-else-if="view === 'monitor'" :api="api" @open-team="openFromMonitor" @open-workflow="openWorkflow" @open-terminal="openMemberTerminal"
          @open-needs-you="focusTeamId = null; go('board')" />
        <BoardView v-else-if="view === 'board'" :api="api" :poll-ms="pollMs" :team-id="focusTeamId" @open-team="openTeam" @backlog="go('backlog')" @connections="openConnections" />
        <BacklogView v-else-if="view === 'backlog'" :teams="teams" :teams-loading="!overview && !error" :api="api" :poll-ms="pollMs" :focus="backlogRequest" @open-team="openTeam"
          @board="focusTeamId = $event; go('board')" />
        <WorkflowsView v-else-if="view === 'workflows'" :teams="teams" :api="api" :poll-ms="pollMs" :request="workflowsRequest" @open-team="openTeam"
          @board="focusTeamId = $event; go('board')" @backlog="go('backlog')" />
        <SettingsView v-else-if="view === 'settings'" :key="settingsKey" :landing="settingsLanding" :api="api":tab="prefTab" :teams="teams" :poll-ms="pollMs" :integrations-tab="integrationsTab" @tab="prefTab = $event; integrationsTab = null" @connections="openConnections" @changed="changed" @link="link = $event as LinkStatus" />

      </div>
      <TerminalPane v-if="terminalPane.open" :api="api" :poll-ms="pollMs" />
      </div>
    </main>

    <NewTeamDialog v-if="dialog === 'newTeam'" :api="api" @close="dialog = null" @created="created" />
    <WorkflowsDialog v-if="monitorWorkflows && view === 'monitor'" :key="`${monitorWorkflows.team.id}:${monitorWorkflows.id ?? 0}`" :api="api"
      :team="monitorWorkflows.team" :poll-ms="pollMs" :focus="monitorWorkflows.id" @close="monitorWorkflows = null" />
    <TeamManageDialog v-if="manageTeam" :api="api" :team="manageTeam" @close="manageTeam = null" @changed="refresh"
      @deleted="manageTeam = null; refresh()" />
    <LinkDialog v-if="dialog === 'link'" :api="api" @close="dialog = null" @changed="link = $event" />
    <MemberSettingsDialog v-if="settingsFor !== null" :key="`${settingsFor}-${settingsTab}`" :member-id="settingsFor" :initial-tab="settingsTab" :api="api" :poll-ms="pollMs" @close="settingsFor = null" @changed="changed" />
    <TimelineDialog :api="api" />
    <!-- Any commit ID clicked anywhere opens here, over whatever dialog showed it. -->
    <CommitDialog :api="api" />
    <CommitChooser />
    <ConfirmDialog />
    <EmbedRenameDialog v-if="embedRename" :title="embedRename.title" @done="embedRename.resolve($event)" />
  </div>
</template>

<style scoped>
/* dvh after vh: the height the browser really shows. iOS Safari's 100vh is the height with its toolbars hidden,
   so a desktop-width remote view (an iPad, a phone in landscape) ran past the bottom of the screen, worst after a rotation.
   In Electron dvh and vh are the same. minmax(0, 1fr): wide content never makes the page column wider than the window,
   nor the sidebar's content the row taller than it (a phone turned to landscape: 380 px; the sidebar then scrolls). */
.app { display: grid; grid-template-columns: 268px minmax(0, 1fr); grid-template-rows: minmax(0, 1fr); height: 100vh; height: 100dvh; background: var(--surface); }
.app.collapsed { grid-template-columns: 56px minmax(0, 1fr); }
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
.cloud-ind.connected { color: var(--dot-ok); } /* connected: the cloud itself is filled green, no dot */
.cdot { position: absolute; left: 21px; top: 17px; width: 8px; height: 8px; border-radius: 50%; border: 2px solid var(--surface); display: none; }
.cloud-ind.connecting .cdot, .cloud-ind.pairing .cdot { display: block; background: var(--dot-wait); }
.cloud-ind.offline .cdot { display: block; background: var(--dot-bad); }
.cnum { font-size: 12px; font-weight: 600; }
/* The Settings gear: as the theme switch beside it (ThemeToggle.vue). */
.gear { display: inline-flex; align-items: center; padding: 6px 8px; border-radius: 8px; color: var(--muted); }
.gear:hover { background: var(--hover); color: var(--ink); }
.gear.on { background: var(--selected); color: var(--ink); }
.gear :deep(svg) { display: block; flex: none; }
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
/* ---- a phone (below 760 px; phone.ts): one column, a slim top bar with the menu button, the sidebar a drawer over the
   page (Sidebar.vue) with a scrim behind it. dvh: the height a phone browser really shows, its own bars left out. */
.app.phone { grid-template-columns: minmax(0, 1fr); height: 100dvh; }
/* Embedded in the cloud page: no sidebar column, the screens take the full width. */
.app.embed { grid-template-columns: minmax(0, 1fr); }
.phone .bar { gap: 2px; min-height: 52px;
  padding: calc(4px + env(safe-area-inset-top, 0px)) calc(6px + env(safe-area-inset-right, 0px)) 4px calc(4px + env(safe-area-inset-left, 0px)); }
.own-bar.phone .bar { height: auto; }
.menu-btn { width: 44px; height: 44px; flex: none; color: var(--ink); }
.phone .title { flex: 0 1 auto; min-width: 0; padding-left: 2px; }
.phone .pill { min-width: 44px; justify-content: center; }
.phone .pill .pill-text { display: none; }
.phone .gear { min-width: 44px; min-height: 44px; justify-content: center; }
.phone .banner { padding: 6px 12px; }
/* All the banners together: at most half the window (40% of a phone's height), scrolling; the view (.content) gets the rest. */
.banners { display: block; flex: none; max-height: 50vh; max-height: 50dvh; overflow-y: auto; overscroll-behavior: contain; }
.phone .banners { max-height: 40dvh; }
.phone .teams-place { padding: 4px 12px 16px; }
.phone .pad { padding: 16px; }
.scrim { position: fixed; inset: 0; background: var(--backdrop); z-index: 29; }
</style>
