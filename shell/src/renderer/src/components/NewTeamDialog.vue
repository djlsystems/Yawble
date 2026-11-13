<script setup lang="ts">
import { reactive, ref } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { type AgentCliId } from '../agentClis';
import { useAgentOptions } from '../agentOptions';
import Modal from './Modal.vue';
import BusyLabel from './BusyLabel.vue';

// Team creation: the repository, the Manager and the members, each with its agent CLI.
const props = defineProps<{ api?: YawbleApi }>();
const emit = defineEmits<{ close: []; created: [teamId: number] }>();
const api = resolveApi(props.api);
const agentOptions = useAgentOptions(api);

const form = reactive({ name: '', source: 'folder' as 'folder' | 'url', repoPath: '', repoUrl: '', managerName: 'Manager', managerCli: 'claude' as AgentCliId });
const members = reactive<{ name: string; cli: AgentCliId }[]>([]);
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
      managerCli: form.managerCli, members: members.filter((m) => m.name.trim()).map((m) => ({ name: m.name.trim(), cli: m.cli })),
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
      <select v-model="form.managerCli" data-test="manager-cli"><option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option></select>
    </div>
    <div v-for="(m, i) in members" :key="i" class="agent row">
      <input v-model="m.name" placeholder="member name" :data-test="`member-name-${i}`" />
      <span class="chip">member</span>
      <select v-model="m.cli" :data-test="`member-cli-${i}`"><option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option></select>
      <button class="icon danger" title="Remove" @click="members.splice(i, 1)">✕</button>
    </div>
    <p v-if="members.length === 0" class="muted small" data-test="hire-hint">No members yet: the Manager hires its own team when you give it work. You can also add some now.</p>
    <button data-test="add-member-row" @click="members.push({ name: '', cli: 'claude' })">+ Add member</button>
    <div class="row actions"><span class="grow"></span>
      <button class="primary" data-test="create-team" :disabled="busy || !form.name.trim()" :aria-busy="busy" @click="create"><BusyLabel :busy="busy" text="Creating…">Create team</BusyLabel></button></div>
  </Modal>
</template>

<style scoped>
.small { font-size: 12px; }
label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--muted); margin-bottom: 10px; }
h3 { font-size: 13px; margin: 8px 0 6px; }
.agent { margin-bottom: 6px; }
.agent input { width: 220px; }
.actions { margin-top: 14px; }
.sources { gap: 16px; margin: 4px 0; }
</style>
