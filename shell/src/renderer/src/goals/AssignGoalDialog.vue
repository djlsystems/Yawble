<script setup lang="ts">
// One goal for several deliverables at once: says how many are selected, offers every goal and Unassigned, and sends
// goals.assign for each deliverable in turn. When all are changed it says `done`; when some are refused it stays open,
// lists them with the core's reason, and Assign tries those again. `changed` is said after every run that changed any,
// so the page reads its rows again. A Visitor's Assign is disabled (v-change).
import { computed, ref } from 'vue';
import type { Goal, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { vChange } from '../remoteAccess';
import Modal from '../components/Modal.vue';
import BusyLabel from '../components/BusyLabel.vue';
import { goalsApi } from './goals';

const props = defineProps<{ api: YawbleApi; deliverables: readonly { id: number; title: string }[]; goals: readonly Goal[] }>();
const emit = defineEmits<{ done: []; changed: []; close: [] }>();

const Unassigned = 'none';
const calls = goalsApi(props.api);
/** The deliverables still to change: all of them, then only the ones the core refused. */
const left = ref([...props.deliverables]);
const value = ref('');
const busy = ref(false);
const at = ref(0);
const failed = ref<{ id: number; title: string; why: string }[]>([]);
const count = computed(() => left.value.length);
const many = (n: number) => `${n} deliverable${n === 1 ? '' : 's'}`;

async function assign() {
  if (!value.value || busy.value) return;
  const goal = value.value === Unassigned ? null : Number(value.value);
  busy.value = true;
  at.value = 0;
  const refused: typeof failed.value = [];
  let changed = 0;
  for (const d of left.value) {
    at.value++;
    try { await calls.assign(d.id, goal); changed++; }
    catch (e) { refused.push({ id: d.id, title: d.title, why: errorText(e) }); }
  }
  busy.value = false;
  failed.value = refused;
  if (changed) emit('changed');
  if (!refused.length) { emit('done'); return; }
  left.value = refused.map(({ id, title }) => ({ id, title }));
}
function close() { if (!busy.value) emit('close'); }
</script>

<template>
  <Modal title="Assign a goal" @close="close">
    <form class="assign" data-test="assign-goal-dialog" @submit.prevent="assign">
      <p class="count" data-test="assign-goal-count"><strong>{{ many(count) }}</strong> selected.</p>
      <label class="field">Goal
        <select v-model="value" :disabled="busy" data-test="assign-goal-select">
          <option value="" disabled>Choose a goal…</option>
          <option v-for="g in goals" :key="g.id" :value="String(g.id)">{{ g.name }}</option>
          <option :value="Unassigned" data-test="assign-goal-unassigned">Unassigned (no goal)</option>
        </select>
      </label>
      <p v-if="!goals.length" class="muted small" data-test="assign-goal-none">There are no goals yet. Make one on the Goals page.</p>
      <div v-if="failed.length" class="failed" role="alert" data-test="assign-goal-failed">
        <p class="error">{{ many(failed.length) }} could not be changed:</p>
        <ul>
          <li v-for="f in failed" :key="f.id"><span class="mono">#{{ f.id }}</span> {{ f.title }}: {{ f.why }}</li>
        </ul>
      </div>
      <div class="buttons">
        <button type="button" :disabled="busy" data-test="assign-goal-cancel" @click="close">{{ failed.length ? 'Close' : 'Cancel' }}</button>
        <button v-change type="submit" class="primary" :disabled="!value || busy" data-test="assign-goal-save">
          <BusyLabel :busy="busy" :text="`Assigning ${at} of ${count}…`">{{ failed.length ? 'Try again' : `Assign to ${many(count)}` }}</BusyLabel>
        </button>
      </div>
    </form>
  </Modal>
</template>

<style scoped>
.assign { display: flex; flex-direction: column; gap: 12px; }
.count { margin: 0; }
.field { display: flex; flex-direction: column; gap: 4px; font-size: 13px; color: var(--ink-soft); }
select { font: inherit; font-size: 14px; padding: 6px 8px; border: 1px solid var(--line); border-radius: 6px; }
.failed ul { margin: 4px 0 0; padding-left: 18px; max-height: 160px; overflow: auto; font-size: 13px; }
.failed p { margin: 0; }
.error { color: var(--bad); }
.muted { color: var(--muted); }
.small { font-size: 12px; margin: 0; }
.mono { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.buttons { display: flex; justify-content: flex-end; gap: 8px; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
@media (max-width: 759.98px) {
  select, .buttons button { min-height: 44px; font-size: 16px; }
}
</style>
