<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue';
import type { AgentCard, ApiRow, GitProvider, IntegrationInfo, McpServerInfo, RepoProviderInfo, YawbleApi, TeamQuiet, TeamSummary } from '../../../shared/api';
import { TEAM_WORKFLOW_EXPIRY, TEAMS_SET_REPO, WorkflowExpiryDefaultDays, TEAM_RETROS, TEAMS_LESSONS, PERSON_WRITER } from '../../../shared/api';
import { TEAM_LOCALHOST, TEAM_LOCALHOST_LABEL, TEAM_LOCALHOST_WARNING } from '../../../shared/api';
import type { HireRow, RoleRow, TeamHireRow, TeamLessons } from '../../../shared/api';
import HireListEditor from './HireListEditor.vue';
import { useAgentModels } from '../agentModels';
import { useAgentOptions } from '../agentOptions';
import { openSkill } from '../skillLink';
import RepoProviderFields from './RepoProviderFields.vue';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import IntegrationDialog from './IntegrationDialog.vue';
import TeamNetworkAllows from './TeamNetworkAllows.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import { usePhone } from '../phone';
import { vChange } from '../remoteAccess';

/**
 * A team's housekeeping: pause or resume it, who its Manager may hire, its repositories, its environment variables, archive it, delete it. Archive and delete need
 * a quiet team (nothing running, nothing waiting) and say what is still working when it is not.
 */
const props = defineProps<{ team: TeamSummary; api?: YawbleApi }>();
const emit = defineEmits<{ close: []; changed: []; deleted: [] }>();
const api = resolveApi(props.api);

type Tab = 'general' | 'hiring' | 'state' | 'repos' | 'env' | 'network' | 'integrations' | 'reset' | 'delete';
const tab = ref<Tab>('general');
const tabs: { id: Tab; label: string }[] = [
  { id: 'general', label: 'General' },
  { id: 'hiring', label: 'Hiring' },
  { id: 'state', label: 'Pause and archive' },
  { id: 'repos', label: 'Repositories' },
  { id: 'env', label: 'Environment' },
  { id: 'network', label: 'Network' },
  { id: 'integrations', label: 'Integrations' },
  { id: 'reset', label: 'Reset' },
  { id: 'delete', label: 'Delete' },
];
// A phone (card 346), laid out like the app's Settings on a phone (SettingsView): the dialog opens at the list of its
// sections; a tap opens that section alone under a sticky Back + title; Back returns to the list. Desktop: the tab row.
const phone = usePhone();
const sectionOpen = ref(false);
function choose(id: Tab) { tab.value = id; sectionOpen.value = true; }
const showList = computed(() => !phone.value || !sectionOpen.value);
const showPane = computed(() => !phone.value || sectionOpen.value);
const tabLabel = computed(() => tabs.find((t) => t.id === tab.value)?.label ?? 'Settings');
// A section opened on a phone starts at its top (the sheet's body is the one scroller).
const root = ref<HTMLElement | null>(null);
watch([tab, sectionOpen], async () => {
  if (!phone.value) return;
  await nextTick();
  const body = root.value?.closest<HTMLElement>('.body');
  if (body) body.scrollTop = 0;
});
const error = ref<string | null>(null);
const note = ref<string | null>(null);
// One action at a time (every action button is held while one runs); the clicked one shows the spinner: work.is('act', method).
const work = useBusy();
const busy = computed(() => work.any());
const doing = (method: string) => work.is('act', method);
// Each tab's data has had its first answer (an error is shown above and ends the loading state too).
const ready = reactive({ localhost: false, expiry: false, quiet: false, push: false, sync: false, repos: false, approvals: false, env: false, ints: false, members: false, hiring: false });

function act(method: string, params: Record<string, unknown> = {}, done?: (r: any) => void) {
  return work.run('act', async () => {
    error.value = null;
    note.value = null;
    try {
      const r = await api.call(method, { teamId: props.team.id, ...params });
      done?.(r);
      emit('changed');
    } catch (e) { error.value = errorText(e); }
  }, method);
}

// ---- B147: who the Manager may hire (teams.hireList, teams.setHireList): rows of a role, its agent, model and how many
// at most, from the role catalog (roles.list). An empty list lets the Manager hire any agent. An older core: a note.
const agentOptions = useAgentOptions(api);
const models = useAgentModels(api);
const roles = ref<RoleRow[]>([]);
const hireRows = reactive<HireRow[]>([]);
const hireUsage = ref<Record<string, { used: number; holders: number[] }>>({});
const hireMissing = ref(false);
const hireSaved = ref(false);
function applyHire(list: TeamHireRow[]) {
  hireRows.splice(0, hireRows.length, ...list.map((r) => ({ role: r.role, agent: r.agent, model: r.model ?? null, max: r.max || 1 })));
  hireUsage.value = Object.fromEntries(list.map((r) => [r.role, { used: r.used ?? 0, holders: r.holders ?? [] }]));
}
async function loadHiring() {
  try { applyHire(await api.call<TeamHireRow[]>('teams.hireList', { teamId: props.team.id, team: props.team.id })); hireMissing.value = false; }
  catch { hireMissing.value = true; } finally { ready.hiring = true; }
  try { roles.value = await api.call<RoleRow[]>('roles.list'); } catch { roles.value = []; }
}
const saveHiring = () => act('teams.setHireList', { team: props.team.id, rows: hireRows.map((r) => ({ role: r.role, agent: r.agent, model: r.model ?? null, max: r.max })) }, () => {
  hireSaved.value = true;
  void loadHiring();
});

