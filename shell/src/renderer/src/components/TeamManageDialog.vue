<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import type { AgentCard, ApiRow, IntegrationInfo, McpServerInfo, YawbleApi, TeamQuiet, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import IntegrationDialog from './IntegrationDialog.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

/**
 * A team's housekeeping: pause or resume it, its repositories, its environment variables, archive it, delete it. Archive and delete need
 * a quiet team (nothing running, nothing waiting) and say what is still working when it is not.
 */
const props = defineProps<{ team: TeamSummary; api?: YawbleApi }>();
const emit = defineEmits<{ close: []; changed: []; deleted: [] }>();
const api = resolveApi(props.api);

type Tab = 'general' | 'state' | 'repos' | 'env' | 'integrations' | 'reset' | 'delete';
const tab = ref<Tab>('general');
const error = ref<string | null>(null);
const note = ref<string | null>(null);
// One action at a time (every action button is held while one runs); the clicked one shows the spinner: work.is('act', method).
const work = useBusy();
const busy = computed(() => work.any());
const doing = (method: string) => work.is('act', method);
// Each tab's data has had its first answer (an error is shown above and ends the loading state too).
const ready = reactive({ quiet: false, push: false, sync: false, repos: false, approvals: false, env: false, ints: false, members: false });

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

// ---- repositories: the main one (every member's working directory) and the others, writable or read-only
interface ExtraRepo { name: string; path: string; writable: boolean; baseBranch: string; source: string | null }
const repos = ref<{ main: { path: string; baseBranch: string; managed: boolean; origin: string | null; name: string | null } | null; extras: ExtraRepo[] } | null>(null);
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

onMounted(() => { void checkQuiet(); void loadEnv(); void loadMembers(); void loadPush(); void loadSync(); void loadRepos(); void loadApprovals(); void loadIntegrations(); });
</script>

<template>
  <Modal :title="`Settings · ${team.name}`" wide @close="emit('close')">
    <nav class="tabs">
      <button :class="{ on: tab === 'general' }" data-test="manage-tab-general" @click="tab = 'general'">General</button>
      <button :class="{ on: tab === 'state' }" data-test="manage-tab-state" @click="tab = 'state'">Pause and archive</button>
      <button :class="{ on: tab === 'repos' }" data-test="manage-tab-repos" @click="tab = 'repos'">Repositories</button>
      <button :class="{ on: tab === 'env' }" data-test="manage-tab-env" @click="tab = 'env'">Environment</button>
      <button :class="{ on: tab === 'integrations' }" data-test="manage-tab-integrations" @click="tab = 'integrations'">Integrations</button>
      <button :class="{ on: tab === 'reset' }" data-test="manage-tab-reset" @click="tab = 'reset'">Reset</button>
      <button :class="{ on: tab === 'delete' }" data-test="manage-tab-delete" @click="tab = 'delete'">Delete</button>
    </nav>
    <p v-if="error" class="error" data-test="manage-error">{{ error }}</p>

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

    <section v-else class="pane">
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
.note { background: #fffaf0; border: 1px solid #e9d29a; border-radius: 6px; padding: 6px 10px; margin: 0; }
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
label.check input { width: auto; padding: 0; }
.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
.danger { background: var(--bad); color: #fff; border-color: var(--bad); }
.danger:disabled { opacity: .5; }
.link { border: 0; background: transparent; color: var(--accent); }
.icon { border: 0; background: transparent; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.warn { color: var(--warn); margin: 0; }
.ok { color: var(--ok); font-size: 12.5px; }
.error { color: var(--bad); }
</style>
