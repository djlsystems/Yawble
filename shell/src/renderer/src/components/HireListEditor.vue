<script setup lang="ts">
import { computed, watch } from 'vue';
import type { HireRow, RoleRow } from '../../../shared/api';
import type { useAgentModels } from '../agentModels';
import AgentModelPicker from './AgentModelPicker.vue';

// A hire list (who the Manager may hire): rows of a role, its agent (built-in or custom) and its model (the agent's
// default or a picked one). Picking a role fills the agent and model from the role's suggestion; both stay editable.
// An empty list lets the Manager hire any agent with a job it writes. Used by Settings > Teams (the default list),
// Team settings > Hiring and the New team dialog. It changes `rows` in place. `used` (Team settings, from
// teams.hireList): how many members hold each role, shown as "2 hired" - a count, never a limit.
const props = withDefaults(defineProps<{
  rows: HireRow[]; roles: RoleRow[]; agents: { id: string; label: string }[]; models: ReturnType<typeof useAgentModels>;
  testId: string; readOnly?: boolean; used?: Record<string, number>;
}>(), { readOnly: false, used: () => ({}) });
const emit = defineEmits<{ changed: [] }>();

const roleOf = (name: string) => props.roles.find((r) => r.name === name);
const free = computed(() => props.roles.filter((r) => !props.rows.some((x) => x.role === r.name)));
watch(() => props.rows.map((r) => r.agent), (clis) => { for (const c of new Set(clis)) void props.models.loadModels(c); }, { immediate: true });

function add() {
  const r = free.value[0] ?? props.roles[0];
  if (!r) return;
  props.rows.push({ role: r.name, agent: r.agent, model: r.model ?? null });
  emit('changed');
}
function remove(i: number) { props.rows.splice(i, 1); emit('changed'); }
function roleChanged(row: HireRow) {
  const r = roleOf(row.role);
  if (r) { row.agent = r.agent; row.model = r.model ?? null; }
  emit('changed');
}
function agentChanged(row: HireRow) { row.model = null; emit('changed'); }
function setModel(row: HireRow, m: string) { row.model = m || null; emit('changed'); }
</script>

<template>
  <div class="hire-list" :data-test="testId">
    <p v-if="!rows.length" class="muted small" :data-test="`${testId}-empty`">No rows: the Manager may hire any agent, with a job it writes itself.
      Add a row to have it hire only these roles, agents and models.</p>
    <div v-for="(r, i) in rows" :key="i" class="hire row" :data-test="`${testId}-row-${i}`">
      <select v-model="r.role" :aria-label="`Row ${i + 1} role`" :data-test="`${testId}-role-${i}`" :disabled="readOnly" @change="roleChanged(r)">
        <option v-for="x in roles" :key="x.name" :value="x.name">{{ x.name }}</option>
        <option v-if="!roleOf(r.role)" :value="r.role">{{ r.role }}</option>
      </select>
      <select v-model="r.agent" :aria-label="`Row ${i + 1} agent`" :data-test="`${testId}-agent-${i}`" :disabled="readOnly" @change="agentChanged(r)">
        <option v-for="c in agents" :key="c.id" :value="c.id">{{ c.label }}</option>
        <option v-if="!agents.some((o) => o.id === r.agent)" :value="r.agent">{{ r.agent }}</option>
      </select>
      <AgentModelPicker :key="`${i}-${r.agent}-${models.modelsOf(r.agent).length}`" :model-value="r.model ?? ''" class="model" :test-id="`${testId}-model-${i}`" :models="models.modelsOf(r.agent)"
        :default-label="models.defaultLabel(r.agent)" :note="false" :class="{ off: readOnly }" @update:model-value="setModel(r, $event)" />
      <span v-if="used[r.role]" class="muted small" :data-test="`${testId}-used-${i}`">{{ used[r.role] }} hired</span>
      <button v-if="!readOnly" type="button" class="icon danger" title="Remove" :aria-label="`Remove row ${i + 1}`" :data-test="`${testId}-remove-${i}`" @click="remove(i)">✕</button>
    </div>
    <button v-if="!readOnly" type="button" :data-test="`${testId}-add`" :disabled="!roles.length" @click="add">+ Add a role</button>
    <p v-if="!readOnly && !roles.length" class="muted small" :data-test="`${testId}-no-roles`">There are no roles yet: add them in Settings &gt; Teams &gt; Roles.</p>
  </div>
</template>

<style scoped>
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.hire { margin-bottom: 6px; }
.small { font-size: 12px; }
.muted { color: var(--muted); }
.model :deep(select) { min-width: 140px; }
.model :deep(input) { width: 140px; }
.off { pointer-events: none; opacity: .7; }
.icon { border: 0; background: transparent; }
</style>
