<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { BranchSyncResult, YawbleApi, TeamSummary, TeamSyncState } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import BusyLabel from './BusyLabel.vue';

// On the team page: how far the team branch is behind its base, with Sync now (the same sync as before new work:
// merges in the team's repository, never a rebase or a force; a conflict changes nothing), and the note of a sync or a
// fetch-back that could not be done, with the conflicting files. Read from teams.syncToBase (no fetch, read-only git).
const props = defineProps<{ team: TeamSummary; api?: YawbleApi; pollMs?: number }>();
const api = resolveApi(props.api);

const state = ref<TeamSyncState | null>(null);
const error = ref<string | null>(null);
const done = ref<string | null>(null);
const busy = ref(false);
let timer: ReturnType<typeof setInterval> | null = null;

async function load() {
  try { state.value = await api.call<TeamSyncState>('teams.syncToBase', { teamId: props.team.id }); } catch { state.value = null; }
}

async function syncNow() {
  if (busy.value) return;
  busy.value = true;
  error.value = null;
  done.value = null;
  try {
    const r = await api.call<BranchSyncResult>('git.sync', { teamId: props.team.id });
    done.value = r.outcome === 'synced' ? `Brought the branches up to ${r.baseBranch}${r.moved.length ? ` (${r.moved.join(', ')})` : ''}.`
      : r.outcome === 'conflict' ? null : `Already up to ${r.baseBranch}.`;
    await load();
  } catch (e) { error.value = errorText(e); } finally { busy.value = false; }
}

// The note is one line per problem; each names its files after "conflicts in:".
const lines = (note: string) => note.split('\n').map((l) => l.trim()).filter(Boolean);
const files = (line: string) => /conflicts in: ([^)]*)\)/.exec(line)?.[1].split(',').map((f) => f.trim()).filter(Boolean) ?? [];

onMounted(() => { void load(); timer = setInterval(load, props.pollMs ?? 60000); });
watch(() => props.team.id, () => { done.value = null; error.value = null; void load(); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });
defineExpose({ load });
</script>

<template>
  <div v-if="state && (state.note || state.behind || done || error)" class="sync" data-test="team-sync">
    <div v-if="state.note" class="note" data-test="team-sync-note" role="alert">
      <strong>Needs you: the branches could not be brought up automatically.</strong>
      <div v-for="(l, i) in lines(state.note)" :key="i" class="line" data-test="team-sync-note-line">
        <span>{{ l }}</span>
        <ul v-if="files(l).length" class="files" data-test="team-sync-files">
          <li v-for="f in files(l)" :key="f"><code>{{ f }}</code></li>
        </ul>
      </div>
    </div>
    <p v-if="state.behind" class="behind small" data-test="team-behind">
      {{ state.behind.text }}<span v-if="!state.on" class="muted"> (bringing branches up before new work is off for this team)</span>
      <button data-test="team-sync-now" :disabled="busy" title="Merge the base into the team and member branches (never a rebase; a conflict changes nothing)"
        :aria-busy="busy" @click="syncNow"><BusyLabel :busy="busy" text="Syncing…">Sync now</BusyLabel></button>
    </p>
    <p v-if="done" class="muted small" data-test="team-sync-done">{{ done }}</p>
    <p v-if="error" class="error small" data-test="team-sync-error">{{ error }}</p>
  </div>
</template>

<style scoped>
.sync { margin: 6px 0; }
.note { border: 1px solid var(--warn, #c80); border-radius: 6px; padding: 8px 10px; background: var(--warn-bg, rgba(204, 136, 0, 0.08)); }
.note .line { margin-top: 4px; }
.files { margin: 2px 0 0 18px; padding: 0; }
.behind { display: flex; gap: 8px; align-items: center; margin: 4px 0; }
</style>
