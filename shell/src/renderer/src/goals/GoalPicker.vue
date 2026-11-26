<script setup lang="ts">
// A deliverable's goal: a select with every goal, Unassigned, and "New goal…" (the goal dialog, then the new goal is
// assigned). A change sends goals.assign and says `changed`; a refusal shows under it and the select goes back to the
// deliverable's goal. The goals come from the page (`goals`, one goals.list for every row) or, without it, are read
// once per mount; after a new goal the page is told (goalsChanged) or the list is read again. A Visitor's select is
// disabled (v-change).
import { computed, onMounted, ref, watch } from 'vue';
import type { Goal, GoalRef, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { vChange } from '../remoteAccess';
import GoalDialog from './GoalDialog.vue';
import { goalsApi } from './goals';

const props = defineProps<{ api: YawbleApi; deliverable: number; goal?: GoalRef | null; goals?: readonly Goal[] | null; compact?: boolean }>();
const emit = defineEmits<{ changed: [goal: GoalRef | null]; goalsChanged: [] }>();

const NewGoal = 'new';
const calls = goalsApi(props.api);
const own = ref<Goal[]>([]);
const goals = computed<readonly Goal[]>(() => props.goals ?? own.value);
const current = ref<GoalRef | null>(props.goal ?? null);
const value = ref(current.value ? String(current.value.id) : '');
const busy = ref(false);
const error = ref<string | null>(null);
const creating = ref(false);
watch(() => props.goal, (g) => { current.value = g ?? null; value.value = g ? String(g.id) : ''; });

async function loadGoals() {
  if (props.goals) { emit('goalsChanged'); return; }
  try { own.value = (await calls.list())?.goals ?? []; } catch { /* the select still shows the deliverable's own goal */ }
}
onMounted(() => { if (!props.goals) void loadGoals(); });

/** The choices: every goal, and the deliverable's own goal even before the list is in. */
const options = computed(() => {
  const list = goals.value.map((g) => ({ id: g.id, name: g.name }));
  const c = current.value;
  if (c && !list.some((g) => g.id === c.id)) list.unshift(c);
  return list;
});

async function assign(goal: GoalRef | null) {
  busy.value = true;
  error.value = null;
  try {
    const a = await calls.assign(props.deliverable, goal?.id ?? null);
    current.value = a?.goal ?? goal;
    value.value = current.value ? String(current.value.id) : '';
    emit('changed', current.value);
  } catch (e) {
    error.value = errorText(e);
    value.value = current.value ? String(current.value.id) : '';
  } finally { busy.value = false; }
}

function onChange(v: string) {
  if (v === NewGoal) { value.value = current.value ? String(current.value.id) : ''; creating.value = true; return; }
  const goal = v ? options.value.find((g) => String(g.id) === v) ?? null : null;
  if ((goal?.id ?? null) === (current.value?.id ?? null)) return;
  void assign(goal);
}

async function created(g: Goal) {
  creating.value = false;
  await loadGoals();
  await assign({ id: g.id, name: g.name });
}
</script>

<template>
  <span class="goal-picker" :class="{ compact }" data-test="goal-picker" @click.stop @keydown.stop>
    <select v-change :value="value" :disabled="busy" :aria-label="`Goal of deliverable #${deliverable}`" title="Goal" data-test="goal-picker-select"
      @change="onChange(($event.target as HTMLSelectElement).value)">
      <option value="" data-test="goal-picker-unassigned">Unassigned</option>
      <option v-for="g in options" :key="g.id" :value="String(g.id)">{{ g.name }}</option>
      <option :value="NewGoal" data-test="goal-picker-new">New goal…</option>
    </select>
    <span v-if="error" class="error small" role="alert" data-test="goal-picker-error">{{ error }}</span>
    <GoalDialog v-if="creating" :api="api" :goals="goals" @saved="created" @close="creating = false" />
  </span>
</template>

<style scoped>
.goal-picker { display: inline-flex; flex-direction: column; gap: 2px; min-width: 0; max-width: 100%; }
select { font: inherit; font-size: 13px; padding: 4px 6px; border: 1px solid var(--line); border-radius: 6px; max-width: 220px; min-width: 0; text-overflow: ellipsis; }
.compact select { font-size: 12px; padding: 2px 4px; max-width: 160px; }
.error { color: var(--bad); }
.small { font-size: 11.5px; }
@media (max-width: 759.98px) {
  select, .compact select { min-height: 44px; font-size: 16px; max-width: 100%; }
}
</style>
