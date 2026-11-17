<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { BranchRealignResult, GitDeletePlan, GitDeleteResult, TeamSummary, TidyPlan, YawbleApi } from '../../../shared/api';
import { errorCode, errorText, resolveApi } from '../api';
import { canDelete, defaultSelection, deleteRequests, groupTidy, keptReport, whereText, type DeleteRequest, type TidyRow } from '../tidyPlan';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// B067 (card 362): the Git dialog's "Tidy…". One plan (git.tidyPlan) of every branch of the team's repositories, grouped
// by class; the merged and contained ones are pre-selected, and one yes deletes them through git.deleteBranches (its own
// plan shown first, its token confirms exactly that plan). A branch the member's clone moved (reset or rebased) has
// Realign: old -> new commit, one yes, git.branchRealign (refused while the member runs, the reason shown). After each
// action the plan is read again and what was kept is listed with why.
const props = defineProps<{ api?: YawbleApi; team: TeamSummary; mainName?: string | null }>();
const emit = defineEmits<{ close: []; changed: [] }>();
const api = resolveApi(props.api);

const plan = ref<TidyPlan | null>(null);
const error = ref<string | null>(null);
const selected = ref<Set<string>>(new Set());
const busy = ref<string | null>(null);
const report = ref<{ done: string[]; kept: string[] } | null>(null);
const teamParams = () => ({ teamId: props.team.id, team: props.team.name });

const groups = computed(() => groupTidy(plan.value));
const requests = computed(() => deleteRequests(plan.value, selected.value, props.mainName));
const selectedCount = computed(() => requests.value.reduce((n, q) => n + q.branches.length, 0));
const multiRepo = computed(() => (plan.value?.repos.length ?? 0) > 1);
const short = (sha: string | null | undefined) => (sha ? sha.slice(0, 10) : '—');
const baseOf = (repo: string) => plan.value?.repos.find((x) => x.repo === repo)?.baseBranch || null;

/** The plan's notes (what the core could not read, e.g. a member's clone), each with its repository. */
const notes = computed(() => (plan.value?.repos ?? []).flatMap((r) => (r.notes ?? []).map((text) => ({ repo: r.repo, text }))));
/** What merged and contained are measured against: one base for all, or each repository's. */
const baseText = computed(() => {
  const repos = (plan.value?.repos ?? []).filter((r) => r.baseBranch);
  if (!repos.length) return null;
  const bases = [...new Set(repos.map((r) => r.baseBranch))];
  return bases.length === 1
    ? `Merged and contained are measured against ${bases[0]}.`
    : `Merged and contained are measured against each repository's base: ${repos.map((r) => `${r.repo} → ${r.baseBranch}`).join(', ')}.`;
});
/** The heading over a row's commits: what they are for its class (none for classes that do not list commits). */
function commitsLabel(r: TidyRow): string | null {
  const base = baseOf(r.repo) ?? 'the base branch';
  switch (r.branch.class) {
    case 'moved': return `A realign drops these from the team's copy (not in ${r.branch.member ? `${r.branch.member}'s` : 'the'} clone):`;
    case 'own-work': case 'origin-only': return `Commits not in ${base}:`;
    default: return null;
  }
}

async function load(): Promise<TidyPlan | null> {
  error.value = null;
  try {
    const p = await api.call<TidyPlan>('git.tidyPlan', teamParams());
    plan.value = { repos: p?.repos ?? [] };
    selected.value = defaultSelection(plan.value);
    return plan.value;
  } catch (e) {
    error.value = errorText(e);
    return null;
  }
}

function toggle(key: string, on: boolean) {
  const s = new Set(selected.value);
  if (on) s.add(key); else s.delete(key);
  selected.value = s;
}

// ---- Delete selected: the core's plan for each repository first, then one yes with its tokens.
interface PlannedDelete { req: DeleteRequest; plan: GitDeletePlan }
const deleting = ref<{ plans: PlannedDelete[]; note: string | null; error: string | null; asked: Set<string> } | null>(null);
const deleteParams = (q: DeleteRequest, confirm?: string) => ({
  teamId: props.team.id, ...(q.repoParam ? { repo: q.repoParam } : {}), branches: q.branches, remote: q.remote, ...(confirm ? { confirm } : {}),
});

