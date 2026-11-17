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
  try {
    const res = await api.call<BranchRealignResult>('git.branchRealign', { ...teamParams(), repo: r.row.repo, member: r.row.branch.member });
    realigning.value = null;
    emit('changed');
    await afterAction([`Realigned ${r.row.branch.name}: ${short(res.oldSha)} → ${short(res.newSha)} (the old commit is recorded for recovery).`], new Set());
  } catch (e) {
    r.error = errorText(e);
    r.refused = errorCode(e) === 'running' || /running|runs/i.test(r.error);
  } finally {
    busy.value = null;
  }
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
          <div v-if="r.branch.class === 'contained' && r.branch.proof" class="small proof" data-test="tidy-proof">{{ r.branch.proof }}</div>
          <div v-if="r.branch.class === 'working'" class="small muted">{{ r.branch.member ? `${r.branch.member}'s` : "The member's" }} working branch, not a leftover.</div>
          <div v-if="r.branch.class === 'moved'" class="small moved">
            <span data-test="tidy-moved-shas">team {{ short(r.branch.teamSha) }} · {{ r.branch.member ? `${r.branch.member}'s ` : '' }}clone {{ short(r.branch.cloneSha) }}</span>
            <button class="small-btn" data-test="tidy-realign" :disabled="!!busy" @click="startRealign(r)">Realign…</button>
          </div>
          <ul v-if="r.branch.class === 'own-work' && r.branch.commits?.length" class="commits small" data-test="tidy-commits">
            <li v-for="c in r.branch.commits" :key="c.sha"><code>{{ short(c.sha) }}</code> {{ c.subject }}</li>
          </ul>
        </div>
      </section>

      <section v-if="realigning" class="confirm-box" data-test="tidy-realign-confirm">
        <div><strong>Realign <code>{{ realigning.row.branch.name }}</code></strong></div>
        <div class="small">Set the team repository's<template v-if="realigning.row.branch.where.includes('origin')"> and origin's</template> copy to what
          {{ realigning.row.branch.member ?? 'the member' }}'s clone has (a forced update):</div>
        <div class="shas" data-test="tidy-realign-shas"><code>{{ short(realigning.row.branch.teamSha) }}</code> → <code>{{ short(realigning.row.branch.cloneSha) }}</code></div>
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
.note { color: var(--warn-ink); }
.report { padding: 6px 8px; border-left: 3px solid var(--ok-rule); background: var(--bg); min-width: 0; }
.ok-line { margin: 0 0 4px 0; overflow-wrap: anywhere; }
.kept { margin: 2px 0 0 0; padding-left: 18px; }
.actions { gap: 8px; flex-wrap: wrap; margin: 4px 0; align-items: center; }
.small-btn { font-size: 12px; padding: 1px 8px; }
</style>
