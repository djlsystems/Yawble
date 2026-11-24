<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import type { HireRow, RoleRow, TeamDefaultMember, TeamDefaults, TeamDefaultsSaved, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { useAgentOptions } from '../agentOptions';
import { readOnly, vChange } from '../remoteAccess';
import BusyLabel from './BusyLabel.vue';
import LoadingNote from './LoadingNote.vue';
import HireListEditor from './HireListEditor.vue';
import RolesSection from './RolesSection.vue';
import { useAgentModels } from '../agentModels';

// Settings > Teams: the agents a new team starts with (the Manager, and member rows that start in the New Team
// dialog's list or are offered there), and the list of first names new members are named from. One Save for the tab.
// Generate fills the names box with names for a country; nothing is kept until Save. The default hire list (who the
// Manager of a new team may hire) is kept by that Save too; the Roles below it save each change at once.
const props = defineProps<{ api?: YawbleApi }>();
const api = resolveApi(props.api);
const agentOptions = useAgentOptions(api);

const loaded = ref(false);
const loadError = ref<string | null>(null);
const manager = reactive({ name: 'Manager', cli: 'claude' });
const members = reactive<TeamDefaultMember[]>([]);
const namesText = ref('');
const country = ref('en-US');
const countries = ref<{ code: string; label: string }[]>([]);
const usedCount = ref(0);
const rejected = ref<{ line: string; reason: string }[]>([]);
// B147: the role catalog (saved at once by its own buttons) and the default hire list (kept by the tab's Save). An
// older core without them: the Roles section and the hire list are not shown, and Save sends no hireList.
const models = useAgentModels(api);
const roles = ref<RoleRow[] | null>(null);
const hireList = reactive<HireRow[]>([]);
const hasHireList = ref(false);
async function loadRoles() { try { roles.value = await api.call<RoleRow[]>('roles.list'); } catch { roles.value = null; } }

const saving = ref(false);
const saveError = ref<string | null>(null);
const saved = ref(false);
const generating = ref(false);
const generateError = ref<string | null>(null);

/** The names in the box as Save keeps them: trimmed, no blanks, no repeats ignoring case. */
const nameCount = computed(() => {
  const seen = new Set<string>();
  for (const line of namesText.value.split(/\r?\n/)) {
    const n = line.trim();
    if (n) seen.add(n.toLowerCase());
  }
  return seen.size;
});

function apply(d: TeamDefaults) {
  manager.name = d.manager?.name || 'Manager';
  manager.cli = d.manager?.cli || 'claude';
  members.splice(0, members.length, ...(d.members ?? []).map((m) => ({ name: m.name, cli: m.cli, onNewTeams: !!m.onNewTeams })));
  namesText.value = (d.memberNames ?? []).join('\n');
  countries.value = d.countries ?? [];
  country.value = d.country || countries.value[0]?.code || 'en-US';
  usedCount.value = d.usedCount ?? 0;
  hasHireList.value = Array.isArray(d.hireList);
  hireList.splice(0, hireList.length, ...(d.hireList ?? []).map((r) => ({ role: r.role, agent: r.agent, model: r.model ?? null, max: r.max || 1 })));
}

async function load() {
  apply(await api.call<TeamDefaults>('teams.defaults'));
  loaded.value = true;
}

onMounted(async () => {
  void models.loadProfiles();
  void loadRoles();
  try { await load(); } catch (e) { loadError.value = errorText(e); }
});

function addMember() {
  members.push({ name: '', cli: manager.cli, onNewTeams: true });
  saved.value = false;
}

function removeMember(i: number) {
  members.splice(i, 1);
  saved.value = false;
}

async function save() {
  if (saving.value || readOnly.value) return;
  saving.value = true;
  saveError.value = null;
  saved.value = false;
  try {
    const r = await api.call<TeamDefaultsSaved>('teams.setDefaults', {
      manager: { name: manager.name.trim() || 'Manager', cli: manager.cli },
      members: members.filter((m) => m.name.trim()).map((m) => ({ name: m.name.trim(), cli: m.cli, onNewTeams: m.onNewTeams })),
      memberNamesText: namesText.value,
      country: country.value,
      ...(hasHireList.value ? { hireList: hireList.map((r) => ({ role: r.role, agent: r.agent, model: r.model ?? null, max: r.max })) } : {}),
    });
    rejected.value = r?.rejected ?? [];
    saved.value = true;
    // The saved list as the core keeps it (cleaned), and its fresh used count.
    try { await load(); } catch { /* keep what is shown */ }
  } catch (e) {
    saveError.value = errorText(e);
  } finally {
    saving.value = false;
  }
}

async function generate() {
  if (generating.value || readOnly.value) return;
  generating.value = true;
  generateError.value = null;
  try {
    const r = await api.call<{ names: string[] }>('teams.generateNames', { country: country.value });
    const names = (r?.names ?? []).map((n) => String(n).trim()).filter(Boolean);
    if (names.length === 0) generateError.value = 'The agent gave no names. Try again.';
    else {
      namesText.value = names.join('\n');
      rejected.value = [];
      saved.value = false;
    }
  } catch (e) {
    generateError.value = errorText(e);
  } finally {
    generating.value = false;
  }
}
</script>

<template>
  <section class="teams-settings" data-test="settings-teams">
    <p v-if="loadError" class="error" data-test="team-defaults-error">{{ loadError }}</p>
    <LoadingNote v-if="!loaded && !loadError" text="Loading settings…" />
    <template v-if="loaded">
      <h3>Default agents</h3>
      <p class="muted small">What the New Team dialog starts with. Rows ticked "On new teams" start in its member list; the others are
        offered there with one click. A name that is just the job ("Developer") gets a first name from the list below.</p>
      <div class="agent row" data-test="default-manager">
        <input v-model="manager.name" class="name" data-test="default-manager-name" aria-label="Manager name" :disabled="readOnly" @input="saved = false" />
        <span class="chip">manager</span>
        <select v-model="manager.cli" data-test="default-manager-cli" aria-label="Manager agent" :disabled="readOnly" @change="saved = false">
          <option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option>
          <option v-if="!agentOptions.some((o) => o.id === manager.cli)" :value="manager.cli">{{ manager.cli }}</option>
        </select>
      </div>
      <div v-for="(m, i) in members" :key="i" class="agent row" :data-test="`default-member-${i}`">
        <input v-model="m.name" class="name" placeholder="member name" :aria-label="`Member ${i + 1} name`" :data-test="`default-member-name-${i}`" :disabled="readOnly" @input="saved = false" />
        <span class="chip">member</span>
        <select v-model="m.cli" :aria-label="`Member ${i + 1} agent`" :data-test="`default-member-cli-${i}`" :disabled="readOnly" @change="saved = false">
          <option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option>
          <option v-if="!agentOptions.some((o) => o.id === m.cli)" :value="m.cli">{{ m.cli }}</option>
        </select>
        <label class="check"><input v-model="m.onNewTeams" type="checkbox" :data-test="`default-member-on-${i}`" :disabled="readOnly" @change="saved = false" /> On new teams</label>
        <button v-change class="icon danger" title="Remove" :aria-label="`Remove member ${i + 1}`" :data-test="`default-member-remove-${i}`" @click="removeMember(i)">✕</button>
      </div>
      <button v-change data-test="default-member-add" @click="addMember">+ Add member</button>

      <template v-if="hasHireList">
        <h3>Who the Manager may hire</h3>
        <p class="muted small">The hire list new teams start with: the roles the Manager may hire, each on its agent and model. A team's own list
          is in its Settings, on the Hiring tab.</p>
        <HireListEditor :rows="hireList" :roles="roles ?? []" :agents="agentOptions" :models="models" test-id="default-hire" :read-only="readOnly"
          @changed="saved = false" />
      </template>

      <h3>Member names</h3>
      <p class="muted small">New members get a first name picked at random from this list, never one a member of any team already has.
        One name per line.</p>
      <textarea v-model="namesText" class="names" rows="10" data-test="member-names" aria-label="Member names" :readonly="readOnly" @input="saved = false"></textarea>
      <p class="muted small" data-test="member-names-count">{{ nameCount }} {{ nameCount === 1 ? 'name' : 'names' }}, {{ usedCount }} used by a member</p>
      <div v-if="rejected.length" class="rejected" data-test="member-names-rejected">
        <p class="warn">Not saved:</p>
        <ul>
          <li v-for="(r, i) in rejected" :key="i" data-test="member-names-rejected-line"><span class="mono">{{ r.line }}</span> <span class="muted">{{ r.reason }}</span></li>
        </ul>
      </div>
      <div class="generate row">
        <label class="inline">Country
          <select v-model="country" data-test="names-country" :disabled="readOnly">
            <option v-for="c in countries" :key="c.code" :value="c.code">{{ c.label }}</option>
            <option v-if="!countries.some((c) => c.code === country)" :value="country">{{ country }}</option>
          </select>
        </label>
        <button v-change data-test="names-generate" :disabled="generating" :aria-busy="generating" @click="generate">
          <BusyLabel :busy="generating" text="Generating…">Generate</BusyLabel></button>
      </div>
      <p class="muted small">Generate asks the Concierge's default agent for 100 popular first names there. They replace the list above; nothing is kept until Save.</p>
      <p v-if="generateError" class="error" data-test="names-generate-error">{{ generateError }}</p>

      <div class="row savebar">
        <button v-change class="primary" data-test="team-defaults-save" :disabled="saving" :aria-busy="saving" @click="save">
          <BusyLabel :busy="saving" text="Saving…">Save</BusyLabel></button>
        <span v-if="saved" class="muted small" data-test="team-defaults-saved">Saved</span>
        <span v-if="saveError" class="error" data-test="team-defaults-save-error">{{ saveError }}</span>
      </div>

      <template v-if="roles">
        <h3>Roles</h3>
        <RolesSection :api="api" :roles="roles" :agents="agentOptions" :models="models" @changed="loadRoles" />
      </template>
    </template>
  </section>
</template>

<style scoped>
h3 { margin: 18px 0 6px; font-size: 15px; }
h3:first-of-type { margin-top: 0; }
.small { font-size: 12px; }
.muted { color: var(--muted); font-weight: 400; }
.warn { color: var(--warn); margin: 6px 0 2px; font-size: 12.5px; }
.error { color: var(--bad); }
.mono { font-family: var(--mono, monospace); }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.agent { margin-bottom: 6px; }
.agent .name { width: 220px; max-width: 100%; }
.check { display: inline-flex; gap: 6px; align-items: center; font-size: 13px; }
.names { display: block; width: 100%; max-width: 520px; box-sizing: border-box; resize: vertical; }
.rejected ul { margin: 0; padding-left: 18px; font-size: 12.5px; }
.generate { margin-top: 10px; }
.inline { display: inline-flex; gap: 6px; align-items: center; font-size: 13px; }
.generate select { max-width: 100%; }
.savebar { margin-top: 18px; }
</style>