let planSeq = 0;
async function planDelete(note: string | null = null) {
  const reqs = requests.value;
  const seq = ++planSeq;
  const asked = new Set(selected.value);
  deleting.value = { plans: [], note, error: null, asked };
  realigning.value = null;
  report.value = null;
  busy.value = 'plan';
  try {
    const plans: PlannedDelete[] = [];
    for (const q of reqs) plans.push({ req: q, plan: await api.call<GitDeletePlan>('git.deleteBranches', deleteParams(q)) });
    if (deleting.value && seq === planSeq) deleting.value.plans = plans;
  } catch (e) {
    if (deleting.value) deleting.value.error = errorText(e);
  } finally {
    busy.value = null;
  }
}
const toDelete = computed(() => (deleting.value?.plans ?? []).filter((p) => !p.plan.empty));

async function confirmDelete() {
  const d = deleting.value;
  if (!d || !toDelete.value.length || busy.value) return;
  busy.value = 'delete';
  d.error = null;
  const done: string[] = [];
  let stale: string | null = null;
  try {
    for (const p of toDelete.value) {
      const r = await api.call<GitDeleteResult>('git.deleteBranches', deleteParams(p.req, p.plan.token));
      done.push(r.result);
    }
  } catch (e) {
    if (errorCode(e) === 'stale') stale = errorText(e);
    else d.error = errorText(e);
  } finally {
    busy.value = null;
  }
  if (done.length) emit('changed');
  if (stale) { await load(); await planDelete(stale); return; }
  if (d.error && !done.length) return;
  const failed = d.error;
  deleting.value = null;
  await afterAction(failed ? [...done, failed] : done, d.asked);
}

// ---- Realign a moved member branch to its clone's commit.
const realigning = ref<{ row: TidyRow; error: string | null; refused: boolean } | null>(null);
function startRealign(row: TidyRow) {
  if (busy.value) return;
  deleting.value = null;
  report.value = null;
  realigning.value = { row, error: null, refused: false };
}
async function confirmRealign() {
  const r = realigning.value;
  if (!r || busy.value) return;
  busy.value = 'realign';
  r.error = null;
  const b = r.row.branch;
  try {
    // The yes is for the two commits the confirm shows: the core refuses ("stale") if either moved since.
    const res = await api.call<BranchRealignResult>('git.branchRealign', {
      ...teamParams(), repo: r.row.repo, member: b.member,
      ...(b.teamSha ? { expectOld: b.teamSha } : {}), ...(b.cloneSha ? { expectNew: b.cloneSha } : {}),
    });
    realigning.value = null;
    emit('changed');
    await afterAction([`Realigned ${b.name}: ${short(res.oldSha)} → ${short(res.newSha)} (the old commit is recorded for recovery).`], new Set());
  } catch (e) {
    const code = errorCode(e);
    r.error = errorText(e);
    r.refused = (!!code && !NOT_REFUSALS.has(code)) || /running|runs/i.test(r.error);
    if (code === 'stale') await reshowRealign(r);
  } finally {
    busy.value = null;
  }
}

/** Error codes that are a failure, not the core saying no (those show as "Refused: <reason>"). */
const NOT_REFUSALS = new Set(['internal', 'method_not_found', 'bad_request', 'invalid_params', 'timeout', 'disconnected']);

/** After a "stale" refusal: read the plan again; if the branch is still moved, show the confirm with its new commits (a
 * new yes is bound to those), else close it and say what happened. The refusal stays shown either way. */
async function reshowRealign(r: { row: TidyRow; error: string | null }) {
  const p = await load();
  const again = groupTidy(p).flatMap((g) => g.rows).find((x) => x.key === r.row.key && x.branch.class === 'moved');
  if (again) { realigning.value = { row: again, error: r.error, refused: true }; return; }
  realigning.value = null;
  report.value = { done: [`Refused: ${r.error}`], kept: p ? keptReport(p, new Set()) : [] };
}

async function afterAction(done: string[], asked: Set<string>) {
  const p = await load();
  report.value = { done, kept: p ? keptReport(p, asked) : [] };
}

async function refresh() {
  if (busy.value) return;
  busy.value = 'refresh';
  report.value = null;
  deleting.value = null;
  realigning.value = null;
  try { await load(); } finally { busy.value = null; }
}

onMounted(() => { void load(); });
defineExpose({ load });
</script>