// ---- pause / archive
const quiet = ref<TeamQuiet | null>(null);
async function checkQuiet() {
  try { quiet.value = await api.call<TeamQuiet>('teams.quiet', { teamId: props.team.id }); } catch (e) { error.value = errorText(e); } finally { ready.quiet = true; }
}
const checkAgain = () => work.run('quiet', checkQuiet);

// ---- pushing to origin
const push = ref<{ on: boolean; origin: string | null } | null>(null);
async function loadPush() { try { push.value = await api.call('teams.push', { teamId: props.team.id }); } catch { push.value = null; } finally { ready.push = true; } }
const setPush = (on: boolean) => act('teams.push', { on }, (r) => { push.value = r; });

// ---- Bring the branches up to the base before new work (on by default; off leaves only the warning)
const sync = ref<{ on: boolean; note: string | null } | null>(null);
async function loadSync() { try { sync.value = await api.call('teams.syncToBase', { teamId: props.team.id }); } catch { sync.value = null; } finally { ready.sync = true; } }
const setSync = (on: boolean) => act('teams.syncToBase', { on }, (r) => { sync.value = r; });
const baseName = computed(() => repos.value?.main?.baseBranch ?? 'the base branch');

// ---- B119: "Allow localhost (macOS)" (off by default). An older core without it: the block is not shown. A Visitor sees it
// read-only (v-change); the core refuses a Visitor's set anyway.
const localhost = ref<{ on: boolean } | null>(null);
async function loadLocalhost() { try { localhost.value = await api.call<{ on: boolean }>(TEAM_LOCALHOST, { teamId: props.team.id }); } catch { localhost.value = null; } finally { ready.localhost = true; } }
const setLocalhost = (on: boolean) => act(TEAM_LOCALHOST, { on }, (r: { on: boolean }) => { localhost.value = r; });
// The warning's `code` spans (the text is kept word for word in TEAM_LOCALHOST_WARNING).
const localhostWarning = TEAM_LOCALHOST_WARNING.split('`').map((text, i) => ({ text, code: i % 2 === 1 }));

// ---- B085: expire idle workflows after N days (workflowExpiryDays, default 3). An older core without it: the field is not shown.
const expiry = ref<number | null>(null);
const expiryDraft = ref<number>(WorkflowExpiryDefaultDays);
const expirySaved = ref(false);
async function loadExpiry() {
  try {
    const r = await api.call<{ days: number }>(TEAM_WORKFLOW_EXPIRY, { teamId: props.team.id });
    expiry.value = r?.days ?? WorkflowExpiryDefaultDays;
    expiryDraft.value = expiry.value;
  } catch { expiry.value = null; } finally { ready.expiry = true; }
}
const expiryValid = computed(() => Number.isInteger(expiryDraft.value) && expiryDraft.value >= 1 && expiryDraft.value <= 365);
const saveExpiry = () => act(TEAM_WORKFLOW_EXPIRY, { days: expiryDraft.value }, (r: { days?: number }) => {
  expiry.value = r?.days ?? expiryDraft.value;
  expiryDraft.value = expiry.value;
  expirySaved.value = true;
});

// ---- B100: the team's lessons (its team_retros skill, kept by the Manager's retrospectives). An older core: not shown.
const lessons = ref<TeamLessons | null>(null);
async function loadLessons() { try { lessons.value = await api.call<TeamLessons>(TEAMS_LESSONS, { teamId: props.team.id }); } catch { lessons.value = null; } }
const day = (iso: string | null) => { if (!iso) return ''; const d = new Date(iso); return Number.isNaN(d.getTime()) ? iso : d.toLocaleDateString(); };
const lessonsText = computed(() => {
  const l = lessons.value;
  if (!l || l.version === 0 && l.skillId === null) return 'Lessons: none yet. The Manager keeps them after each completed workflow.';
  const by = l.writer === PERSON_WRITER ? 'by you' : l.workflow !== null ? `by the retro of workflow ${l.workflow}` : l.writer ? `by ${l.writer}` : '';
  return `Lessons: ${l.count}` + (l.updatedAt ? `, last updated ${day(l.updatedAt)}${by ? ' ' + by : ''}` : '');
});
function showLessons() { openSkill(props.team.id, TEAM_RETROS); emit('close'); }

