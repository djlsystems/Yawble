<script setup lang="ts">
import { discussWithConcierge, syncNotePrompt } from '../conciergeDiscuss';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { GIT_COMMIT_RANGE } from '../../../shared/api';
import type { BranchSyncResult, CommitRange, CommitRow, YawbleApi, TeamGitStatus, TeamSummary, TeamSyncState } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { commitsInHeading } from '../gitRange';
import BusyLabel from './BusyLabel.vue';
import CommitList from './CommitList.vue';

// On the team page: how far the team branch is behind its base, with Sync now (the same sync as before new work:
// merges in the team's repository, never a rebase or a force; a conflict changes nothing), and the note of a sync or a
// fetch-back that could not be done, with the conflicting files. Read from teams.syncToBase (no fetch, read-only git).
const props = defineProps<{ team: TeamSummary; api?: YawbleApi; pollMs?: number }>();
const api = resolveApi(props.api);

const state = ref<TeamSyncState | null>(null);
const error = ref<string | null>(null);
const done = ref<string | null>(null);
const busy = ref(false);
// The commits Sync now brought into the team branch (the core's incoming; empty or absent: none listed).
const cameIn = ref<{ heading: string; commits: CommitRow[]; total: number; hasOrigin: boolean } | null>(null);
// "N commits behind" opens those commits (git.commitRange with the pair teams.syncToBase gave; an older core
// sends none and the count stays text). The same link again closes the list.
const range = ref<{ data: CommitRange | null; error: string | null } | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;

async function load() {
  try { state.value = await api.call<TeamSyncState>('teams.syncToBase', { teamId: props.team.id }); } catch { state.value = null; }
}

async function syncNow() {
  if (busy.value) return;
  busy.value = true;
  error.value = null;
  done.value = null;
  cameIn.value = null;
  range.value = null;
  try {
    const r = await api.call<BranchSyncResult>('git.sync', { teamId: props.team.id });
    const team = state.value?.behind?.teamBranch;
    const total = r.incomingCount ?? r.incoming?.length ?? 0;
    if (r.incoming?.length && team) {
      // F4b: the not-pushed mark as in the Git dialog - only when the repository has an origin (git.status origin; neither
      // teams.syncToBase nor git.sync says). Unreadable status: no mark.
      let hasOrigin = false;
      try { hasOrigin = !!(await api.call<TeamGitStatus>('git.status', { teamId: props.team.id }))?.origin; } catch { /* no mark */ }
      cameIn.value = { heading: commitsInHeading(total, team), commits: r.incoming, total, hasOrigin };
    }
    done.value = r.outcome === 'synced' ? `Brought the branches up to ${r.baseBranch}${r.moved.length ? ` (${r.moved.join(', ')})` : ''}.`
      : r.outcome === 'conflict' ? null : `Already up to ${r.baseBranch}.`;
    await load();
  } catch (e) { error.value = errorText(e); } finally { busy.value = false; }
}

async function toggleRange() {
  const b = state.value?.behind;
  if (range.value) { range.value = null; return; }
  if (!b?.from || !b.to) return;
  range.value = { data: null, error: null };
  const cur = range.value;
  try {
    const d = await api.call<CommitRange>(GIT_COMMIT_RANGE, { teamId: props.team.id, from: b.from, to: b.to });
    if (range.value === cur) cur.data = { ...d, commits: d?.commits ?? [] };
  } catch (e) { if (range.value === cur) cur.error = errorText(e); }
}

// The note is one line per problem; each names its files after "conflicts in:".
const lines = (note: string) => note.split('\n').map((l) => l.trim()).filter(Boolean);
const files = (line: string) => /conflicts in: ([^)]*)\)/.exec(line)?.[1].split(',').map((f) => f.trim()).filter(Boolean) ?? [];

