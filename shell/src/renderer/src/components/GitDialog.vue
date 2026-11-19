<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type {
  BranchSyncResult, GitDeletePlan, GitDeleteResult, YawbleApi, TeamGitBranch, TeamGitOldBranch, TeamGitResult, TeamGitStatus, TeamSummary,
} from '../../../shared/api';
import { errorCode, errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import TidyPanel from './TidyPanel.vue';
import type { GitWebLinkQuery, RepoProviderInfo } from '../../../shared/api';
import { PROVIDER_LABELS, effectiveProvider, fetchWebLinks, linkKey, openOnText } from '../gitWebLinks';
import CommitLink from './CommitLink.vue';
import CommitList from './CommitList.vue';
import LocalMark from './LocalMark.vue';
import MergePreviewPanel from './MergePreviewPanel.vue';
import { GIT_COMMIT_RANGE, GIT_DELETE_PREVIEW, GIT_MERGE_PREVIEW } from '../../../shared/api';
import type { CommitRange, CommitRow, DeletePreview, MergePreview } from '../../../shared/api';
import { commits, commitsInHeading, deletePreviewView, mergePreviewView, rangeFor, type CountKind, type RangeAsk } from '../gitRange';
import { branchLocalOnly, branchLocalTitle } from '../localMark';

// The Git dialog: for the team's main repository or one of its writable extra repositories (the tabs on
// top), see the team's branches against the base branch, fetch origin,
// bring the base current, merge the team branch to it, or ask the team to bring its branch current. The person's
// buttons only. Nothing is ever forced, rebased or stashed; a merge that would conflict changes nothing.
/** focusBranch (B072, the Commit view's Show its branch): that branch's row is marked. */
const props = defineProps<{ api?: YawbleApi; team: TeamSummary; focusBranch?: string | null }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const status = ref<TeamGitStatus | null>(null);
const error = ref<string | null>(null);
/** B073: a result may list the commits that came in (each opens the Commit view) and, after a delete, the deleted
 * branches' last commits the app keeps on record. */
type DialogResult = TeamGitResult & { commitsHeading?: string | null; commitsList?: CommitRow[] | null; commitsTotal?: number; recorded?: { branch: string; sha: string }[] | null };
const result = ref<DialogResult | null>(null);
const busy = ref<string | null>(null);
const confirming = ref(false);

// Which repository: the main one ('') or a writable extra by name. Read-only references have no team branch.
const repo = ref('');
const choices = ref<{ name: string; label: string }[]>([]);
const mainName = ref<string | null>(null);
// B067: Tidy… (every branch of the team's repositories in one plan; TidyPanel).
const tidying = ref(false);
// B075: each repository's provider (teams.repos), for the "Open on …" text and the pull request line.
const providers = ref<Record<string, RepoProviderInfo>>({});
async function loadChoices() {
  try {
    const r = await api.call<{ main: ({ name: string | null } & RepoProviderInfo) | null; extras: ({ name: string; writable: boolean } & RepoProviderInfo)[] }>('teams.repos', { teamId: props.team.id });
    mainName.value = r.main?.name ?? null;
    choices.value = [{ name: '', label: r.main?.name ?? 'main' }, ...r.extras.filter((e) => e.writable).map((e) => ({ name: e.name, label: e.name }))];
    providers.value = { ...(r.main ? { '': r.main } : {}), ...Object.fromEntries(r.extras.map((e) => [e.name, e])) };
  } catch { choices.value = []; }
}
const providerInfo = computed<RepoProviderInfo | null>(() => providers.value[repo.value] ?? null);
const openOn = computed(() => openOnText(effectiveProvider(providerInfo.value)));
// The pull request line: only when the core says (prSupported or prUnavailable); an older core shows nothing.
const prLine = computed(() => {
  const p = providerInfo.value;
  if (!p || (p.prSupported === undefined && !p.prUnavailable)) return null;
  if (p.prSupported) {
    const name = effectiveProvider(p);
    return { ok: true, text: `Pull requests on ${name && name !== 'none' ? PROVIDER_LABELS[name] : 'this host'}: the team and the Companion can open, list and merge them (each asks you first).` };
  }
  return { ok: false, text: p.prUnavailable || 'Pull requests are not supported for this repository from here.' };
});

// B075: "Open on …" links for what the dialog shows (the core answers null for anything not on origin: no link then).
const links = ref<Record<string, string>>({});
const link = (q: GitWebLinkQuery) => links.value[linkKey(q)] ?? null;
function linkQueries(s: TeamGitStatus): GitWebLinkQuery[] {
  const q: GitWebLinkQuery[] = [{ kind: 'branch', branch: s.baseBranch }];
  if (s.baseSha) q.push({ kind: 'commit', commit: s.baseSha });
  if (s.teamSha) {
    q.push({ kind: 'branch', branch: s.teamBranch }, { kind: 'commit', commit: s.teamSha }, { kind: 'compare', from: s.baseBranch, to: s.teamBranch });
    for (const f of s.conflictFiles) q.push({ kind: 'file', commit: s.teamSha, file: f });
  }
  for (const m of s.members) q.push({ kind: 'branch', branch: m.branch }, { kind: 'commit', commit: m.sha });
  return q;
}
async function loadLinks(s: TeamGitStatus) {
  const forRepo = repo.value;
  if (!s.origin) { links.value = {}; return; }
  const got = await fetchWebLinks(api, props.team, forRepo || null, linkQueries(s));
  if (repo.value === forRepo && status.value === s) links.value = got;
}
const which = () => (repo.value ? { teamId: props.team.id, repo: repo.value } : { teamId: props.team.id });
function pick(name: string) {
  if (repo.value === name) return;
  repo.value = name;
  status.value = null;
  result.value = null;
  confirming.value = false;
  deleting.value = null;
  range.value = null;
  preview.value = null;
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
  const before = status.value?.baseSha ?? null;
  const base = status.value?.baseBranch ?? 'main';
  try {
    const r = await api.call<TeamGitResult>(method, which());
    result.value = r;
    if (r.status) status.value = r.status;
    if (method !== 'git.fetch') {
      // B073: the commits that came in / landed (the core's commits; an older core: read the range before..after).
      const given = r.commits === undefined ? undefined : { commits: r.commits ?? [], count: r.commitCount ?? r.commits?.length ?? 0 };
      void commitsIn(base, given, before, r.status?.baseSha ?? r.sha ?? null);
    }
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = null;
    // The merge confirmation stays up (its Merge button shows Merging…) until the core answers.
    confirming.value = false;
    preview.value = null;
  }
}

// ---- B073: the commits an action brought into a branch: the core's list when it sent one (empty: nothing came in),
// else (an older core) git.commitRange from the commit before to the one after.
async function commitsIn(branch: string, given: { commits: CommitRow[]; count: number } | undefined, before: string | null, after: string | null) {
  const res = result.value;
  if (!res) return;
  const show = (list: CommitRow[], count: number) => {
    if (result.value !== res || !list.length) return;
    res.commitsHeading = commitsInHeading(count, branch);
    res.commitsList = list;
    res.commitsTotal = count;
  };
  if (given) { show(given.commits, given.count); return; }
  if (!before || !after || before === after) return;
  try {
    const d = await api.call<CommitRange>(GIT_COMMIT_RANGE, { ...which(), from: before, to: after });
    show(d?.commits ?? [], d?.count ?? 0);
  } catch { /* an older core: the result's message only */ }
}

// ---- B073: a count opens the list of those commits (git.commitRange); the same link again closes it (another link for
// the same range, e.g. the two "behind" counts, shows it again).
const range = ref<{ key: string; ask: RangeAsk; data: CommitRange | null; error: string | null } | null>(null);
async function openRange(kind: CountKind, branch?: string, source: string = kind) {
  const s = status.value;
  if (!s) return;
  const key = `${source}:${branch ?? ''}`;
  if (range.value?.key === key) { range.value = null; return; }
  const ask = rangeFor(s, kind, branch);
  if (!ask) return;
  range.value = { key, ask, data: null, error: null };
  const cur = range.value;
  try {
    const d = await api.call<CommitRange>(GIT_COMMIT_RANGE, { ...which(), from: ask.from, to: ask.to });
    if (range.value === cur) cur.data = { ...d, commits: d?.commits ?? [] };
  } catch (e) {
    if (range.value === cur) cur.error = errorText(e);
  }
}

// ---- B073: Merge to <base>… reads git.mergePreview first; Merge is offered under it (an older core: no preview, as before).
const preview = ref<{ data: MergePreview | null; error: string | null } | null>(null);
const previewView = computed(() => (preview.value?.data ? mergePreviewView(preview.value.data) : null));
function startMerge() {
  confirming.value = true;
  deleting.value = null;
  preview.value = { data: null, error: null };
  const cur = preview.value;
  void api.call<MergePreview>(GIT_MERGE_PREVIEW, which()).then(
    (d) => { if (preview.value === cur) cur.data = { ...d, commits: d?.commits ?? [], files: d?.files ?? [], conflictFiles: d?.conflictFiles ?? [], count: d?.count ?? 0 }; },
    (e) => { if (preview.value === cur) cur.error = errorText(e); },
  );
}
function cancelMerge() { confirming.value = false; preview.value = null; }

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
  const before = status.value?.teamSha ?? null;
  const teamBranch = status.value?.teamBranch ?? 'the team branch';
  try {
    const r = await api.call<BranchSyncResult>('git.sync', which());
    const moved = r.moved.length ? ` (${r.moved.join(', ')})` : '';
    result.value = {
      outcome: r.outcome, changed: r.outcome === 'synced', sha: r.baseSha, files: r.conflicts.length ? r.conflicts : null, status: null,
      message: r.outcome === 'conflict' ? (r.note ?? 'Could not sync automatically; nothing changed.')
        : r.outcome === 'synced' ? `Brought the branches up to ${r.baseBranch}${moved}.` : `Already up to ${r.baseBranch}.`,
    };
    await load();
    const given = r.incoming === undefined ? undefined : { commits: r.incoming ?? [], count: r.incomingCount ?? r.incoming?.length ?? 0 };
    if (r.outcome === 'synced') await commitsIn(teamBranch, given, before, status.value?.teamSha ?? null);
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
// B073: beside the plan, git.deletePreview lists the branch's commits not in the base, each "already in main" or "work of
// its own", so the person sees what is lost (an older core: the plan alone, as before).
const deleting = ref<(DeleteTarget & {
  unmergedOk: boolean; plan: GitDeletePlan | null; note: string | null; error: string | null;
  preview: DeletePreview | null; previewError: string | null;
}) | null>(null);
const deleteView = computed(() => (deleting.value?.preview ? deletePreviewView(deleting.value.preview) : null));
const deleteMarks = computed(() => Object.fromEntries((deleteView.value?.rows ?? []).map((r) => [r.commit.sha, { text: r.mark, own: r.own }])));
async function loadDeletePreview() {
  const d = deleting.value;
  if (!d) return;
  try {
    const p = await api.call<DeletePreview>(GIT_DELETE_PREVIEW, { ...which(), branch: d.branch });
    if (deleting.value === d) d.preview = { ...p, commits: p?.commits ?? [] };
  } catch (e) {
    if (deleting.value === d) d.previewError = errorCode(e) === 'method_not_found' ? null : errorText(e);
  }
}
const oldBranches = computed(() => status.value?.oldBranches ?? []);
const memberTarget = (b: TeamGitBranch): DeleteTarget => ({ branch: b.branch, remote: !!status.value?.origin, unmerged: !b.inTeamBranch && b.aheadOfBase > 0 });
const oldTarget = (o: TeamGitOldBranch): DeleteTarget => ({ branch: o.name, remote: o.remote, unmerged: !o.merged });
const oldWhere = (o: TeamGitOldBranch) => (o.local && o.remote ? 'here and on origin' : o.remote ? 'on origin' : 'here');

// A members/<slug> branch named like this team's slug blocks its members/<slug>/<member> branches (git cannot hold
// both). git.status lists one entry per ref (here, on origin); the dialog shows one per branch and repository. Whether
// its commits are merged is not known here, so the person may tick to delete it anyway; the core's plan says which.
// One a member is still on (inUseBy, core card 104) has no Delete: that member's next start moves it, which frees the name.
interface BlockingRow { name: string; repo: string; local: boolean; remote: boolean; inUseBy?: string; movesTo?: string }
const blockingBranches = computed<BlockingRow[]>(() => {
  const rows: BlockingRow[] = [];
  for (const b of status.value?.blockingBranches ?? []) {
    let row = rows.find((r) => r.name === b.name && r.repo === b.repo);
    if (!row) rows.push(row = { name: b.name, repo: b.repo, local: false, remote: false });
    if (b.location === 'remote') row.remote = true; else row.local = true;
    if (b.inUseBy) { row.inUseBy = b.inUseBy; row.movesTo = b.movesTo; }
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
  preview.value = null;
  result.value = null;
  deleting.value = { ...t, unmergedOk: false, plan: null, note: null, error: null, preview: null, previewError: null };
  void planDelete();
  void loadDeletePreview();
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
    // The deleted branch's last commit still opens (the core keeps it on record); an older core: the preview's tip.
    const recorded = d.preview?.exists && d.preview.sha ? [{ branch: d.branch, sha: d.preview.sha }] : null;
    result.value = { outcome: 'deleted', message: r.result, changed: true, sha: null, files: null, status: null, recorded };
    await load();
  } catch (e) {
    if (errorCode(e) === 'stale') { d.note = errorText(e); replan = true; }
    else d.error = errorText(e);
  } finally {
    busy.value = null;
  }
  if (replan) await planDelete(); // the stale note stays above the new plan
}

// Every new status (open, refresh, fetch, merge, sync, another repository) asks for its links again.
watch(status, (s) => { links.value = {}; if (s) void loadLinks(s); });

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
          Base branch <code>{{ status.baseBranch }}</code><a v-if="link({ kind: 'branch', branch: status.baseBranch })" class="web" data-test="git-link-base-branch"
            :href="link({ kind: 'branch', branch: status.baseBranch })!" target="_blank" rel="noopener">{{ openOn }}</a>
          at <CommitLink :team="team.name" :sha="status.baseSha" /><a v-if="status.baseSha && link({ kind: 'commit', commit: status.baseSha })" class="web"
            data-test="git-link-base-commit" :href="link({ kind: 'commit', commit: status.baseSha })!" target="_blank" rel="noopener">{{ openOn }}</a>
          <template v-if="status.checkedOut"> · checked out: <code>{{ status.checkedOut }}</code></template>
          <span v-if="status.dirty" class="chip warn">uncommitted changes</span>
        </div>
        <div class="muted small" data-test="git-origin">
          <template v-if="status.origin">Origin {{ status.origin }}:
            <template v-if="status.originBaseSha">its {{ status.baseBranch }} at <CommitLink :team="team.name" :sha="status.originBaseSha" />
              <button v-if="status.baseBehindOrigin" type="button" class="chip count" data-test="git-count-base-behind-origin" :title="`List the commits on origin's ${status.baseBranch} not in yours`"
                @click="openRange('base-behind-origin')">yours is {{ status.baseBehindOrigin }} behind</button>
              <button v-if="status.baseAheadOfOrigin" type="button" class="chip warn count" data-test="git-count-base-ahead-of-origin" :title="`List the commits on your ${status.baseBranch} not on origin`"
                @click="openRange('base-ahead-of-origin')">yours has {{ status.baseAheadOfOrigin }} not on origin</button>
            </template>
            <template v-else>not fetched yet</template>
          </template>
          <template v-else>No origin: merges stay in this repository.</template>
        </div>
        <div v-if="prLine" class="small" :class="{ muted: !prLine.ok }" data-test="git-prs">{{ prLine.text }}</div>
      </section>

      <section class="merge" :class="`state-${status.merge}`" data-test="git-merge-state">
        <div :class="{ focus: focusBranch === status.teamBranch }" :data-focus="focusBranch === status.teamBranch ? 'true' : undefined"><strong>{{ status.teamBranch }}</strong>
          <CommitLink :team="team.name" :sha="status.teamSha" /><a v-if="status.teamSha && link({ kind: 'commit', commit: status.teamSha })" class="web"
            data-test="git-link-team-commit" :title="`The commit ${openOn}`" :href="link({ kind: 'commit', commit: status.teamSha })!" target="_blank" rel="noopener">{{ openOn }}</a>
          <a v-if="link({ kind: 'branch', branch: status.teamBranch })" class="web" data-test="git-link-team-branch" :title="`The branch ${openOn}`"
            :href="link({ kind: 'branch', branch: status.teamBranch })!" target="_blank" rel="noopener">{{ openOn }}</a>
          <a v-if="link({ kind: 'compare', from: status.baseBranch, to: status.teamBranch })" class="web" data-test="git-link-compare" :title="openOn"
            :href="link({ kind: 'compare', from: status.baseBranch, to: status.teamBranch })!" target="_blank" rel="noopener">Compare with {{ status.baseBranch }}</a>
        </div>
        <div data-test="git-merge-text">
          <template v-if="status.merge === 'fast-forward'">{{ status.teamBranch }} is <button type="button" class="count" data-test="git-count-team-ahead"
            @click="openRange('team-ahead')">{{ commits(status.teamAheadOfBase) }} ahead</button>: {{ status.baseBranch }} will fast-forward to it.</template>
          <template v-else-if="status.merge === 'merge' || (status.merge === 'conflict' && status.teamAheadOfBase > 0)">{{ status.teamBranch }} is <button type="button" class="count"
            data-test="git-count-team-ahead" @click="openRange('team-ahead')">{{ commits(status.teamAheadOfBase) }} ahead</button> and <button type="button" class="count"
            data-test="git-count-team-behind" @click="openRange('team-behind')">{{ status.teamBehindBase }} behind</button><template
            v-if="status.merge === 'merge'">: a merge commit will join them (no rebase).</template><template v-else>: it does not merge cleanly into {{ status.baseBranch }}.</template></template>
          <template v-else>{{ mergeText }}</template>
        </div>
        <div v-if="status.conflictFiles.length" class="small">Conflicting files:
          <template v-for="(f, i) in status.conflictFiles" :key="f">{{ i ? ', ' : '' }}<a v-if="status.teamSha && link({ kind: 'file', commit: status.teamSha, file: f })"
            class="mono" :data-test="`git-link-file-${f}`" :title="`${openOn} (at ${short(status.teamSha)})`" :href="link({ kind: 'file', commit: status.teamSha, file: f })!"
            target="_blank" rel="noopener">{{ f }}</a><code v-else>{{ f }}</code></template></div>
        <div v-if="behindText" class="small behind" data-test="git-behind">{{ status.teamBranch }} is <button type="button" class="count" data-test="git-count-behind"
          @click="openRange('team-behind', undefined, 'behind-line')">{{ commits(status.teamBehindBase) }} behind</button> {{ status.baseBranch }}
          <button v-if="canSync" data-test="git-sync-now" :disabled="!!busy" title="Merge the base into the team and member branches (never a rebase; a conflict changes nothing)"
            @click="syncNow"><BusyLabel :busy="busy === 'sync'" text="Syncing…">Sync now</BusyLabel></button>
        </div>
      </section>

      <section v-if="range" class="range" data-test="git-range" :data-from="range.ask.from" :data-to="range.ask.to">
        <div class="range-head"><strong data-test="git-range-title">{{ range.ask.title }}</strong>
          <button type="button" class="small-btn" data-test="git-range-close" @click="range = null">Close</button></div>
        <LoadingNote v-if="!range.data && !range.error" text="Reading the commits…" />
        <p v-if="range.error" class="error" data-test="git-range-error">{{ range.error }}</p>
        <CommitList v-if="range.data" :team="team.name" :commits="range.data.commits" :total="range.data.count" :has-origin="range.data.hasOrigin" empty="No commits: the branches are level here." />
      </section>

      <template v-if="confirming && preview">
        <LoadingNote v-if="!preview.data && !preview.error" text="Reading what the merge would do…" />
        <p v-if="preview.error" class="muted small" data-test="git-merge-preview-error">The preview could not be read: {{ preview.error }}</p>
        <MergePreviewPanel v-if="preview.data" :api="api" :team="team.name" :params="which()" :preview="preview.data" />
      </template>

      <div class="row actions">
        <button v-if="status.origin" data-test="git-fetch" :disabled="!!busy" @click="act('git.fetch', 'fetch')"><BusyLabel :busy="busy === 'fetch'" text="Fetching…">Fetch</BusyLabel></button>
        <button v-if="status.origin" data-test="git-bring-current" :disabled="!!busy" title="Fast-forward your base branch to origin's"
          @click="act('git.bringCurrent', 'bring')"><BusyLabel :busy="busy === 'bring'" text="Bringing current…">Bring {{ status.baseBranch }} current</BusyLabel></button>
        <button v-if="canMerge && !confirming" class="primary" data-test="git-merge" :disabled="!!busy" @click="startMerge">Merge to {{ status.baseBranch }}…</button>
        <span v-if="confirming" class="confirm" data-test="git-merge-confirm">
          Merge {{ status.teamBranch }} into {{ status.baseBranch }}<template v-if="status.origin"> and push {{ status.baseBranch }} to origin</template>?
          <button class="primary" data-test="git-merge-yes" :disabled="!!busy || (!!preview && !preview.data && !preview.error) || (!!previewView && !previewView.canMerge)"
            @click="act('git.merge', 'merge')"><BusyLabel :busy="busy === 'merge'" text="Merging…">Merge</BusyLabel></button>
          <button data-test="git-merge-cancel" @click="cancelMerge">Cancel</button>
        </span>
        <button v-if="status.merge === 'conflict' || (status.teamBehindBase > 0 && status.merge !== 'none')" data-test="git-ask-team" :disabled="!!busy"
          title="Tell the Manager to merge the base into its branch and resolve the conflicts" @click="askTeam"><BusyLabel :busy="busy === 'ask'" text="Asking…">Ask the team to bring it current</BusyLabel></button>
        <button data-test="git-tidy" :disabled="!!busy || tidying" title="Every branch here, on origin and in the members' clones in one list: delete the merged ones, realign moved ones"
          @click="tidying = true; deleting = null; confirming = false">Tidy…</button>
        <button data-test="git-refresh" :disabled="!!busy" @click="refresh"><BusyLabel :busy="busy === 'refresh'" text="Refreshing…">Refresh</BusyLabel></button>
      </div>

      <p v-if="result" class="result" :class="{ ok: result.changed }" data-test="git-result">{{ result.message }}
        <template v-if="result.files?.length"> Files: <code>{{ result.files.join(', ') }}</code></template></p>
      <section v-if="result?.commitsList?.length" class="came-in" data-test="git-result-commits">
        <div class="small"><strong>{{ result.commitsHeading }}</strong></div>
        <CommitList :team="team.name" :commits="result.commitsList" :total="result.commitsTotal" :has-origin="!!status?.origin" />
      </section>
      <p v-if="result?.recorded?.length" class="small" data-test="git-result-recorded">
        <template v-for="(x, i) in result.recorded" :key="x.branch">{{ i ? ' · ' : '' }}<code>{{ x.branch }}</code>'s last commit, kept on record:
          <CommitLink :team="team.name" :sha="x.sha" /></template></p>

      <TidyPanel v-if="tidying" :api="api" :team="team" :main-name="mainName" @close="tidying = false" @changed="load()" />

      <section v-for="b in blockingBranches" :key="`blocking:${b.repo}:${b.name}`" class="blocking" data-test="git-blocking-branch">
        <div><strong><code>{{ b.name }}</code> blocks this team's member branches</strong></div>
        <div v-if="b.inUseBy" class="small" data-test="git-blocking-in-use">Git cannot keep it beside the <code>{{ memberPrefix }}</code>
          branches. It is the old branch of {{ b.inUseBy }}, who is still on it, so it cannot be deleted here: it moves to
          <code>{{ b.movesTo }}</code> at that member's next start. Start that member first, then the others.</div>
        <div v-else class="small">Git cannot keep it beside the <code>{{ memberPrefix }}</code> branches, so members cannot start until it is
          deleted.</div>
        <div class="muted small">Repository <span data-test="git-blocking-repo">{{ b.repo }}</span> · {{ blockingWhere(b) }}</div>
        <div v-if="!b.inUseBy"><button class="small-btn" :data-test="`git-delete-blocking-${b.name}`" :disabled="!!busy" @click="startDelete(blockingTarget(b))">Delete…</button></div>
      </section>

      <section v-if="deleting" class="delete" data-test="git-delete">
        <div><strong>Delete <code>{{ deleting.branch }}</code></strong><template v-if="deleting.remote"> here and on origin</template><LocalMark
          v-if="deleting.preview && branchLocalOnly(deleting.preview.push, deleting.preview.hasOrigin)" :title="branchLocalTitle(deleting.preview.push!)" /></div>
        <p v-if="deleting.note" class="small note" data-test="git-delete-stale">{{ deleting.note }} Planned again below.</p>
        <label v-if="deleting.unmerged" class="small" data-test="git-delete-unmerged">
          <input type="checkbox" :checked="deleting.unmergedOk" :disabled="!!busy" @change="chooseUnmerged(($event.target as HTMLInputElement).checked)" />
          <template v-if="deleting.mergeUnknown">Delete it even if the plan keeps it as not merged (its unmerged commits are lost)</template>
          <template v-else>Delete it although its commits are not merged (they are lost)</template>
        </label>
        <div v-if="deleteView" class="lost" data-test="git-delete-preview">
          <div class="small" :class="{ loses: deleteView.loses }" data-test="git-delete-summary">{{ deleteView.summary }}</div>
          <CommitList v-if="deleteView.rows.length" :team="team.name" :commits="deleting.preview!.commits" :has-origin="deleting.preview!.hasOrigin" :marks="deleteMarks" />
        </div>
        <p v-else-if="deleting.previewError" class="muted small" data-test="git-delete-preview-error">Its commits could not be listed: {{ deleting.previewError }}</p>
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
        <tr v-for="b in status.members" :key="b.branch" :class="{ focus: focusBranch === b.branch }" :data-focus="focusBranch === b.branch ? 'true' : undefined" data-test="git-member-row">
          <td><code>{{ b.branch }}</code><LocalMark v-if="branchLocalOnly(b.push, !!status.origin)" :title="branchLocalTitle(b.push!)" /><a v-if="link({ kind: 'branch', branch: b.branch })" class="web" :data-test="`git-link-branch-${b.branch}`"
            :href="link({ kind: 'branch', branch: b.branch })!" target="_blank" rel="noopener">{{ openOn }}</a></td>
          <td><CommitLink :team="team.name" :sha="b.sha" /><a v-if="link({ kind: 'commit', commit: b.sha })" class="web" :data-test="`git-link-commit-${b.branch}`"
            :title="`The commit ${openOn}`" :href="link({ kind: 'commit', commit: b.sha })!" target="_blank" rel="noopener">↗</a></td>
          <td><button v-if="b.aheadOfBase > 0" type="button" class="count" :data-test="`git-count-member-${b.branch}`" :title="`List the ${commits(b.aheadOfBase)} on ${b.branch} not in ${status.baseBranch}`"
            @click="openRange('member-ahead', b.branch)">{{ b.aheadOfBase }}</button><template v-else>{{ b.aheadOfBase }}</template></td><td>{{ b.inTeamBranch ? 'yes' : 'not yet' }}</td>
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
.merge { padding: 8px 10px; border-radius: 6px; background: var(--chip); }
.merge.state-conflict { background: var(--bad-bg); border-left: 3px solid var(--state-failed); }
.merge.state-fast-forward, .merge.state-merge { background: var(--ok-bg); }
.actions { gap: 8px; flex-wrap: wrap; margin: 8px 0; align-items: center; }
.confirm { display: inline-flex; gap: 6px; align-items: center; }
.chip { margin-left: 6px; font-size: 11px; padding: 0 5px; border-radius: 8px; background: var(--chip); }
.chip.warn { background: var(--warn-chip); }
.delete { padding: 8px 10px; border-radius: 6px; border-left: 3px solid var(--state-failed); background: var(--chip); margin: 8px 0; }
.delete .plan { white-space: pre-wrap; word-break: break-word; background: var(--bg); border-radius: 4px; padding: 6px 8px; margin: 6px 0; font-size: 12.5px; max-height: 240px; overflow: auto; }
.delete .note { color: var(--warn-ink); }
.blocking { display: flex; flex-direction: column; gap: 4px; padding: 8px 10px; border-radius: 6px; border-left: 3px solid var(--warn-rule); background: var(--chip); margin: 8px 0; }
.small-btn { font-size: 12px; padding: 1px 8px; }
.web { margin-left: 8px; font-size: 12px; }
.sha { font-family: ui-monospace, Consolas, monospace; }
.result { padding: 6px 8px; border-left: 3px solid var(--warn-rule); }
.result.ok { border-left-color: var(--ok-rule); }
.list { width: 100%; border-collapse: collapse; margin: 8px 0; }
.list th, .list td { text-align: left; padding: 4px 6px; border-bottom: 1px solid var(--line); }
.repos { display: flex; gap: 4px; border-bottom: 1px solid var(--line); margin-bottom: 10px; }
.repos button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 6px 10px; font-weight: 600; color: var(--ink-soft); }
.repos button.on { color: var(--accent); border-bottom-color: var(--accent); }
.count { all: unset; cursor: pointer; color: var(--accent); text-decoration: underline; text-underline-offset: 2px; border-radius: 3px; }
.chip.count { color: var(--accent); margin-left: 6px; font-size: 11px; padding: 0 5px; border-radius: 8px; }
.count:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.range, .came-in { padding: 8px 10px; border-radius: 6px; background: var(--chip); margin: 8px 0; min-width: 0; }
.range-head { display: flex; gap: 8px; align-items: baseline; justify-content: space-between; flex-wrap: wrap; }
.lost { margin: 6px 0; }
.lost .loses { color: var(--warn-ink); font-weight: 600; }
.focus { box-shadow: inset 4px 0 0 var(--accent); background: color-mix(in srgb, var(--accent) 8%, var(--surface)); }
</style>
