<script setup lang="ts">
// A goal's name and description: New goal (no goal given) or Edit. Save sends goals.save and gives back the saved goal;
// Delete (Edit only) asks first in the window's own confirm dialog, then goals.delete (its deliverables become
// Unassigned). Escape, Cancel or a click on the backdrop closes without a change. A Visitor's buttons are disabled.
// An empty name is reported when Save is pressed, not when the box loses focus; pressing Cancel keeps the focus in the
// box, so no message appears under it and moves the buttons between the press and the release.
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { GoalDescriptionMax, GoalNameMax, type Goal, type YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { confirmDialog } from '../confirm';
import { vChange } from '../remoteAccess';
import BusyLabel from '../components/BusyLabel.vue';
import { goalsApi, nameProblem } from './goals';

const props = defineProps<{ api: YawbleApi; goal?: Goal | null; goals?: readonly Goal[] }>();
const emit = defineEmits<{ saved: [goal: Goal]; deleted: [id: number]; close: [] }>();

const name = ref(props.goal?.name ?? '');
const description = ref(props.goal?.description ?? '');
const busy = ref<'save' | 'delete' | null>(null);
const error = ref<string | null>(null);
const touched = ref(false);
const box = ref<HTMLInputElement | null>(null);
const editing = computed(() => !!props.goal);
const problem = computed(() => nameProblem(name.value, props.goals ?? [], props.goal?.id ?? null));
const calls = goalsApi(props.api);

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && !document.querySelector('[data-test=confirm-dialog]')) { e.preventDefault(); e.stopPropagation(); emit('close'); }
}
onMounted(() => { document.addEventListener('keydown', onKey, true); void nextTick(() => box.value?.focus()); });
onBeforeUnmount(() => document.removeEventListener('keydown', onKey, true));

async function save() {
  touched.value = true;
  if (problem.value || busy.value) return;
  busy.value = 'save';
  error.value = null;
  try { emit('saved', await calls.save({ id: props.goal?.id ?? null, name: name.value, description: description.value })); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = null; }
}

async function remove() {
  const g = props.goal;
  if (!g || busy.value) return;
  const n = g.total;
  const ok = await confirmDialog({
    message: `Delete the goal "${g.name}"?${n ? ` Its ${n} deliverable${n === 1 ? '' : 's'} become${n === 1 ? 's' : ''} Unassigned.` : ''}`,
    okLabel: 'Delete', danger: true,
  });
  if (!ok) return;
  busy.value = 'delete';
  error.value = null;
  try { await calls.remove(g.id); emit('deleted', g.id); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = null; }
}
</script>

<template>
  <div class="goal-scrim" data-test="goal-dialog-backdrop" @mousedown.self="emit('close')">
    <form class="goal-dialog" role="dialog" aria-modal="true" :aria-label="editing ? 'Edit goal' : 'New goal'" data-test="goal-dialog" @submit.prevent="save">
      <h3>{{ editing ? 'Edit goal' : 'New goal' }}</h3>
      <label class="field">Name
        <input ref="box" v-model="name" :maxlength="GoalNameMax" data-test="goal-dialog-name" @blur="touched = touched || !!name.trim()" />
      </label>
      <p v-if="touched && problem" class="error small" data-test="goal-dialog-problem">{{ problem }}</p>
      <label class="field">Description <span class="muted small">(optional)</span>
        <textarea v-model="description" rows="4" :maxlength="GoalDescriptionMax" data-test="goal-dialog-description" />
      </label>
      <p v-if="error" class="error small" data-test="goal-dialog-error">{{ error }}</p>
      <div class="buttons">
        <button v-if="editing" v-change type="button" class="danger-link" data-test="goal-dialog-delete" :disabled="!!busy" @click="remove">
          <BusyLabel :busy="busy === 'delete'" text="Deleting…">Delete</BusyLabel>
        </button>
        <span class="grow" />
        <button type="button" data-test="goal-dialog-cancel" @mousedown.prevent @click="emit('close')">Cancel</button>
        <button v-change type="submit" class="primary" data-test="goal-dialog-save" :disabled="!!busy || (touched && !!problem)">
          <BusyLabel :busy="busy === 'save'" text="Saving…">Save</BusyLabel>
        </button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.goal-scrim {
  position: fixed; inset: 0; z-index: 1000; display: flex; align-items: flex-start; justify-content: center; background: var(--backdrop);
  padding: var(--dialog-top) 16px 16px;
}
.goal-dialog {
  width: min(480px, 100%); box-sizing: border-box; background: var(--surface); color: var(--ink); border: 1px solid var(--line); border-radius: 10px;
  box-shadow: 0 10px 40px var(--shadow); padding: 18px; display: flex; flex-direction: column; gap: 10px;
}
h3 { margin: 0 0 4px; font-size: 16px; }
.field { display: flex; flex-direction: column; gap: 4px; font-size: 13px; font-weight: 600; }
.field input, .field textarea { font: inherit; font-weight: 400; font-size: 14px; padding: 6px 8px; border: 1px solid var(--line); border-radius: 6px; resize: vertical; }
.error { color: var(--bad); margin: 0; }
.small { font-size: 12px; }
.muted { color: var(--muted); font-weight: 400; }
.buttons { display: flex; align-items: center; gap: 8px; margin-top: 4px; }
.buttons button { min-height: 36px; min-width: 88px; }
.grow { flex: 1; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.danger-link { color: var(--bad); }
@media (max-width: 760px) {
  .goal-scrim { padding-top: 8vh; }
  .field input, .field textarea { font-size: 16px; min-height: 44px; }
  .buttons { flex-wrap: wrap; }
  .buttons button { flex: 1; min-width: 0; min-height: 44px; }
  .buttons .grow { display: none; }
}
</style>
