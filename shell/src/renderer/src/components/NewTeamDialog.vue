<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import type { HireRow, RoleRow, TeamDefaultMember, TeamDefaults, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { type AgentCliId } from '../agentClis';
import { useAgentOptions } from '../agentOptions';
import Modal from './Modal.vue';
import BusyLabel from './BusyLabel.vue';
import AgentModelPicker from './AgentModelPicker.vue';
import HireListEditor from './HireListEditor.vue';
import { useAgentModels } from '../agentModels';

// Team creation: the repository, the Manager and the members, each with its agent CLI. It starts from the saved
// defaults (Settings > Teams, teams.defaults): the Manager's name and agent, the rows ticked "On new teams" in the
// member list, and the other rows offered as one-click "+ <name>" buttons under it, and the default hire list (who the
// Manager may hire). Each member row may take a role, which fills its agent and model. Everything stays editable.
const props = defineProps<{ api?: YawbleApi }>();
const emit = defineEmits<{ close: []; created: [teamId: number] }>();
const api = resolveApi(props.api);
const agentOptions = useAgentOptions(api);

const form = reactive({ name: '', source: 'folder' as 'folder' | 'url', repoPath: '', repoUrl: '', managerName: 'Manager', managerCli: 'claude' as AgentCliId, managerModel: '' });
/** model '' = the agent's default model (Settings > Agents); role '' = none. */
const members = reactive<{ name: string; cli: AgentCliId; model: string; role: string }[]>([]);

// ---- models: each row picks one of its agent's models (each with its "from" hint), its default, or types one (Custom…)
const models = useAgentModels(api);
const { defaultLabel, modelsOf, loadModels } = models;
// ---- B147: roles (each row's role picker fills its agent and model) and who the Manager may hire, from the defaults.
// An older core without roles: no picker; without a default hire list: the section starts empty (any agent).
const roles = ref<RoleRow[]>([]);
const hireList = reactive<HireRow[]>([]);
onMounted(async () => {
  try { roles.value = await api.call<RoleRow[]>('roles.list'); } catch { /* no roles: no picker */ }
});
onMounted(async () => {
  await models.loadProfiles();
  await loadModels(form.managerCli);
});
/** A role picked on a row: its agent and model come from the role's suggestion, and a blank name takes the role word. */
function roleChanged(row: { name: string; cli: AgentCliId; model: string; role: string }) {
  const r = roles.value.find((x) => x.name === row.role);
  if (!r) return;
  row.cli = r.agent as AgentCliId;
  row.model = r.model ?? '';
  if (!row.name.trim()) row.name = r.name;
  void loadModels(row.cli);
}
/** A new agent for a row: its model goes back to that agent's default. */
function agentChanged(row: { cli: string; model: string }) { row.model = ''; void loadModels(row.cli); }
function managerAgentChanged() { form.managerModel = ''; void loadModels(form.managerCli); }
/** The agent "+ Add member" starts a row on: the saved Manager's agent. */
const memberCli = ref<AgentCliId>('claude');
/** Saved default rows not ticked "On new teams": offered under the list until added. */
const offered = ref<TeamDefaultMember[]>([]);
const offers = computed(() => offered.value.map((m, i) => ({ ...m, i })).filter((m) => !added.value.has(m.i)));
const added = ref(new Set<number>());

onMounted(async () => {
  let d: TeamDefaults;
  try { d = await api.call<TeamDefaults>('teams.defaults'); } catch { return; } // an older core: the built-in start
  // (a field the person already changed while this loaded is kept)
  if (d.manager?.name && form.managerName === 'Manager') form.managerName = d.manager.name;
  if (d.manager?.cli) { if (form.managerCli === 'claude') form.managerCli = d.manager.cli as AgentCliId; memberCli.value = d.manager.cli as AgentCliId; }
  const rows = (d.members ?? []).filter((m) => m.name?.trim());
  members.push(...rows.filter((m) => m.onNewTeams).map((m) => ({ name: m.name, cli: m.cli as AgentCliId, model: '', role: '' })));
  hireList.push(...(d.hireList ?? []).map((h) => ({ role: h.role, agent: h.agent, model: h.model ?? null, max: h.max || 1 })));
  for (const c of new Set([form.managerCli, ...members.map((m) => m.cli)])) void loadModels(c);
  offered.value = rows.filter((m) => !m.onNewTeams);
});

function offer(m: TeamDefaultMember & { i: number }) {
  members.push({ name: m.name, cli: m.cli as AgentCliId, model: '', role: '' });
  void loadModels(m.cli);
  added.value = new Set([...added.value, m.i]);
}
const error = ref<string | null>(null);
const busy = ref(false);

async function pick() {
  const folder = await api.pickFolder();
  if (folder) form.repoPath = folder;
}

async function create() {
  if (busy.value) return;
  error.value = null;
  busy.value = true;
  try {
    const r = await api.call<{ teamId: number }>('teams.create', {
      name: form.name.trim(), managerName: form.managerName.trim() || 'Manager',
      repoPath: form.source === 'folder' ? form.repoPath.trim() || null : null,
      repoUrl: form.source === 'url' ? form.repoUrl.trim() || null : null,
      managerCli: form.managerCli, ...(form.managerModel ? { managerModel: form.managerModel } : {}),
      members: members.filter((m) => m.name.trim()).map((m) => ({ name: m.name.trim(), cli: m.cli, ...(m.model ? { model: m.model } : {}), ...(m.role ? { role: m.role } : {}) })),
      hireList: hireList.map((h) => ({ role: h.role, agent: h.agent, model: h.model ?? null, max: h.max })),
    });
    emit('created', r.teamId);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <Modal title="New team" @close="emit('close')">
    <p v-if="error" class="error" data-test="team-error">{{ error }}</p>
    <label>Team name <input v-model="form.name" data-test="team-name" placeholder="payments-refactor" /></label>
    <div class="row sources">
      <label class="check"><input v-model="form.source" type="radio" value="folder" data-test="team-source-folder" /> A folder on this computer</label>
      <label class="check"><input v-model="form.source" type="radio" value="url" data-test="team-source-url" /> Clone from an address</label>
    </div>
    <label v-if="form.source === 'folder'">Repository
      <span class="row"><input v-model="form.repoPath" class="grow" data-test="team-repo" placeholder="a folder on this machine (blank: the app makes one)" />
        <button v-if="!api.remote" type="button" data-test="team-repo-pick" @click="pick">Choose…</button></span>
    </label>
    <template v-else>
      <label>Repository address <input v-model="form.repoUrl" data-test="team-repo-url" placeholder="https://git.example.com/owner/repo.git" /></label>
      <p class="muted small">Cloned with your own git sign-in (clone it once in your terminal first if it is private). The team's branches are
        pushed back there after each run; merging to main stays yours.</p>
    </template>
    <h3>Agents</h3>
    <div class="agent row">
      <input v-model="form.managerName" data-test="manager-name" />
      <span class="chip">manager</span>
      <select v-model="form.managerCli" data-test="manager-cli" @change="managerAgentChanged"><option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option></select>
      <AgentModelPicker :key="`manager-${form.managerCli}`" v-model="form.managerModel" class="model" test-id="manager-model" :models="modelsOf(form.managerCli)"
        :default-label="defaultLabel(form.managerCli)" :note="false" />
    </div>
    <div v-for="(m, i) in members" :key="i" class="agent row">
      <input v-model="m.name" placeholder="member name" :data-test="`member-name-${i}`" />
      <span class="chip">member</span>
      <select v-if="roles.length" v-model="m.role" :aria-label="`Member ${i + 1} role`" :data-test="`member-role-${i}`" @change="roleChanged(m)">
        <option value="">no role</option>
        <option v-for="r in roles" :key="r.name" :value="r.name">{{ r.name }}</option>
      </select>
      <select v-model="m.cli" :data-test="`member-cli-${i}`" @change="agentChanged(m)"><option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option></select>
      <AgentModelPicker :key="`member-${i}-${m.cli}`" v-model="m.model" class="model" :test-id="`member-model-${i}`" :models="modelsOf(m.cli)"
        :default-label="defaultLabel(m.cli)" :note="false" />
      <button class="icon danger" title="Remove" @click="members.splice(i, 1)">✕</button>
    </div>
    <p v-if="members.length === 0" class="muted small" data-test="hire-hint">No members yet: the Manager hires its own team when you give it work. You can also add some now.</p>
    <div v-if="offers.length" class="row offers" data-test="member-offers">
      <button v-for="m in offers" :key="m.i" type="button" class="offer" :data-test="`member-offer-${m.i}`" @click="offer(m)">+ {{ m.name }}</button>
    </div>
    <button data-test="add-member-row" @click="members.push({ name: '', cli: memberCli, model: '', role: '' }); void loadModels(memberCli)">+ Add member</button>
    <h3>Who the Manager may hire</h3>
    <p class="muted small">The roles the Manager may hire later, each on its agent and model. It starts from Settings &gt; Teams; the team's
      Settings change it later, on the Hiring tab.</p>
    <HireListEditor :rows="hireList" :roles="roles" :agents="agentOptions" :models="models" test-id="new-hire" />
    <div class="row actions"><span class="grow"></span>
      <button class="primary" data-test="create-team" :disabled="busy || !form.name.trim()" :aria-busy="busy" @click="create"><BusyLabel :busy="busy" text="Creating…">Create team</BusyLabel></button></div>
  </Modal>
</template>

<style scoped>
.small { font-size: 12px; }
label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--muted); margin-bottom: 10px; }
h3 { font-size: 13px; margin: 8px 0 6px; }
.agent { margin-bottom: 6px; }
.agent input { width: 200px; }
.agent .model :deep(select) { min-width: 150px; }
.agent .model :deep(input) { width: 150px; }
.actions { margin-top: 14px; }
.sources { gap: 16px; margin: 4px 0; }
.offers { gap: 6px; flex-wrap: wrap; margin: 2px 0 8px; }
</style>
