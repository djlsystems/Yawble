<script setup lang="ts">
import { reactive, ref } from 'vue';
import type { RoleRow, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import type { useAgentModels } from '../agentModels';
import { readOnly, vChange } from '../remoteAccess';
import AgentModelPicker from './AgentModelPicker.vue';
import BusyLabel from './BusyLabel.vue';

// Settings > Teams > Roles: the role catalog (roles.list). Each role is a one-word name (also the name word on hire), a
// short job (the hire's standing instructions), and a suggested agent and model. Add, edit and remove save at once
// (roles.save, roles.remove); a removal the core refuses (a team's hire list uses the role) shows its message as it is.
const props = defineProps<{ api: YawbleApi; roles: RoleRow[]; agents: { id: string; label: string }[]; models: ReturnType<typeof useAgentModels> }>();
const emit = defineEmits<{ changed: [] }>();

/** The open form: null = closed; originalName null = a new role. */
const editing = ref<{ originalName: string | null } | null>(null);
const draft = reactive({ name: '', job: '', agent: 'claude', model: '' });
const busy = ref(false);
const error = ref<string | null>(null);
const incomplete = ref(false);

function open(r: RoleRow | null) {
  error.value = null;
  editing.value = { originalName: r?.name ?? null };
  Object.assign(draft, r ? { name: r.name, job: r.job, agent: r.agent, model: r.model ?? '' } : { name: '', job: '', agent: props.agents[0]?.id ?? 'claude', model: '' });
  void props.models.loadModels(draft.agent);
}
function agentChanged() { draft.model = ''; void props.models.loadModels(draft.agent); }
const nameOk = () => /^\S+$/.test(draft.name.trim());

async function run(method: string, params: Record<string, unknown>) {
  if (busy.value || readOnly.value) return false;
  busy.value = true;
  error.value = null;
  try { await props.api.call(method, params); emit('changed'); return true; } catch (e) { error.value = errorText(e); return false; } finally { busy.value = false; }
}
async function save() {
  const e = editing.value;
  if (!e || !nameOk()) return;
  const ok = await run('roles.save', {
    name: draft.name.trim(), job: draft.job.trim(), agent: draft.agent, model: draft.model || null,
    ...(e.originalName !== null ? { originalName: e.originalName } : {}),
  });
  if (ok) editing.value = null;
}
const remove = (r: RoleRow) => run('roles.remove', { name: r.name });
</script>

<template>
  <div class="roles" data-test="roles">
    <p class="muted small">The roles a team's hire list picks from. The name is one word and is also the member's name word when hired
      ("Developer" gets a first name from the list below); the job becomes the member's standing instructions. A role's changes are kept at once.</p>
    <p v-if="error" class="error" data-test="roles-error">{{ error }}</p>
    <p v-if="!roles.length" class="muted small" data-test="roles-empty">No roles yet.</p>
    <div v-for="r in roles" :key="r.name" class="role" :data-test="`role-${r.name}`">
      <div class="grow">
        <b>{{ r.name }}</b> <span class="muted small">{{ agents.find((a) => a.id === r.agent)?.label ?? r.agent }} · {{ r.model ?? 'agent default' }}</span>
        <p class="small job">{{ r.job }}</p>
      </div>
      <button v-change :data-test="`role-edit-${r.name}`" :disabled="busy" @click="open(r)">Edit</button>
      <button v-change class="danger-link" :data-test="`role-remove-${r.name}`" :disabled="busy" @click="remove(r)">Remove</button>
    </div>
    <div v-if="editing" class="form" data-test="role-form">
      <label>Name (one word)<input v-model="draft.name" class="name" data-test="role-name" placeholder="Developer" /></label>
      <p v-if="draft.name && !nameOk()" class="warn small" data-test="role-name-warn">One word, no spaces.</p>
      <label>Job<textarea v-model="draft.job" rows="2" data-test="role-job" placeholder="What a member of this role does"></textarea></label>
      <div class="row">
        <label>Suggested agent
          <select v-model="draft.agent" data-test="role-agent" @change="agentChanged">
            <option v-for="c in agents" :key="c.id" :value="c.id">{{ c.label }}</option>
            <option v-if="!agents.some((o) => o.id === draft.agent)" :value="draft.agent">{{ draft.agent }}</option>
          </select></label>
        <label>Suggested model
          <AgentModelPicker :key="draft.agent" v-model="draft.model" test-id="role-model" :models="models.modelsOf(draft.agent)"
            :default-label="models.defaultLabel(draft.agent)" :note="false" @incomplete="incomplete = $event" /></label>
      </div>
      <div class="row">
        <button v-change class="primary" data-test="role-save" :disabled="busy || !nameOk() || incomplete" :aria-busy="busy" @click="save">
          <BusyLabel :busy="busy" text="Saving…">{{ editing.originalName === null ? 'Add role' : 'Save role' }}</BusyLabel></button>
        <button data-test="role-cancel" :disabled="busy" @click="editing = null">Cancel</button>
      </div>
    </div>
    <button v-else v-change data-test="role-add" :disabled="busy" @click="open(null)">+ Add a role</button>
  </div>
</template>

<style scoped>
.small { font-size: 12px; }
.muted { color: var(--muted); font-weight: 400; }
.error { color: var(--bad); }
.warn { color: var(--warn); margin: 0; }
.row { display: flex; gap: 8px; align-items: flex-end; flex-wrap: wrap; }
.role { display: flex; gap: 8px; align-items: flex-start; padding: 6px 0; border-top: 1px solid var(--line-soft); }
.role .job { margin: 2px 0 0; }
.grow { flex: 1; min-width: 0; }
.form { display: flex; flex-direction: column; gap: 6px; border: 1px solid var(--line-soft); border-radius: 8px; padding: 8px 10px; margin: 6px 0; max-width: 620px; }
label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--muted); }
.name { width: 220px; max-width: 100%; }
textarea { width: 100%; box-sizing: border-box; resize: vertical; }
.danger-link { border: 0; background: transparent; color: var(--bad); }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
</style>
