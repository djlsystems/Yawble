<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { IntegrationInfo, YawbleApi, TeamSummary, WatchInfo } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

/**
 * A team's watches: an integration's read-only command looked at every few minutes on this
 * computer (no model run); each new item in its JSON output becomes an event, which the team's event triggers act on.
 */
const props = defineProps<{ api?: YawbleApi; team: TeamSummary }>();
const api = resolveApi(props.api);

const watches = ref<WatchInfo[]>([]);
const clis = ref<string[]>([]);
const error = ref<string | null>(null);
const adding = ref(false);
const loaded = ref(false);
const work = useBusy();
const form = ref({ name: '', integration: 'gh', args: 'pr list --state open --json number,title,url,author', keyField: 'number', eventName: '', minutes: 5 });

async function load() {
  try {
    watches.value = await api.call<WatchInfo[]>('integrations.watches', { teamId: props.team.id });
    const list = (await api.call<{ integrations: IntegrationInfo[] }>('integrations.list', {})).integrations;
    clis.value = list.filter((i) => i.all || i.teams.includes(props.team.id)).map((i) => i.name);
    if (!clis.value.includes(form.value.integration) && clis.value.length) form.value.integration = clis.value[0];
    error.value = null;
  } catch (e) { error.value = errorText(e); } finally { loaded.value = true; }
}

/** Splits the command line into arguments, keeping "quoted parts" together (no shell is ever involved). */
function split(line: string): string[] {
  return (line.match(/"[^"]*"|\S+/g) ?? []).map((a) => a.replace(/^"(.*)"$/, '$1'));
}

const save = () => work.run('save', doSave);
async function doSave() {
  error.value = null;
  try {
    await api.call('integrations.saveWatch', {
      teamId: props.team.id, name: form.value.name.trim(), integration: form.value.integration, args: split(form.value.args),
      keyField: form.value.keyField.trim(), eventName: form.value.eventName.trim() || `${props.team.name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}.${form.value.name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-')}`,
      intervalSec: Math.max(60, Math.round(form.value.minutes * 60)),
    });
    adding.value = false;
    await load();
  } catch (e) { error.value = errorText(e); }
}
const remove = (id: number) => work.run(`watch-${id}`, async () => { try { await api.call('integrations.deleteWatch', { id }); await load(); } catch (e) { error.value = errorText(e); } }, 'remove');
const lookNow = (id: number) => work.run(`watch-${id}`, async () => { try { await api.call('integrations.runWatch', { id }); await load(); } catch (e) { error.value = errorText(e); } }, 'look');

onMounted(load);
</script>

<template>
  <section class="watches" data-test="watches">
    <h3>Watches <span class="muted small">(no model run: a command looked at every few minutes; each new item is an event)</span></h3>
    <p v-if="error" class="error" data-test="watches-error">{{ error }}</p>
    <LoadingNote v-if="!loaded" text="Loading watches…" />
    <p v-else-if="!clis.length" class="muted small">Enable an integration for this team (Settings › Integrations) to watch it.</p>
    <ul class="list">
      <li v-for="w in watches" :key="w.id" :data-test="`watch-${w.id}`">
        <b>{{ w.name }}</b> <span class="mono small">{{ w.integration }} {{ w.args.join(' ') }}</span>
        <span class="muted small">· every {{ Math.round(w.intervalSec / 60) }} min · new {{ w.keyField }} → event <span class="mono">{{ w.eventName }}</span></span>
        <span v-if="w.lastOutcome" class="small" :class="w.lastOutcome === 'failed' || w.lastOutcome === 'skipped' ? 'warn' : 'ok'">· {{ w.lastOutcome }}<template v-if="w.lastReason">: {{ w.lastReason }}</template></span>
        <button class="mini" :title="work.is(`watch-${w.id}`, 'look') ? 'Looking…' : 'Look now'" :disabled="work.is(`watch-${w.id}`)" :aria-busy="work.is(`watch-${w.id}`, 'look')" @click="lookNow(w.id)"><BusyLabel :busy="work.is(`watch-${w.id}`, 'look')">↻</BusyLabel></button>
        <button class="mini" :title="work.is(`watch-${w.id}`, 'remove') ? 'Removing…' : 'Remove'" :disabled="work.is(`watch-${w.id}`)" :aria-busy="work.is(`watch-${w.id}`, 'remove')" @click="remove(w.id)"><BusyLabel :busy="work.is(`watch-${w.id}`, 'remove')">✕</BusyLabel></button>
      </li>
    </ul>
    <button v-if="!adding && clis.length" data-test="watch-new" @click="adding = true">+ New watch</button>
    <div v-if="adding" class="form">
      <div class="row">
        <label>Name <input v-model="form.name" placeholder="open PRs" data-test="watch-name" /></label>
        <label>Integration <select v-model="form.integration"><option v-for="c in clis" :key="c" :value="c">{{ c }}</option></select></label>
        <label>Every <input v-model.number="form.minutes" type="number" min="1" class="num" /> min</label>
      </div>
      <label>Command (its JSON output is a list of items) <input v-model="form.args" class="mono" data-test="watch-args" /></label>
      <div class="row">
        <label>Field that names an item <input v-model="form.keyField" class="mono" placeholder="number" data-test="watch-key" /></label>
        <label class="grow">Event name <input v-model="form.eventName" class="mono" placeholder="web.pr (blank: team.name)" data-test="watch-event" /></label>
      </div>
      <p class="muted small">The command must be allowed for this team without asking (a watch runs with nobody there). Then add an Event trigger above
        with this event's name to have an agent act on each new item.</p>
      <div class="row"><span class="grow" /><button :disabled="work.is('save')" @click="adding = false">Cancel</button><button class="primary" :disabled="!form.name.trim() || work.is('save')" data-test="watch-save" :aria-busy="work.is('save')" @click="save"><BusyLabel :busy="work.is('save')" text="Saving…">Save</BusyLabel></button></div>
    </div>
  </section>
</template>

<style scoped>
.watches { border-top: 1px solid var(--line); margin-top: 14px; padding-top: 10px; }
h3 { margin: 0 0 6px; font-size: 14px; }
.list { list-style: none; padding: 0; margin: 0 0 8px; display: flex; flex-direction: column; gap: 4px; }
.form { display: flex; flex-direction: column; gap: 6px; border: 1px dashed var(--line); border-radius: 8px; padding: 8px 10px; }
.form label { display: flex; flex-direction: column; gap: 2px; font-size: 12px; color: var(--ink-soft); }
.row { display: flex; gap: 10px; align-items: flex-end; flex-wrap: wrap; }
.grow { flex: 1; }
input, select { font: inherit; font-size: 13px; padding: 4px 7px; border: 1px solid var(--line); border-radius: 6px; }
.num { width: 70px; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.muted { color: var(--muted); font-weight: 400; }
.small { font-size: 12px; }
.ok { color: var(--ok); } .warn { color: var(--warn); }
.mini { border: 0; background: transparent; padding: 0 4px; }
.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
.error { color: var(--bad); }
</style>
