<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type {
  BranchSyncResult, GitDeletePlan, GitDeleteResult, YawbleApi, TeamGitBranch, TeamGitOldBranch, TeamGitResult, TeamGitStatus, TeamSummary,
} from '../../../shared/api';
import { errorCode, errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// The Git dialog: for the team's main repository or one of its writable extra repositories (the tabs on
// top), see the team's branches against the base branch, fetch origin,
// bring the base current, merge the team branch to it, or ask the team to bring its branch current. The person's
// buttons only. Nothing is ever forced, rebased or stashed; a merge that would conflict changes nothing.
const props = defineProps<{ api?: YawbleApi; team: TeamSummary }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const status = ref<TeamGitStatus | null>(null);
const error = ref<string | null>(null);
const result = ref<TeamGitResult | null>(null);
const busy = ref<string | null>(null);
const confirming = ref(false);

// Which repository: the main one ('') or a writable extra by name. Read-only references have no team branch.
const repo = ref('');
const choices = ref<{ name: string; label: string }[]>([]);
async function loadChoices() {
  try {
    const r = await api.call<{ main: { name: string | null } | null; extras: { name: string; writable: boolean }[] }>('teams.repos', { teamId: props.team.id });
    choices.value = [{ name: '', label: r.main?.name ?? 'main' }, ...r.extras.filter((e) => e.writable).map((e) => ({ name: e.name, label: e.name }))];
  } catch { choices.value = []; }
}
const which = () => (repo.value ? { teamId: props.team.id, repo: repo.value } : { teamId: props.team.id });
function pick(name: string) {
  if (repo.value === name) return;
  repo.value = name;
  status.value = null;
  result.value = null;
  confirming.value = false;
  deleting.value = null;
  void load();
}

const short = (sha: string | null | undefined) => (sha ? sha.slice(0, 10) : '—');
const mergeText = computed(() => {
  const s = status.value;
  if (!s) return '';
  switch (s.merge) {
    case 'none': return `There is no ${s.teamBranch} yet: the Manager makes it when it merges accepted work.`;
    case 'up-to-date': return `${s.baseBranch} already has everything on ${s.teamBranch}.`;
    case 'fast-forward': return `${s.teamBranch} is ${s.teamAheadOfBase} commit(s) ahead: ${s.baseBranch} will fast-forward to it.`;
    case 'merge': return `${s.teamBranch} is ${s.teamAheadOfBase} ahead and ${s.teamBehindBase} behind: a merge commit will join them (no rebase).`;
    case 'conflict': return `${s.teamBranch} does not merge cleanly into ${s.baseBranch}.`;
    default: return s.merge;
  }
});
const canMerge = computed(() => status.value?.merge === 'fast-forward' || status.value?.merge === 'merge');

async function load() {
  error.value = null;
  try {
    status.value = await api.call<TeamGitStatus>('git.status', which());
  } catch (e) {
    error.value = errorText(e);
  }
}

// Refresh: the same read as opening, shown busy on its button (the content stays while it reads).
async function refresh() {
  if (busy.value) return;
  busy.value = 'refresh';
  try { await load(); } finally { busy.value = null; }
}

async function act(method: 'git.fetch' | 'git.bringCurrent' | 'git.merge', label: string) {
  if (busy.value) return;
  busy.value = label;
  error.value = null;
  try {
    const r = await api.call<TeamGitResult>(method, which());
    result.value = r;
    if (r.status) status.value = r.status;
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = null;
    // The merge confirmation stays up (its Merge button shows Merging…) until the core answers.
    confirming.value = false;
  }
}

// The team branch's own distance from the base, and Sync now (the same sync as before new work; a conflict changes nothing).
const behindText = computed(() => {
  const s = status.value;
  if (!s || !s.teamSha || s.teamBehindBase <= 0) return '';
  return `${s.teamBranch} is ${s.teamBehindBase} commit${s.teamBehindBase === 1 ? '' : 's'} behind ${s.baseBranch}`;
});
// Sync now acts on the team's main repository only (the extra repositories are not synced by the app).
const canSync = computed(() => !!behindText.value && !repo.value);

async function syncNow() {
  if (busy.value) return;
  busy.value = 'sync';
  error.value = null;
  try {
    const r = await api.call<BranchSyncResult>('git.sync', which());
    const moved = r.moved.length ? ` (${r.moved.join(', ')})` : '';
    result.value = {
      outcome: r.outcome, changed: r.outcome === 'synced', sha: r.baseSha, files: r.conflicts.length ? r.conflicts : null, status: null,
      message: r.outcome === 'conflict' ? (r.note ?? 'Could not sync automatically; nothing changed.')
        : r.outcome === 'synced' ? `Brought the branches up to ${r.baseBranch}${moved}.` : `Already up to ${r.baseBranch}.`,
    };
    await load();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = null;
  }
}

async function askTeam() {
  if (busy.value) return;
  busy.value = 'ask';
  error.value = null;
  try {
    const r = await api.call<{ seq: number; workflow: number }>('git.askTeam', which());
    result.value = { outcome: 'asked', message: `Told the Manager to bring ${status.value?.teamBranch} current (workflow ${r.workflow}).`, changed: false, sha: null, files: null, status: null };
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = null;
  }
}

// Delete a branch: a member branch or an old-style members/<member> one (and its origin copy). The core plans first
// (git.deleteBranches without confirm) and the person reads that plan; Delete sends the plan's token back. An unmerged
// branch is deleted only when the person ticks it, which re-plans with it in unmergedOk so the plan shown is the one
// confirmed. A stale answer (the branches changed since the plan) is shown and planned again.
interface DeleteTarget { branch: string; remote: boolean; unmerged: boolean; mergeUnknown?: boolean }
const deleting = ref<(DeleteTarget & { unmergedOk: boolean; plan: GitDeletePlan | null; note: string | null; error: string | null }) | null>(null);
const oldBranches = computed(() => status.value?.oldBranches ?? []);
const memberTarget = (b: TeamGitBranch): DeleteTarget => ({ branch: b.branch, remote: !!status.value?.origin, unmerged: !b.inTeamBranch && b.aheadOfBase > 0 });
const oldTarget = (o: TeamGitOldBranch): DeleteTarget => ({ branch: o.name, remote: o.remote, unmerged: !o.merged });
const oldWhere = (o: TeamGitOldBranch) => (o.local && o.remote ? 'here and on origin' : o.remote ? 'on origin' : 'here');

// A members/<slug> branch named like this team's slug blocks its members/<slug>/<member> branches (git cannot hold
// both). git.status lists one entry per ref (here, on origin); the dialog shows one per branch and repository. Whether
// its commits are merged is not known here, so the person may tick to delete it anyway; the core's plan says which.
interface BlockingRow { name: string; repo: string; local: boolean; remote: boolean }
const blockingBranches = computed<BlockingRow[]>(() => {
  const rows: BlockingRow[] = [];
  for (const b of status.value?.blockingBranches ?? []) {
    let row = rows.find((r) => r.name === b.name && r.repo === b.repo);
    if (!row) rows.push(row = { name: b.name, repo: b.repo, local: false, remote: false });
    if (b.location === 'remote') row.remote = true; else row.local = true;
  }
  return rows;
});
const blockingWhere = (b: BlockingRow) => (b.local && b.remote ? 'here and on origin' : b.remote ? 'on origin' : 'here');
const memberPrefix = computed(() => {
  const t = status.value?.teamBranch ?? '';
  return t.startsWith('team/') ? `members/${t.slice('team/'.length)}/…` : 'members/<team>/…';
});
const blockingTarget = (b: BlockingRow): DeleteTarget => ({ branch: b.name, remote: b.remote, unmerged: true, mergeUnknown: true });

function deleteParams(d: NonNullable<typeof deleting.value>, confirm?: string) {
  return {
    ...which(), branches: [d.branch], remote: d.remote,
    ...(d.unmerged && d.unmergedOk ? { unmergedOk: [d.branch] } : {}),
    ...(confirm ? { confirm } : {}),
  };
}

async function planDelete() {
  const d = deleting.value;
  if (!d) return;
  busy.value = 'plan';
  d.error = null;
  d.plan = null;
  try {
    const p = await api.call<GitDeletePlan>('git.deleteBranches', deleteParams(d));
    if (deleting.value === d) d.plan = p;
  } catch (e) {
    d.error = errorText(e);
  } finally {
    busy.value = null;
  }
}

function startDelete(t: DeleteTarget) {
  if (busy.value) return;
  confirming.value = false;
  result.value = null;
  deleting.value = { ...t, unmergedOk: false, plan: null, note: null, error: null };
  void planDelete();
}

function chooseUnmerged(ok: boolean) {
  const d = deleting.value;
  if (!d || busy.value) return;
  d.unmergedOk = ok;
  d.note = null;
  void planDelete();
}

async function confirmDelete() {
  const d = deleting.value;
  if (!d?.plan || d.plan.empty || busy.value) return;
  busy.value = 'delete';
  d.error = null;
  d.note = null;
  let replan = false;
  try {
    const r = await api.call<GitDeleteResult>('git.deleteBranches', deleteParams(d, d.plan.token));
    deleting.value = null;
    result.value = { outcome: 'deleted', message: r.result, changed: true, sha: null, files: null, status: null };
    await load();
  } catch (e) {
    if (errorCode(e) === 'stale') { d.note = errorText(e); replan = true; }
    else d.error = errorText(e);
  } finally {
    busy.value = null;
  }
  if (replan) await planDelete(); // the stale note stays above the new plan
}

onMounted(() => { void loadChoices(); void load(); });
</script>

<template>
  <Modal :title="`Git · ${team.name}`" wide @close="emit('close')">
    <nav v-if="choices.length > 1" class="repos" data-test="git-repos">
      <button v-for="c in choices" :key="c.name" :class="{ on: repo === c.name }" :data-test="`git-repo-${c.name || 'main'}`" @click="pick(c.name)">{{ c.label }}</button>
    </nav>
    <p v-if="error" class="error" data-test="git-error">{{ error }}</p>
    <LoadingNote v-if="!status && !error" text="Reading the repository…" />

    <template v-if="status">
      <section class="repo">
        <div><strong>{{ status.repoPath }}</strong><span v-if="status.managed" class="chip">made by the app</span></div>
        <div class="muted small">
          Base branch <code>{{ status.baseBranch }}</code> at {{ short(status.baseSha) }}
          <template v-if="status.checkedOut"> · checked out: <code>{{ status.checkedOut }}</code></template>
          <span v-if="status.dirty" class="chip warn">uncommitted changes</span>
        </div>
        <div class="muted small" data-test="git-origin">
          <template v-if="status.origin">Origin {{ status.origin }}:
            <template v-if="status.originBaseSha">its {{ status.baseBranch }} at {{ short(status.originBaseSha) }}
              <span v-if="status.baseBehindOrigin" class="chip">yours is {{ status.baseBehindOrigin }} behind</span>
              <span v-if="status.baseAheadOfOrigin" class="chip warn">yours has {{ status.baseAheadOfOrigin }} not on origin</span>
            </template>
            <template v-else>not fetched yet</template>
          </template>
          <template v-else>No origin: merges stay in this repository.</template>
        </div>
      </section>

      <section class="merge" :class="`state-${status.merge}`" data-test="git-merge-state">
        <div><strong>{{ status.teamBranch }}</strong> {{ short(status.teamSha) }}</div>
        <div>{{ mergeText }}</div>
        <div v-if="status.conflictFiles.length" class="small">Conflicting files: <code>{{ status.conflictFiles.join(', ') }}</code></div>
        <div v-if="behindText" class="small behind" data-test="git-behind">{{ behindText }}
          <button v-if="canSync" data-test="git-sync-now" :disabled="!!busy" title="Merge the base into the team and member branches (never a rebase; a conflict changes nothing)"
            @click="syncNow"><BusyLabel :busy="busy === 'sync'" text="Syncing…">Sync now</BusyLabel></button>
        </div>
      </section>

      <div class="row actions">
        <button v-if="status.origin" data-test="git-fetch" :disabled="!!busy" @click="act('git.fetch', 'fetch')"><BusyLabel :busy="busy === 'fetch'" text="Fetching…">Fetch</BusyLabel></button>
        <button v-if="status.origin" data-test="git-bring-current" :disabled="!!busy" title="Fast-forward your base branch to origin's"
          @click="act('git.bringCurrent', 'bring')"><BusyLabel :busy="busy === 'bring'" text="Bringing current…">Bring {{ status.baseBranch }} current</BusyLabel></button>
        <button v-if="canMerge && !confirming" class="primary" data-test="git-merge" :disabled="!!busy" @click="confirming = true">Merge to {{ status.baseBranch }}…</button>
        <span v-if="confirming" class="confirm" data-test="git-merge-confirm">
          Merge {{ status.teamBranch }} into {{ status.baseBranch }}<template v-if="status.origin"> and push {{ status.baseBranch }} to origin</template>?
          <button class="primary" data-test="git-merge-yes" :disabled="!!busy" @click="act('git.merge', 'merge')"><BusyLabel :busy="busy === 'merge'" text="Merging…">Merge</BusyLabel></button>
          <button data-test="git-merge-cancel" @click="confirming = false">Cancel</button>
        </span>
        <button v-if="status.merge === 'conflict' || (status.teamBehindBase > 0 && status.merge !== 'none')" data-test="git-ask-team" :disabled="!!busy"
          title="Tell the Manager to merge the base into its branch and resolve the conflicts" @click="askTeam"><BusyLabel :busy="busy === 'ask'" text="Asking…">Ask the team to bring it current</BusyLabel></button>
        <button data-test="git-refresh" :disabled="!!busy" @click="refresh"><BusyLabel :busy="busy === 'refresh'" text="Refreshing…">Refresh</BusyLabel></button>
      </div>

      <p v-if="result" class="result" :class="{ ok: result.changed }" data-test="git-result">{{ result.message }}
        <template v-if="result.files?.length"> Files: <code>{{ result.files.join(', ') }}</code></template></p>

      <section v-for="b in blockingBranches" :key="`blocking:${b.repo}:${b.name}`" class="blocking" data-test="git-blocking-branch">
        <div><strong><code>{{ b.name }}</code> blocks this team's member branches</strong></div>
        <div class="small">Git cannot keep it beside the <code>{{ memberPrefix }}</code> branches, so members cannot start until it is
          deleted or renamed (or the team is renamed).</div>
        <div class="muted small">Repository <span data-test="git-blocking-repo">{{ b.repo }}</span> · {{ blockingWhere(b) }}</div>
        <div><button class="small-btn" :data-test="`git-delete-blocking-${b.name}`" :disabled="!!busy" @click="startDelete(blockingTarget(b))">Delete…</button></div>
      </section>

      <section v-if="deleting" class="delete" data-test="git-delete">
        <div><strong>Delete <code>{{ deleting.branch }}</code></strong><template v-if="deleting.remote"> here and on origin</template></div>
        <p v-if="deleting.note" class="small note" data-test="git-delete-stale">{{ deleting.note }} Planned again below.</p>
        <label v-if="deleting.unmerged" class="small" data-test="git-delete-unmerged">
          <input type="checkbox" :checked="deleting.unmergedOk" :disabled="!!busy" @change="chooseUnmerged(($event.target as HTMLInputElement).checked)" />
          <template v-if="deleting.mergeUnknown">Delete it even if the plan keeps it as not merged (its unmerged commits are lost)</template>
          <template v-else>Delete it although its commits are not merged (they are lost)</template>
        </label>
        <LoadingNote v-if="!deleting.plan && !deleting.error" text="Planning the delete…" />
        <pre v-if="deleting.plan" class="plan" data-test="git-delete-plan">{{ deleting.plan.plan }}</pre>
        <p v-if="deleting.plan?.empty" class="muted small" data-test="git-delete-empty">Nothing to delete.</p>
        <p v-if="deleting.error" class="error" data-test="git-delete-error">{{ deleting.error }}</p>
        <div class="row actions">
          <button v-if="deleting.plan && !deleting.plan.empty" class="danger" data-test="git-delete-yes" :disabled="!!busy" @click="confirmDelete">
            <BusyLabel :busy="busy === 'delete'" text="Deleting…">Delete</BusyLabel></button>
          <button data-test="git-delete-cancel" :disabled="busy === 'delete'" @click="deleting = null">{{ deleting.plan?.empty ? 'Close' : 'Cancel' }}</button>
        </div>
      </section>

      <table class="list" data-test="git-members">
        <tr><th>Member branch</th><th>Commit</th><th>Ahead of {{ status.baseBranch }}</th><th>In {{ status.teamBranch }}</th><th></th></tr>
        <tr v-for="b in status.members" :key="b.branch">
          <td><code>{{ b.branch }}</code></td><td>{{ short(b.sha) }}</td><td>{{ b.aheadOfBase }}</td><td>{{ b.inTeamBranch ? 'yes' : 'not yet' }}</td>
          <td><button class="small-btn" :data-test="`git-delete-${b.branch}`" :disabled="!!busy" @click="startDelete(memberTarget(b))">Delete…</button></td>
        </tr>
        <tr v-if="status.members.length === 0 && oldBranches.length === 0"><td colspan="5" class="muted">No member has committed yet.</td></tr>
        <tr v-for="o in oldBranches" :key="`old:${o.name}`" class="old" data-test="git-old-branch">
          <td><code>{{ o.name }}</code><span class="chip" title="Named before member branches were members/<team>/<member>; nothing uses it now">old</span></td>
          <td>{{ oldWhere(o) }}</td><td>—</td><td>{{ o.merged ? 'merged' : 'not merged' }}</td>
          <td><button class="small-btn" :data-test="`git-delete-old-${o.name}`" :disabled="!!busy" @click="startDelete(oldTarget(o))">Delete…</button></td>
        </tr>
      </table>
      <p class="muted small">The app never forces, rebases or stashes, and never pushes your own commits on {{ status.baseBranch }}. A merge that
        would conflict changes nothing.</p>
    </template>
  </Modal>
</template>

<style scoped>
.repo, .merge { display: flex; flex-direction: column; gap: 4px; margin-bottom: 10px; }
.merge { padding: 8px 10px; border-radius: 6px; background: var(--chip, #eef1f4); }
.merge.state-conflict { background: #fbe3e1; border-left: 3px solid var(--state-failed); }
.merge.state-fast-forward, .merge.state-merge { background: #e2f3ea; }
.actions { gap: 8px; flex-wrap: wrap; margin: 8px 0; align-items: center; }
.confirm { display: inline-flex; gap: 6px; align-items: center; }
.chip { margin-left: 6px; font-size: 11px; padding: 0 5px; border-radius: 8px; background: var(--chip, #eee); }
.chip.warn { background: #f6d58a; }
.delete { padding: 8px 10px; border-radius: 6px; border-left: 3px solid var(--state-failed); background: var(--chip, #eef1f4); margin: 8px 0; }
.delete .plan { white-space: pre-wrap; word-break: break-word; background: var(--bg); border-radius: 4px; padding: 6px 8px; margin: 6px 0; font-size: 12.5px; max-height: 240px; overflow: auto; }
.delete .note { color: #8a5a00; }
.blocking { display: flex; flex-direction: column; gap: 4px; padding: 8px 10px; border-radius: 6px; border-left: 3px solid #c9a227; background: var(--chip, #eef1f4); margin: 8px 0; }
.small-btn { font-size: 12px; padding: 1px 8px; }
.result { padding: 6px 8px; border-left: 3px solid #c9a227; }
.result.ok { border-left-color: #2e8b57; }
.list { width: 100%; border-collapse: collapse; margin: 8px 0; }
.list th, .list td { text-align: left; padding: 4px 6px; border-bottom: 1px solid var(--border, #ddd); }
.repos { display: flex; gap: 4px; border-bottom: 1px solid var(--line); margin-bottom: 10px; }
.repos button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 6px 10px; font-weight: 600; color: var(--ink-soft); }
.repos button.on { color: var(--accent); border-bottom-color: var(--accent); }
</style>
