<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { CompanionAction, CompanionActionStatus, CompanionLevel, CompanionList, CompanionPairing, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { COMPANION_LEVEL_TEXT, levelsUpTo } from '../companionLevels';
import BusyLabel from './BusyLabel.vue';

/**
 * yawble-companion's asks, on the main window: a program that wants to pair (with the code its terminal shows), and
 * each destructive call a paired program made, which runs only when the person approves it here. Plans and results are
 * agent-influenced text: always shown as plain text ({{ }} / <pre>), never as HTML. Nothing in a remote view: the
 * companion is managed only at this computer.
 */
const props = defineProps<{ api: YawbleApi; pollMs?: number }>();

const pairings = ref<CompanionPairing[]>([]);
const actions = ref<CompanionAction[]>([]);
const chosen = ref<Record<string, CompanionLevel>>({});
const outcomes = ref<CompanionActionStatus[]>([]);
const deciding = ref<Set<string>>(new Set());
/** Which answer was clicked on each ask being decided (that button shows the spinner). */
const answering = ref<Record<string, 'approve' | 'deny'>>({});
/** Requests this window approved whose run was still under way: their outcome is fetched once the list no longer shows them running. */
const awaiting = ref<Set<string>>(new Set());
const error = ref<string | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;
let off: (() => void) | null = null;

async function load() {
  try {
    const l = await props.api.call<CompanionList>('companion.list', {});
    pairings.value = l.pairings ?? [];
    actions.value = l.actions ?? [];
    await settle();
  } catch {
    // An older core has no companion, or the core is down (the header says so): nothing to ask.
    pairings.value = [];
    actions.value = [];
  }
}

function levelFor(p: CompanionPairing): CompanionLevel { return chosen.value[p.id] ?? p.level; }

async function decide(id: string, run: () => Promise<void>, how: 'approve' | 'deny') {
  if (deciding.value.has(id)) return;
  error.value = null;
  deciding.value = new Set([...deciding.value, id]);
  answering.value = { ...answering.value, [id]: how };
  try { await run(); } catch (e) { error.value = errorText(e); }
  finally {
    const next = new Set(deciding.value); next.delete(id); deciding.value = next;
    const { [id]: _, ...rest } = answering.value; answering.value = rest;
    await load();
  }
}

function decidePairing(p: CompanionPairing, approve: boolean) {
  return decide(p.id, async () => {
    await props.api.call('companion.decidePairing', approve ? { id: p.id, approve: true, level: levelFor(p) } : { id: p.id, approve: false });
  }, approve ? 'approve' : 'deny');
}

/**
 * A decision's answer: an outcome card once it is final. A run still under way (running) is shown by the list and
 * awaited; a pending answer is left to the list (it is still the person's to decide) and never awaited.
 */
function record(id: string, s: CompanionActionStatus) {
  const rest = new Set(awaiting.value); rest.delete(id);
  if (s.state === 'running') rest.add(id);
  awaiting.value = rest;
  if (s.state === 'running' || s.state === 'pending') return;
  outcomes.value = [{ ...s, id: s.id ?? id }, ...outcomes.value.filter((o) => o.id !== id)];
}

/**
 * The final outcome of each run this window started, once the list no longer shows it running (companion.changed says
 * when), read with companion.actionStatus: a read only, it never decides anything.
 */
async function settle() {
  for (const id of [...awaiting.value]) {
    if (actions.value.some((a) => a.id === id && a.state === 'running')) continue;
    try { record(id, await props.api.call<CompanionActionStatus>('companion.actionStatus', { id })); }
    catch { const rest = new Set(awaiting.value); rest.delete(id); awaiting.value = rest; } // forgotten by the core: nothing to show
  }
}

function decideAction(a: CompanionAction, approve: boolean) {
  return decide(a.id, async () => {
    record(a.id, await props.api.call<CompanionActionStatus>('companion.decideAction', { id: a.id, approve }));
  }, approve ? 'approve' : 'deny');
}

function dismiss(id: string) { outcomes.value = outcomes.value.filter((o) => o.id !== id); }

const OUTCOME_TEXT: Record<CompanionActionStatus['state'], string> = {
  done: 'Done.',
  failed: 'It ran and failed.',
  denied: 'Denied. Nothing ran.',
  expired: 'This request expired before it was decided, so nothing ran.',
  changed: 'What this request would change has moved on since it was made, so nothing ran. A new request with the current list is waiting for you.',
  pending: 'Still waiting.',
  running: 'Running…',
};

onMounted(() => {
  if (props.api.remote) return;
  void load();
  off = props.api.onEvent((e) => { if (e.event === 'companion.changed') void load(); });
  timer = setInterval(load, props.pollMs ?? 3000);
});
onBeforeUnmount(() => { off?.(); if (timer) clearInterval(timer); });
</script>

<template>
  <section v-if="!api.remote && (pairings.length || actions.length || outcomes.length || error)" class="companion-asks" data-test="companion-asks">
    <p v-if="error" class="error" data-test="companion-error">{{ error }}</p>
    <article v-for="p in pairings" :key="p.id" class="card" data-test="companion-pairing">
      <h3><b>{{ p.name }}</b> wants to connect to Yawble</h3>
      <p>Approve only if the code below matches the one your terminal shows: <code class="code" data-test="companion-code">{{ p.code }}</code></p>
      <label class="level">
        <span>Allow</span>
        <select :value="levelFor(p)" :disabled="deciding.has(p.id)" @change="chosen = { ...chosen, [p.id]: ($event.target as HTMLSelectElement).value as CompanionLevel }">
          <option v-for="l in levelsUpTo(p.level)" :key="l" :value="l">{{ l }}</option>
        </select>
      </label>
      <p class="muted" data-test="companion-level-description">{{ COMPANION_LEVEL_TEXT[levelFor(p)] }}</p>
      <div class="buttons">
        <button class="primary" :disabled="deciding.has(p.id)" data-test="companion-approve" @click="decidePairing(p, true)"><BusyLabel :busy="answering[p.id] === 'approve'" text="Approving…">Approve</BusyLabel></button>
        <button :disabled="deciding.has(p.id)" data-test="companion-deny" @click="decidePairing(p, false)"><BusyLabel :busy="answering[p.id] === 'deny'" text="Denying…">Deny</BusyLabel></button>
      </div>
    </article>
    <article v-for="a in actions" :key="a.id" class="card" data-test="companion-action" :data-state="a.state">
      <h3><b>{{ a.client }}</b> asks to run <code>{{ a.tool }}</code></h3>
      <p v-if="a.state === 'running'" class="muted" data-test="companion-running">Running now…</p>
      <p v-else class="muted">This runs exactly what is listed, once, only if you approve it.</p>
      <pre class="plan" data-test="companion-plan">{{ a.plan }}</pre>
      <div class="buttons">
        <button class="danger" :disabled="a.state === 'running' || deciding.has(a.id)" data-test="companion-approve" @click="decideAction(a, true)"><BusyLabel :busy="answering[a.id] === 'approve'" text="Approving…">Approve</BusyLabel></button>
        <button :disabled="a.state === 'running' || deciding.has(a.id)" data-test="companion-deny" @click="decideAction(a, false)"><BusyLabel :busy="answering[a.id] === 'deny'" text="Denying…">Deny</BusyLabel></button>
      </div>
    </article>
    <article v-for="o in outcomes" :key="`o-${o.id}`" class="card outcome" data-test="companion-outcome" :data-state="o.state">
      <p><b>{{ OUTCOME_TEXT[o.state] ?? o.state }}</b></p>
      <p v-if="o.state === 'changed' && o.nextId" data-test="companion-next">New request {{ o.nextId }} is waiting below.</p>
      <pre v-if="o.result" class="plan" data-test="companion-result">{{ o.result }}</pre>
      <button class="ghost" data-test="companion-outcome-dismiss" @click="dismiss(o.id)">Dismiss</button>
    </article>
  </section>
</template>

<style scoped>
.companion-asks { display: flex; flex-direction: column; gap: 8px; padding: 8px 18px; border-bottom: 1px solid var(--line); border-left: 3px solid var(--warn); background: var(--side); }
.card { border: 1px solid var(--line); border-radius: 6px; padding: 8px 12px; background: var(--surface); }
.card h3 { margin: 0 0 6px; font-size: 14px; font-weight: 500; }
.card p { margin: 4px 0; font-size: 13px; }
.code { font-size: 16px; letter-spacing: 2px; font-weight: 700; }
.level { display: flex; gap: 8px; align-items: center; font-size: 13px; }
.plan { white-space: pre-wrap; word-break: break-word; background: var(--bg); border-radius: 4px; padding: 6px 8px; margin: 6px 0; font-size: 12.5px; max-height: 240px; overflow: auto; }
.buttons { display: flex; gap: 8px; margin-top: 6px; }
.muted { color: var(--muted); }
.error { color: var(--bad); margin: 0; }
</style>