<template>
  <section class="tidy" data-test="tidy">
    <div class="head"><strong>Tidy branches</strong>
      <span class="muted small">every branch here, on origin and in the members' clones, in one list</span></div>
    <p v-if="error" class="error" data-test="tidy-error">{{ error }}</p>
    <LoadingNote v-if="!plan && !error" text="Planning the tidy…" />

    <section v-if="report" class="report" data-test="tidy-report">
      <p v-for="(d, i) in report.done" :key="`d${i}`" class="ok-line" data-test="tidy-done">{{ d }}</p>
      <template v-if="report.kept.length">
        <div class="small"><strong>Kept, and why</strong></div>
        <ul class="kept small"><li v-for="(k, i) in report.kept" :key="`k${i}`" data-test="tidy-kept">{{ k }}</li></ul>
      </template>
      <p v-else class="small muted" data-test="tidy-kept-none">Nothing left to tidy.</p>
    </section>

    <template v-if="plan">
      <ul v-if="notes.length" class="notes small" data-test="tidy-notes">
        <li v-for="(n, i) in notes" :key="`n${i}`" class="note" data-test="tidy-note"><template v-if="multiRepo">{{ n.repo }}: </template>{{ n.text }}</li>
      </ul>
      <p v-if="baseText" class="small muted base" data-test="tidy-base">{{ baseText }}</p>
      <p v-if="!groups.length" class="muted small" data-test="tidy-empty">No branches to tidy.</p>
      <section v-for="g in groups" :key="g.cls" class="group" :class="`cls-${g.cls}`" :data-test="`tidy-group-${g.cls}`">
        <div class="ghead"><strong>{{ g.label }}</strong> <span class="count">{{ g.rows.length }}</span></div>
        <div class="muted small">{{ g.why }}</div>
        <div v-for="r in g.rows" :key="r.key" class="trow" :data-test="`tidy-row-${r.branch.name}`">
          <div class="line">
            <input v-if="canDelete(r.branch)" type="checkbox" :checked="selected.has(r.key)" :disabled="!!busy"
              :aria-label="`Delete ${r.branch.name}`" data-test="tidy-select" @change="toggle(r.key, ($event.target as HTMLInputElement).checked)" />
            <code class="name">{{ r.branch.name }}</code>
            <span v-if="multiRepo" class="chip" data-test="tidy-repo">{{ r.repo }}</span>
            <span class="muted small">{{ whereText(r.branch.where) }}</span>
          </div>
          <div v-if="r.branch.class === 'working'" class="small muted">{{ r.branch.member ? `${r.branch.member}'s` : "The member's" }} working branch, not a leftover.</div>
          <div v-if="r.branch.class === 'moved'" class="small moved">
            <span data-test="tidy-moved-shas">team {{ short(r.branch.teamSha) }} · {{ r.branch.member ? `${r.branch.member}'s ` : '' }}clone {{ short(r.branch.cloneSha) }}</span>
            <button class="small-btn" data-test="tidy-realign" :disabled="!!busy" @click="startRealign(r)">Realign…</button>
          </div>
          <template v-if="r.branch.commits?.length && commitsLabel(r)">
            <div class="small muted">{{ commitsLabel(r) }}</div>
            <ul class="commits small" data-test="tidy-commits">
              <li v-for="c in r.branch.commits" :key="c.sha"><code>{{ short(c.sha) }}</code> {{ c.subject }}</li>
            </ul>
          </template>
          <div v-if="r.branch.proof && r.branch.class !== 'working'" class="small proof" :data-test="r.branch.class === 'contained' ? 'tidy-proof' : 'tidy-why'">{{ r.branch.proof }}</div>
        </div>
      </section>

      <section v-if="realigning" class="confirm-box" data-test="tidy-realign-confirm">
        <div><strong>Realign <code>{{ realigning.row.branch.name }}</code></strong></div>
        <div class="small">Set the team repository's<template v-if="realigning.row.branch.where.includes('origin')"> and origin's</template> copy to what
          {{ realigning.row.branch.member ?? 'the member' }}'s clone has (a forced update):</div>
        <div class="shas" data-test="tidy-realign-shas"><code>{{ short(realigning.row.branch.teamSha) }}</code> → <code>{{ short(realigning.row.branch.cloneSha) }}</code></div>
        <template v-if="realigning.row.branch.commits?.length">
          <div class="small">The team's copy drops these commits:</div>
          <ul class="commits small" data-test="tidy-realign-commits">
            <li v-for="c in realigning.row.branch.commits" :key="c.sha"><code>{{ short(c.sha) }}</code> {{ c.subject }}</li>
          </ul>
        </template>
        <div v-if="realigning.row.branch.proof" class="small proof" data-test="tidy-realign-proof">{{ realigning.row.branch.proof }}</div>
        <div class="muted small">The old commit is recorded so it can be recovered.</div>
        <p v-if="realigning.error" class="error" data-test="tidy-realign-error">
          <template v-if="realigning.refused">Refused: </template>{{ realigning.error }}</p>
        <div class="row actions">
          <button class="danger" data-test="tidy-realign-yes" :disabled="!!busy" @click="confirmRealign"><BusyLabel :busy="busy === 'realign'" text="Realigning…">Realign</BusyLabel></button>
          <button data-test="tidy-realign-cancel" :disabled="busy === 'realign'" @click="realigning = null">Cancel</button>
        </div>
      </section>

      <section v-if="deleting" class="confirm-box" data-test="tidy-delete-confirm">
        <p v-if="deleting.note" class="small note" data-test="tidy-delete-stale">{{ deleting.note }} Planned again below.</p>
        <LoadingNote v-if="busy === 'plan'" text="Planning the delete…" />
        <template v-for="p in deleting.plans" :key="`${p.req.repo}:${p.req.remote}`">
          <div v-if="multiRepo" class="small"><strong>{{ p.req.repo }}</strong></div>
          <pre class="plan" data-test="tidy-delete-plan">{{ p.plan.plan }}</pre>
        </template>
        <p v-if="busy !== 'plan' && deleting.plans.length && !toDelete.length" class="muted small" data-test="tidy-delete-empty">Nothing to delete.</p>
        <p v-if="deleting.error" class="error" data-test="tidy-delete-error">{{ deleting.error }}</p>
        <div class="row actions">
          <button v-if="toDelete.length" class="danger" data-test="tidy-delete-yes" :disabled="!!busy" @click="confirmDelete">
            <BusyLabel :busy="busy === 'delete'" text="Deleting…">Delete</BusyLabel></button>
          <button data-test="tidy-delete-cancel" :disabled="busy === 'delete'" @click="deleting = null">{{ toDelete.length ? 'Cancel' : 'Close' }}</button>
        </div>
      </section>

      <div class="row actions">
        <button class="danger" data-test="tidy-delete-selected" :disabled="!!busy || !selectedCount || !!deleting" @click="planDelete()">
          <BusyLabel :busy="busy === 'plan'" text="Planning…">Delete selected ({{ selectedCount }})…</BusyLabel></button>
        <button data-test="tidy-refresh" :disabled="!!busy" @click="refresh"><BusyLabel :busy="busy === 'refresh'" text="Reading…">Read again</BusyLabel></button>
        <button data-test="tidy-close" :disabled="!!busy" @click="emit('close')">Close</button>
      </div>
    </template>
  </section>