// ---- repositories: the main one (every member's working directory) and the others, writable or read-only
interface ExtraRepo extends RepoProviderInfo { name: string; path: string; writable: boolean; baseBranch: string; source: string | null }
const repos = ref<{ main: ({ path: string; baseBranch: string; managed: boolean; origin: string | null; name: string | null } & RepoProviderInfo) | null; extras: ExtraRepo[] } | null>(null);
// B075: the provider fields show only when the core sends providerDetected (an older core does not).
const hasProvider = (r: RepoProviderInfo | null | undefined) => !!r && r.providerDetected !== undefined;
const saveProvider = (name: string | null, v: { provider: GitProvider | null; linkPattern: string | null }) =>
  act(TEAMS_SET_REPO, { ...(name ? { name } : {}), provider: v.provider, linkPattern: v.linkPattern }, () => {
    repoNote.value = `Saved the provider settings of ${name ?? repos.value?.main?.name ?? 'the main repository'}.`;
    void loadRepos();
  });
const newRepo = ref({ where: '', name: '', readOnly: false });
const repoNote = ref<string | null>(null);
async function loadRepos() { try { repos.value = await api.call('teams.repos', { teamId: props.team.id }); } catch (e) { error.value = errorText(e); } finally { ready.repos = true; } }
const isAddress = (s: string) => /^(https?|ssh|file):|^[\w.-]+@[\w.-]+:/i.test(s.trim());
const addRepo = () => {
  const where = newRepo.value.where.trim();
  repoNote.value = null;
  return act('teams.addRepo', {
    [isAddress(where) ? 'url' : 'path']: where,
    name: newRepo.value.name.trim() || undefined,
    writable: !newRepo.value.readOnly,
  }, (r: ExtraRepo) => {
    repoNote.value = `Added ${r.name}. Members get it from their next run.`;
    newRepo.value = { where: '', name: '', readOnly: false };
    void loadRepos();
  });
};
const removeRepo = (name: string) => act('teams.removeRepo', { name }, () => { repoNote.value = `Removed ${name}; its folder on this computer is kept.`; void loadRepos(); });

// ---- standing approvals ("Always approve for this team" on a question)
interface Approval { id: number; text: string; createdAt: string; createdBy: string | null }
const approvals = ref<Approval[]>([]);
async function loadApprovals() { try { approvals.value = await api.call<Approval[]>('teams.approvals', { teamId: props.team.id }); } catch { approvals.value = []; } finally { ready.approvals = true; } }
const removeApproval = (id: number) => act('teams.removeApproval', { id }, () => { void loadApprovals(); void loadIntegrations(); });

// ---- environment variables
interface Row { name: string; value: string }
const rows = ref<Row[]>([]);
const envSaved = ref(false);
async function loadEnv() {
  try {
    const env = await api.call<Record<string, string>>('teams.env', { teamId: props.team.id });
    rows.value = Object.entries(env).map(([name, value]) => ({ name, value }));
  } catch (e) { error.value = errorText(e); } finally { ready.env = true; }
}
const saveEnv = () => act('teams.setEnv', { entries: rows.value.filter((r) => r.name.trim()).map((r) => ({ name: r.name, value: r.value })) }, () => { envSaved.value = true; void loadEnv(); });

// ---- integrations offered to this team (CLIs and MCP servers), each opening the shared dialog scoped to the team
interface OfferedInt { kind: 'cli' | 'mcp' | 'api'; website?: boolean; name: string; label: string; counts: string | null }
const offeredInts = ref<OfferedInt[]>([]);
const editingInt = ref<OfferedInt | null>(null);
const intCounts = (c?: IntegrationInfo['counts'] | null) => {
  if (!c) return null;
  const n = c.allowed + c.ask + c.off + c.undecided;
  return n === 0 ? 'no actions listed yet' : `${n} actions: ${c.allowed} allowed, ${c.ask} ask first`;
};
async function loadIntegrations() {
  const mine = (x: { all: boolean; teams: number[] }) => x.all || x.teams.includes(props.team.id);
  try {
    const [l, m] = await Promise.all([
      api.call<{ integrations: IntegrationInfo[] }>('integrations.list'),
      api.call<McpServerInfo[]>('integrations.mcpList'),
    ]);
    offeredInts.value = [
      ...l.integrations.filter((i) => i.configured && mine(i)).map((i) => ({ kind: 'cli' as const, name: i.name, label: i.label || i.name, counts: intCounts(i.counts) })),
      ...m.filter(mine).map((s) => ({ kind: 'mcp' as const, name: s.name, label: s.name, counts: intCounts(s.counts) })),
    ];
  } catch (e) { error.value = errorText(e); ready.ints = true; return; }
  ready.ints = true;
  try {
    const a = await api.call<ApiRow[]>('integrations.apiList');
    offeredInts.value = [...offeredInts.value, ...a.filter(mine).map((x) => ({ kind: 'api' as const, website: x.kind === 'website', name: x.name, label: x.name, counts: intCounts(x.counts) }))];
  } catch { /* APIs are extra here: the CLIs and MCP servers above still show */ }
}
function intClosed() { editingInt.value = null; void loadIntegrations(); }