onMounted(() => { void load(); timer = setInterval(load, props.pollMs ?? 60000); });
watch(() => props.team.id, () => { done.value = null; error.value = null; cameIn.value = null; range.value = null; void load(); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
defineExpose({ load });
</script>

<template>
  <div v-if="state && (state.note || state.behind || done || error)" class="sync" data-test="team-sync">
    <div v-if="state.note" class="note" data-test="team-sync-note" role="alert">
      <div class="head">
        <strong>Needs you: the branches could not be brought up automatically.</strong>
        <button class="discuss" data-test="team-sync-discuss" title="Talk it through with the Concierge"
          @click="discussWithConcierge(team.id, syncNotePrompt(team.name, state.note))">Discuss with the Concierge</button>
      </div>
      <div v-for="(l, i) in lines(state.note)" :key="i" class="line" data-test="team-sync-note-line">
        <span>{{ l }}</span>
        <ul v-if="files(l).length" class="files" data-test="team-sync-files">
          <li v-for="f in files(l)" :key="f"><code>{{ f }}</code></li>
        </ul>
      </div>
    </div>
    <p v-if="state.behind" class="behind small" data-test="team-behind">
      <button v-if="state.behind.from && state.behind.to" type="button" class="count" data-test="team-behind-count"
        :aria-expanded="!!range" title="Show these commits" @click="toggleRange">{{ state.behind.text }}</button>
      <template v-else>{{ state.behind.text }}</template><span v-if="!state.on" class="muted"> (bringing branches up before new work is off for this team)</span>
      <button data-test="team-sync-now" :disabled="busy" title="Merge the base into the team and member branches (never a rebase; a conflict changes nothing)"
        :aria-busy="busy" @click="syncNow"><BusyLabel :busy="busy" text="Syncing…">Sync now</BusyLabel></button>
    </p>
    <div v-if="state.behind && range" class="list" data-test="team-behind-list">
      <p v-if="range.error" class="error small">{{ range.error }}</p>
      <p v-else-if="!range.data" class="muted small">Reading the commits…</p>
      <CommitList v-else :team="team.name" :commits="range.data.commits" :total="range.data.count" :has-origin="range.data.hasOrigin"
        empty="No commits: the team branch is level with the base now." />
    </div>
    <p v-if="done" class="muted small" data-test="team-sync-done">{{ done }}</p>
    <div v-if="cameIn" class="list" data-test="team-sync-came-in">
      <p class="small">{{ cameIn.heading }}</p>
      <CommitList :team="team.name" :commits="cameIn.commits" :total="cameIn.total" :has-origin="cameIn.hasOrigin" />
    </div>
    <p v-if="error" class="error small" data-test="team-sync-error">{{ error }}</p>
  </div>
</template>

<style scoped>
.sync { margin: 6px 0; }
.note { border: 1px solid var(--warn); border-radius: 6px; padding: 8px 10px; background: var(--warn-bg); }
.note .line { margin-top: 4px; }
.note .head { display: flex; gap: 10px; align-items: flex-start; justify-content: space-between; }
.discuss { flex: none; font-size: 12px; }
.files { margin: 2px 0 0 18px; padding: 0; }
.behind { display: flex; gap: 8px; align-items: center; margin: 4px 0; }
/* The count is a link, as in the Git dialog. */
.count { all: unset; cursor: pointer; color: var(--accent); text-decoration: underline; text-underline-offset: 2px; border-radius: 3px; }
.count:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.list { margin: 2px 0 6px; }
/* A phone: the heading over its button, the text over Sync now; tap targets 44 px. */
@media (max-width: 759.98px) {
  .note .head { flex-wrap: wrap; }
  .sync button { min-height: 44px; }
  /* On a phone "N commits behind" is one compact row, the text beside Sync now (the off-for-this-team aside left out). */
  .sync { margin: 4px 0; }
  .behind { flex-wrap: nowrap; justify-content: space-between; margin: 0; min-height: 44px; }
  .behind > .muted { display: none; }
  .behind button { flex: none; }
  .behind button.count { flex: 1 1 auto; min-height: 44px; display: flex; align-items: center; }
}
</style>