</template>

<style scoped>
.tidy { display: flex; flex-direction: column; gap: 6px; padding: 8px 10px; border-radius: 6px; background: var(--chip); margin: 8px 0; min-width: 0; }
.head { display: flex; flex-wrap: wrap; gap: 6px; align-items: baseline; }
.group { padding: 6px 0; border-top: 1px solid var(--line); min-width: 0; }
.ghead .count { font-size: 11px; padding: 0 6px; border-radius: 8px; background: var(--bg); }
.trow { padding: 4px 0 4px 6px; min-width: 0; }
.line { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; min-width: 0; }
.name, .proof, .commits, .shas, .kept { overflow-wrap: anywhere; word-break: break-word; min-width: 0; }
.proof { color: var(--ink-soft); padding-left: 2px; }
.moved { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.commits { margin: 2px 0 0 0; padding-left: 18px; }
.chip { font-size: 11px; padding: 0 5px; border-radius: 8px; background: var(--bg); }
.confirm-box { padding: 8px 10px; border-radius: 6px; border-left: 3px solid var(--state-failed); background: var(--bg); min-width: 0; }
.confirm-box .plan { white-space: pre-wrap; word-break: break-word; background: var(--chip); border-radius: 4px; padding: 6px 8px; margin: 6px 0; font-size: 12.5px; max-height: 240px; overflow: auto; }
.note { color: var(--warn-ink); overflow-wrap: anywhere; }
.notes { margin: 0; padding-left: 18px; min-width: 0; }
.base { margin: 0; }
.report { padding: 6px 8px; border-left: 3px solid var(--ok-rule); background: var(--bg); min-width: 0; }
.ok-line { margin: 0 0 4px 0; overflow-wrap: anywhere; }
.kept { margin: 2px 0 0 0; padding-left: 18px; }
.actions { gap: 8px; flex-wrap: wrap; margin: 4px 0; align-items: center; }
.small-btn { font-size: 12px; padding: 1px 8px; }
</style>