// ---- reset
const memberNames = ref<string[]>([]);
const resetPick = ref<string[]>([]);
const resetOpts = ref({ dropWaiting: true, clearHome: true, removeClone: false, clearDocuments: false });
const resetDone = ref<string | null>(null);
async function loadMembers() {
  try {
    const cards = await api.call<AgentCard[]>('teams.agents', { teamId: props.team.id });
    memberNames.value = cards.map((c) => c.member.name);
  } catch (e) { error.value = errorText(e); } finally { ready.members = true; }
}
const anyReset = computed(() => resetPick.value.length > 0 && Object.values(resetOpts.value).some((v) => v));
const doReset = () => act('teams.reset', { members: [...resetPick.value], ...resetOpts.value }, (r: { done: string[]; left: string[] }) => {
  resetDone.value = `Done: ${r.done.join('; ') || 'nothing to clear'}.` + (r.left.length ? ` Not removed: ${r.left.join('; ')}.` : '');
});

onMounted(() => { void models.loadProfiles(); void loadHiring(); void loadLessons(); void loadExpiry(); void checkQuiet(); void loadEnv(); void loadMembers(); void loadPush(); void loadSync(); void loadLocalhost(); void loadRepos(); void loadApprovals(); void loadIntegrations(); });
</script>

<template>
  <Modal :title="`Settings · ${team.name}`" wide @close="emit('close')">
    <div ref="root" class="manage" :class="{ phone }" :data-phone-screen="phone ? (sectionOpen ? 'section' : 'list') : undefined">
    <div v-if="phone && sectionOpen" class="section-head">
      <button class="back" data-test="manage-back" aria-label="Back to the team's Settings" @click="sectionOpen = false"><span class="back-icon" aria-hidden="true">‹</span>Settings</button>
      <h3 class="section-title" data-test="manage-section-title">{{ tabLabel }}</h3>
    </div>
    <nav v-if="showList" class="tabs" :class="{ list: phone }" data-test="manage-tabs" role="tablist" :aria-orientation="phone ? 'vertical' : undefined">
      <button v-for="t in tabs" :key="t.id" role="tab" :aria-selected="tab === t.id" :class="{ on: tab === t.id && !phone }" :data-test="`manage-tab-${t.id}`"
        @click="choose(t.id)">{{ t.label }}<span v-if="phone" class="go-icon" aria-hidden="true">›</span></button>
    </nav>
    <p v-if="error" class="error" data-test="manage-error">{{ error }}</p>

    <template v-if="showPane">
    <section v-if="tab === 'general'" class="pane" data-test="team-general">
      <div class="block">
        <h4>Team</h4>
        <p><b>{{ team.name }}</b> <span class="muted small">· {{ team.paused ? 'paused' : 'running' }}{{ team.archived ? ' · archived' : '' }}</span></p>
      </div>
      <div class="block">
        <h4>Repository</h4>
        <LoadingNote v-if="!ready.repos" text="Loading the repository…" />
        <template v-else-if="repos?.main">
          <p class="mono small path" data-test="team-repo-path">{{ repos.main.path }}</p>
          <p class="muted small">Base branch <code>{{ repos.main.baseBranch }}</code><template v-if="repos.main.origin"> · cloned from {{ repos.main.origin }}</template></p>
        </template>
        <p v-else-if="team.repoPath" class="mono small path" data-test="team-repo-path">{{ team.repoPath }}</p>
        <p v-else class="muted small">Made at the team's first run.</p>
        <p class="muted small">Other repositories, and adding more, are on the <a href="#" @click.prevent="tab = 'repos'">Repositories</a> tab.</p>
      </div>
      <div v-if="lessons" class="block" data-test="team-lessons">
        <h4>What the team has learned</h4>
        <p class="small"><span data-test="team-lessons-line">{{ lessonsText }}</span>
          <template v-if="lessons.skillId !== null"> · <a href="#" data-test="team-lessons-link" @click.prevent="showLessons">Read or edit them in Settings &gt; Skills</a></template></p>
        <p class="muted small">Every run of the team gets its lessons (the team_retros skill) in its prompt, at once.</p>
      </div>
      <div v-if="!ready.expiry || expiry !== null" class="block" data-test="team-workflow-expiry">
        <h4>Idle workflows</h4>
        <LoadingNote v-if="!ready.expiry" text="Loading…" />
        <template v-else>
          <label class="inline">Expire idle workflows after
            <input v-model.number="expiryDraft" type="number" min="1" max="365" step="1" class="days" data-test="team-workflow-expiry-days" @input="expirySaved = false" />
            days</label>
          <p class="muted small">An open workflow with no message, run, hand-back or status change for this long is closed by the app as expired.
            One that waits on your answer is shown in Needs you the day before, with Extend. Nothing expires while a run, a wake or a trigger is pending.</p>
          <div class="buttons"><span v-if="!expiryValid" class="warn small">A whole number of days, 1 to 365.</span><span class="grow" />
            <span v-if="expirySaved" class="ok" data-test="team-workflow-expiry-saved">Saved.</span>
            <button class="primary" data-test="team-workflow-expiry-save" :disabled="busy || !expiryValid || expiryDraft === expiry" @click="saveExpiry"><BusyLabel :busy="doing(TEAM_WORKFLOW_EXPIRY)" text="Saving…">Save</BusyLabel></button></div>
        </template>
      </div>
    </section>

    <section v-else-if="tab === 'hiring'" class="pane" data-test="team-hiring">
      <p class="muted">Who the Manager may hire: each row is a role, the agent and model a member of it runs on, and how many members of it
        at most. The Manager can hire only these; a hire gets the role's job as its standing instructions.</p>
      <LoadingNote v-if="!ready.hiring" text="Loading the hire list…" />
      <p v-else-if="hireMissing" class="muted small" data-test="team-hiring-missing">This version of the app's core keeps no hire list: the Manager may hire any agent.</p>
      <template v-else>
        <HireListEditor :rows="hireRows" :roles="roles" :agents="agentOptions" :models="models" :usage="hireUsage" test-id="team-hire" @changed="hireSaved = false" />
        <div class="buttons"><span class="grow" />
          <span v-if="hireSaved" class="ok" data-test="team-hire-saved">Saved: the Manager hires from this list from now on.</span>
          <button v-change class="primary" data-test="team-hire-save" :disabled="busy" @click="saveHiring"><BusyLabel :busy="doing('teams.setHireList')" text="Saving…">Save</BusyLabel></button></div>
      </template>
    </section>

    <section v-else-if="tab === 'state'" class="pane">
      <div class="block">
        <h4>{{ team.paused ? 'Paused' : 'Running' }}</h4>
        <p class="muted">
          A paused team starts no new runs: what it was told waits in place and runs, oldest first, when you resume it.
          A run already going finishes. Its schedules skip while it is paused.
        </p>
        <button v-if="!team.paused" data-test="team-pause" :disabled="busy" @click="act('teams.pause')"><BusyLabel :busy="doing('teams.pause')" text="Pausing…">Pause the team</BusyLabel></button>
        <button v-else class="primary" data-test="team-resume" :disabled="busy || team.archived" @click="act('teams.resume')"><BusyLabel :busy="doing('teams.resume')" text="Resuming…">Resume the team</BusyLabel></button>
      </div>
      <div v-if="push" class="block">
        <h4>Pushing</h4>
        <label class="check"><input type="checkbox" :checked="push.on" data-test="team-push" :disabled="busy" @change="setPush(($event.target as HTMLInputElement).checked)" />
          Push the team's branches to its repository's origin after each run (never main, never forced)</label>
        <p class="muted small">Off: the work stays in the team's repository on this computer until you push it yourself or turn this back on.</p>
      </div>
      <div class="block" data-test="team-approvals">
        <h4>Approved once and for all</h4>
        <p class="muted small">What you approved with "Always approve for this team": the Manager goes ahead with these without asking, and reports what it did.</p>
        <LoadingNote v-if="!ready.approvals" text="Loading the approvals…" />
        <p v-else-if="!approvals.length" class="muted small">Nothing yet.</p>
        <div v-for="a in approvals" :key="a.id" class="approval">
          <span class="grow">{{ a.text }}</span>
          <button class="link" :data-test="`approval-remove-${a.id}`" :disabled="busy" @click="removeApproval(a.id)"><BusyLabel :busy="doing('teams.removeApproval')" text="Removing…">Remove</BusyLabel></button>
        </div>
      </div>
      <div class="block">
        <h4>Archive</h4>
        <p class="muted">
          Archiving puts the team away whole: its members, settings, repository and history stay. It does no work and takes no
          instructions, and it leaves the sidebar (it stays in the Teams list). Unarchiving brings it back paused.
        </p>
        <template v-if="!team.archived">
          <LoadingNote v-if="!ready.quiet" text="Checking whether the team is quiet…" />
          <p v-if="quiet && !quiet.quiet" class="warn" data-test="archive-not-quiet">Not quiet yet: {{ quiet.working.join('; ') }}. Wait for that work, or stop it.</p>
          <p v-if="quiet?.openWorkflows.length" class="muted small">{{ quiet.openWorkflows.length }} open workflow{{ quiet.openWorkflows.length === 1 ? '' : 's' }} stay open.</p>
          <button data-test="team-archive" :disabled="busy || !quiet?.quiet" @click="act('teams.archive', {}, () => emit('close'))"><BusyLabel :busy="doing('teams.archive')" text="Archiving…">Archive the team</BusyLabel></button>
          <button class="link" data-test="team-check-quiet" :disabled="work.is('quiet')" @click="checkAgain"><BusyLabel :busy="work.is('quiet')" text="Checking…">Check again</BusyLabel></button>
        </template>
        <button v-else data-test="team-unarchive" :disabled="busy" @click="act('teams.unarchive')"><BusyLabel :busy="doing('teams.unarchive')" text="Unarchiving…">Unarchive (comes back paused)</BusyLabel></button>
      </div>
    </section>

    <section v-else-if="tab === 'repos'" class="pane">
      <p class="muted">
        The team works in its main repository; it can have more. A writable one works like the main one: each member gets its
        own clone, and the team's branches come back and are pushed to its origin (never main, never forced). A read-only one is
        a reference the members can read, kept up to date with its base branch.
      </p>
      <div class="block" data-test="repo-main">
        <h4>{{ repos?.main?.name ?? 'Main' }} <span class="tag">main · every member's working directory</span></h4>
        <LoadingNote v-if="!ready.repos" text="Loading the repositories…" />
        <template v-else-if="repos?.main">
          <p class="mono small">{{ repos.main.path }} <span class="muted">({{ repos.main.baseBranch }})</span></p>
          <p v-if="repos.main.origin" class="muted small" data-test="repo-main-origin">Cloned from {{ repos.main.origin }}</p>
          <p v-else-if="repos.main.managed" class="muted small">A repository the app made for the team (no origin).</p>
          <RepoProviderFields v-if="hasProvider(repos.main)" :info="repos.main" test-id="repo-main" :busy="busy" :saving="doing(TEAMS_SET_REPO)"
            @save="saveProvider(null, $event)" />
        </template>
        <p v-else class="muted small">Made at the team's first run.</p>
      </div>
      <div v-if="sync" class="block">
        <h4>Keeping up with {{ baseName }}</h4>
        <label class="check"><input type="checkbox" :checked="sync.on" data-test="team-sync-to-base" :disabled="busy" @change="setSync(($event.target as HTMLInputElement).checked)" />
          Bring branches up to {{ baseName }} before new work</label>
        <p class="muted small">Before a workflow's first run the app merges {{ baseName }} into the team branch and the members' branches, in the team's
          repository (never a rebase, never forced; a merge that would conflict changes nothing and is reported). Off: you are only told when the
          team branch falls behind.</p>
      </div>
      <div v-for="r in repos?.extras ?? []" :key="r.name" class="block repo" :data-test="`repo-${r.name}`">
        <div class="grow">
          <h4>{{ r.name }} <span class="tag">{{ r.writable ? 'writable' : 'read-only' }}</span></h4>
          <p class="mono small">{{ r.path }} <span class="muted">({{ r.baseBranch }})</span></p>
          <p v-if="r.source" class="muted small">Cloned from {{ r.source }}</p>
          <RepoProviderFields v-if="hasProvider(r)" :info="r" :test-id="`repo-${r.name}`" :busy="busy" :saving="doing(TEAMS_SET_REPO)"
            @save="saveProvider(r.name, $event)" />
        </div>
        <button class="link" :data-test="`repo-remove-${r.name}`" :disabled="busy" @click="removeRepo(r.name)"><BusyLabel :busy="doing('teams.removeRepo')" text="Removing…">Remove</BusyLabel></button>
      </div>
      <div class="block">
        <h4>Add a repository</h4>
        <label>Its address (cloned with your own git) or its folder on this computer
          <input v-model="newRepo.where" class="mono wide" placeholder="https://git.example.com/owner/repo.git" data-test="repo-where" />
        </label>
        <label>Name members see it by (optional)<input v-model="newRepo.name" placeholder="from the repository's name" data-test="repo-name" /></label>
        <label class="check"><input v-model="newRepo.readOnly" type="checkbox" data-test="repo-readonly" /> Read-only reference (the team reads it, never changes it)</label>
        <div class="buttons"><span class="grow" /><button class="primary" data-test="repo-add" :disabled="busy || !newRepo.where.trim()" @click="addRepo"><BusyLabel :busy="doing('teams.addRepo')" text="Adding…">Add</BusyLabel></button></div>
      </div>
      <p v-if="repoNote" class="ok" data-test="repo-note">{{ repoNote }}</p>
    </section>

    <section v-else-if="tab === 'env'" class="pane">
      <p class="note">
        Coordination, not confinement: these are kept in plain text and every agent on the team can read them. Use test
        accounts and local addresses, never a production key.
      </p>
      <LoadingNote v-if="!ready.env" text="Loading the environment…" />
      <table v-else class="env" data-test="env-rows">
        <tr v-for="(r, i) in rows" :key="i">
          <td><input v-model="r.name" placeholder="NAME" class="mono" :data-test="`env-name-${i}`" @input="envSaved = false" /></td>
          <td class="grow"><input v-model="r.value" placeholder="value" class="mono" :data-test="`env-value-${i}`" @input="envSaved = false" /></td>
          <td><button class="icon" title="Remove" @click="rows.splice(i, 1); envSaved = false">✕</button></td>
        </tr>
      </table>
      <div class="buttons">
        <button data-test="env-add" @click="rows.push({ name: '', value: '' }); envSaved = false">+ Add a variable</button>
        <span class="grow" />
        <span v-if="envSaved" class="ok" data-test="env-saved">Saved: the next runs get them.</span>
        <button class="primary" data-test="env-save" :disabled="busy" @click="saveEnv"><BusyLabel :busy="doing('teams.setEnv')" text="Saving…">Save</BusyLabel></button>
      </div>
      <p class="muted small">The app's own variables (HOME, PATH, the sandbox's folders, sign-in and gateway settings) always win and cannot be set here.</p>
    </section>

    <section v-else-if="tab === 'network'" class="pane" data-test="team-network">
      <div class="block"><TeamNetworkAllows :team-id="team.id" :api="api" @changed="emit('changed')" /></div>
      <div v-if="!ready.localhost || localhost" class="block" data-test="team-localhost">
        <h4>Localhost</h4>
        <LoadingNote v-if="!ready.localhost" text="Loading…" />
        <template v-else-if="localhost">
          <label class="check"><input v-change type="checkbox" :checked="localhost.on" data-test="team-localhost-toggle" :disabled="busy"
            @change="setLocalhost(($event.target as HTMLInputElement).checked)" />{{ TEAM_LOCALHOST_LABEL }}</label>
          <p class="note small" data-test="team-localhost-warning"><template v-for="(s, i) in localhostWarning" :key="i"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></p>
          <p class="muted small">Off by default. It applies to this team's runs on macOS from their next run; Windows and Linux runs already have a loopback of their own.</p>
        </template>
      </div>
      <p class="muted small">Each member's own policy (its override and Loosen…) is in the member's settings, on its Policy tab, next to this list.</p>
    </section>

    <section v-else-if="tab === 'integrations'" class="pane" data-test="team-integrations">
      <LoadingNote v-if="!ready.ints" text="Loading integrations…" />
      <p v-else-if="!offeredInts.length" class="muted" data-test="team-int-empty">No integrations are offered to this team. Offer one in Settings → Integrations.</p>
      <div v-for="i in offeredInts" :key="`${i.kind}-${i.name}`" class="block repo" :data-test="`team-int-${i.name}`">
        <div class="grow">
          <h4>{{ i.label }} <span class="tag">{{ i.kind === 'cli' ? 'CLI' : i.kind === 'api' ? (i.website ? 'Website' : 'API') : 'MCP server' }}</span></h4>
          <p v-if="i.counts" class="muted small" data-test="counts">{{ i.counts }}</p>
        </div>
        <button :data-test="`team-int-edit-${i.name}`" @click="editingInt = i">Edit</button>
      </div>
      <IntegrationDialog v-if="editingInt" :api="api" :kind="editingInt.kind" :website="!!editingInt.website" :name="editingInt.name" :teams="[team]" :initial-scope="team.id" @close="intClosed" @saved="loadIntegrations" />
    </section>

    <section v-else-if="tab === 'reset'" class="pane">
      <p class="muted">
        The members stay as they are; what they carry is cleared. Nobody picked may be running. Committed work is safe: it is in the
        team's repository.
      </p>
      <div class="block">
        <h4>Members</h4>
        <LoadingNote v-if="!ready.members" text="Loading the members…" />
        <label v-for="n in memberNames" :key="n" class="check"><input v-model="resetPick" type="checkbox" :value="n" :data-test="`reset-member-${n}`" /> {{ n }}</label>
      </div>
      <div class="block">
        <h4>What to clear</h4>
        <label class="check"><input v-model="resetOpts.dropWaiting" type="checkbox" data-test="reset-waiting" /> Their waiting instructions</label>
        <label class="check"><input v-model="resetOpts.clearHome" type="checkbox" data-test="reset-home" /> Their home folder (the agent CLI's own history and memory)</label>
        <label class="check"><input v-model="resetOpts.removeClone" type="checkbox" data-test="reset-clone" /> Their working copy (made again from their branch at the next run; uncommitted changes are lost)</label>
        <label class="check"><input v-model="resetOpts.clearDocuments" type="checkbox" data-test="reset-docs" /> The team's documents (the whole team's)</label>
      </div>
      <div class="buttons"><span class="grow" /><button class="danger" data-test="team-reset" :disabled="busy || !anyReset" @click="doReset"><BusyLabel :busy="doing('teams.reset')" text="Resetting…">Reset</BusyLabel></button></div>
      <p v-if="resetDone" class="ok" data-test="reset-done">{{ resetDone }}</p>
    </section>

    <section v-else class="pane" data-test="team-delete-pane">
      <p>
        Deleting <b>{{ team.name }}</b> removes its members, their settings, schedules and Board cards, and its folder in the app's
        data: the members' working copies and, if the app made it, the team's repository with any work not pushed anywhere.
        A repository folder you picked yourself is left alone. The record of what was said stays in the app's history.
      </p>
      <p v-if="quiet && !quiet.quiet" class="warn">Not quiet yet: {{ quiet.working.join('; ') }}.</p>
      <div class="buttons">
        <span class="grow" />
        <button class="danger" data-test="team-delete" :disabled="busy"
          @click="act('teams.delete', { confirm: team.name }, (r) => { if (r?.left?.length) note = 'Some files could not be removed: ' + r.left.join('; '); emit('deleted'); })">
          <BusyLabel :busy="doing('teams.delete')" text="Deleting…">Delete {{ team.name }}</BusyLabel>
        </button>
      </div>
      <p v-if="note" class="warn">{{ note }}</p>
    </section>
    </template>
    </div>
  </Modal>
