<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { YawbleApi } from '../../../../shared/api';
import { errorText } from '../../api';
import { runScript, scriptsApi } from '../../scripts/api';
import type { Run, RunDetail } from '../../scripts/types';
import LoadingNote from '../LoadingNote.vue';
import ScriptRunView from './ScriptRunView.vue';

// The Runs tab (B134): every script run across the teams (scripts.runs with no teamId), newest first: team, member,
// script@version, trigger, status and start. A row opens its run view (scripts.run).
const props = defineProps<{ api: YawbleApi }>();
const s = scriptsApi(props.api);

const runs = ref<Run[] | null>(null);
const error = ref<string | null>(null);
const open = ref<RunDetail | null>(null);
const openError = ref<string | null>(null);

async function refresh() {
  try { runs.value = await s.runs({}); error.value = null; } catch (e) { error.value = errorText(e); }
}
onMounted(refresh);

async function show(r: Run) {
  openError.value = null;
  try { open.value = await s.run(r.id); } catch (e) { openError.value = errorText(e); }
}
</script>

<template>
  <div class="runs" data-test="runs-tab">
    <template v-if="open">
      <div class="row"><button type="button" class="ghost" data-test="run-back" @click="open = null">← Runs</button></div>
      <ScriptRunView :run="open" :api="api" />
    </template>
    <template v-else>
      <div class="row"><span class="muted grow">Every script run, across the teams. Test runs are marked.</span><button type="button" class="mini" data-test="runs-refresh" @click="refresh">Refresh</button></div>
      <p v-if="error" class="error">{{ error }}</p>
      <p v-if="openError" class="error" data-test="run-open-error">{{ openError }}</p>
      <LoadingNote v-if="runs === null && !error" text="Loading runs…" />
      <p v-else-if="runs && !runs.length" class="muted" data-test="runs-empty">No script has run yet.</p>
      <table v-else-if="runs" class="table">
        <thead><tr><th>Team</th><th>Member</th><th>Script</th><th>Trigger</th><th>Status</th><th>Started</th></tr></thead>
        <tbody>
          <tr v-for="r in runs" :key="r.id" class="clickable" tabindex="0" data-test="run-row" :data-id="r.id" @click="show(r)" @keydown.enter="show(r)">
            <td>{{ r.team }}</td>
            <td>{{ r.member ?? '' }}<span v-if="!r.member" class="muted">(test)</span></td>
            <td><code>{{ runScript(r) }}</code></td>
            <td>{{ r.trigger }}<span v-if="r.dryRun" class="chip">dry run</span></td>
            <td><span class="status" :data-status="r.status">{{ r.status }}</span></td>
            <td class="muted">{{ new Date(r.startedAt).toLocaleString() }}</td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.runs { display: flex; flex-direction: column; gap: 8px; }
.table { border-collapse: collapse; width: 100%; font-size: 13px; }
.table th { text-align: left; font-weight: 500; color: var(--muted); font-size: 12px; border-bottom: 1px solid var(--line); padding: 4px 6px; }
.table td { padding: 5px 6px; border-bottom: 1px solid var(--line-soft); }
.clickable { cursor: pointer; }
.clickable:hover td { background: var(--hover); }
.ghost { border: 0; background: transparent; color: var(--accent); padding: 4px 6px; }
.mini { font-size: 12px; padding: 2px 8px; }
.status { font-size: 11.5px; padding: 1px 7px; border-radius: 9px; background: var(--pill-neutral-bg); color: var(--pill-neutral-ink); }
.status[data-status=succeeded] { background: var(--pill-green-bg); color: var(--pill-green-ink); }
.status[data-status=failed] { background: var(--pill-red-bg); color: var(--pill-red-ink); }
.status[data-status=running] { background: var(--pill-blue-bg); color: var(--pill-blue-ink); }
.status[data-status=skipped], .status[data-status=interrupted] { background: var(--pill-yellow-bg); color: var(--pill-yellow-ink); }
</style>