</template>

<style scoped>
.tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--line); margin-bottom: 12px; }
.tabs button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 6px 10px; font-weight: 600; color: var(--ink-soft); }
.tabs button.on { color: var(--accent); border-bottom-color: var(--accent); }
.pane { display: flex; flex-direction: column; gap: 10px; font-size: 13.5px; }
.block { border: 1px solid var(--line-soft); border-radius: 8px; padding: 10px 12px; }
.block h4 { margin: 0 0 4px; font-size: 14px; }
.block p { margin: 0 0 8px; }
.check { display: flex; gap: 8px; align-items: center; padding: 2px 0; }
.note { background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 6px; padding: 6px 10px; margin: 0; }
.env { width: 100%; border-collapse: collapse; }
.env td { padding: 2px 4px 2px 0; }
.env td:first-child { width: 230px; min-width: 160px; }
.env td.grow { width: auto; }
.env input { width: 100%; font-size: 13px; padding: 4px 6px; border: 1px solid var(--line); border-radius: 6px; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.repo { display: flex; gap: 10px; align-items: flex-start; }
.path { user-select: all; overflow-wrap: anywhere; }
.approval { display: flex; gap: 8px; align-items: baseline; padding: 3px 0; border-top: 1px solid var(--line-soft); }
.tag { font-size: 11px; font-weight: 600; color: var(--ink-soft); border: 1px solid var(--line); border-radius: 10px; padding: 0 6px; margin-left: 4px; }
label input.wide { max-width: none; }
.check input { max-width: none; }
.buttons { display: flex; gap: 8px; align-items: center; }
.grow { flex: 1; }
label { display: flex; flex-direction: column; gap: 4px; }
label input { font: inherit; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; max-width: 320px; }
label.check { flex-direction: row; align-items: center; }
label.inline { flex-direction: row; align-items: center; gap: 6px; }
input.days { width: 72px; }
label.check input { width: auto; padding: 0; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.danger { background: var(--bad); color: var(--on-accent); border-color: var(--bad); }
.danger:disabled { opacity: .5; }
.link { border: 0; background: transparent; color: var(--accent); }
.icon { border: 0; background: transparent; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.warn { color: var(--warn); margin: 0; }
.ok { color: var(--ok); font-size: 12.5px; }
.error { color: var(--bad); }
/* ---- a phone (card 346), like the app's Settings on a phone (SettingsView): the list of sections as full-width 52 px
   rows with a chevron, or one section under a slim sticky Back + title. Nothing is wider than the sheet. */
.phone .tabs.list { flex-direction: column; flex-wrap: nowrap; gap: 0; border-bottom: 0; border-top: 1px solid var(--line-soft); margin-bottom: 12px; }
.phone .tabs.list button { display: flex; align-items: center; justify-content: space-between; width: 100%; min-height: 52px; padding: 0 6px 0 4px;
  border-bottom: 1px solid var(--line-soft); text-align: left; font-weight: 500; color: var(--ink); font-size: 15px; white-space: nowrap; }
.go-icon { color: var(--muted); font-size: 20px; line-height: 1; }
/* Sticks to the top of the sheet's body (its only scroller); the negative margin covers the body's top padding, so
   nothing shows above the head as the section scrolls under it. */
.section-head { position: sticky; top: -12px; z-index: 2; display: flex; align-items: center; gap: 6px; min-height: 44px;
  margin: -12px 0 10px; padding-top: 12px; background: var(--surface); border-bottom: 1px solid var(--line-soft); }
.back { flex: none; display: inline-flex; align-items: center; gap: 2px; border: 0; background: transparent; color: var(--accent); padding: 0 8px 0 0; min-height: 44px; font-size: 15px; }
.back-icon { font-size: 22px; line-height: 1; margin-right: 2px; }
.section-title { margin: 0; font-size: 17px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.phone .pane { min-width: 0; }
.phone .block { min-width: 0; }
.phone .repo .grow { min-width: 0; }
.phone .mono, .phone code { overflow-wrap: anywhere; }
.phone .env { table-layout: fixed; }
.phone .env td:first-child { width: 38%; min-width: 0; }
.phone .env td:last-child { width: 36px; }
.phone .env td.grow { width: auto; }
.phone .env input { min-width: 0; box-sizing: border-box; }
.phone label input:not([type=checkbox]) { max-width: none; width: 100%; min-width: 0; box-sizing: border-box; }
.phone .buttons { flex-wrap: wrap; }
.phone .check { align-items: flex-start; padding: 6px 0; }
.phone .check input { margin-top: 3px; }
</style>
