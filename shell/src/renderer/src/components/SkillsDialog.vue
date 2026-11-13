<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import type { YawbleApi, Skill, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

// Skills: what agents load with skills_list / skills_get. Built-in skills come with the app and
// are read-only; a custom skill of the same name replaces one (in every team, or in one team only).
// `fill` (the Settings tab): the list and the skill's text take the page's remaining height and scroll on their own.
const props = defineProps<{ api?: YawbleApi; teams: TeamSummary[]; inline?: boolean; fill?: boolean }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const skills = ref<Skill[]>([]);
const selected = ref<Skill | null>(null);
const editing = ref(false);
const error = ref<string | null>(null);
const saved = ref<string | null>(null);
const loaded = ref(false); // the first skills.list answered (or failed)
const form = reactive({ name: '', description: '', roles: ['member'] as string[], body: '', teamId: null as number | null });

const roleChoices = [
  { id: 'manager', label: 'Manager' },
  { id: 'member', label: 'Members' },
  { id: 'any', label: 'Everyone' },
];

const key = (s: Skill) => `${s.kind}:${s.teamId ?? ''}:${s.name}`;
const scope = (s: Skill) => (s.kind === 'builtin' ? 'built-in' : s.team ? `team ${s.team}` : 'all teams');
const replaced = computed(() => new Set(skills.value.filter((s) => s.kind === 'custom' && s.teamId === null).map((s) => s.name)));

async function load() {
  error.value = null;
  try {
    skills.value = await api.call<Skill[]>('skills.list');
    if (selected.value) selected.value = skills.value.find((s) => key(s) === key(selected.value!)) ?? null;
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loaded.value = true;
  }
}

function pick(s: Skill) {
  selected.value = s;
  editing.value = false;
  saved.value = null;
}

function startNew(from?: Skill) {
  Object.assign(form, from
    ? { name: from.name, description: from.description, roles: [...from.roles], body: from.body, teamId: from.kind === 'custom' ? from.teamId : null }
    : { name: '', description: '', roles: ['member'], body: '', teamId: null });
  selected.value = null;
  editing.value = true;
  saved.value = null;
}

// Save / Delete await the core: held with a spinner until it answers.
const work = useBusy();
const save = () => work.run('save', doSave);
async function doSave() {
  error.value = null;
  try {
    const s = await api.call<Skill>('skills.save', { ...form, name: form.name.trim(), description: form.description.trim() });
    await load();
    selected.value = skills.value.find((x) => key(x) === key(s)) ?? s;
    editing.value = false;
    saved.value = `Saved ${s.name}. Agents get it at their next run.`;
  } catch (e) {
    error.value = errorText(e);
  }
}

function remove(s: Skill) {
  if (s.id === null || !window.confirm(`Delete the skill ${s.name} (${scope(s)})?`)) return;
  return work.run('delete', () => doRemove(s));
}
async function doRemove(s: Skill) {
  error.value = null;
  try {
    await api.call('skills.delete', { id: s.id });
    selected.value = null;
    await load();
  } catch (e) {
    error.value = errorText(e);
  }
}

function toggleRole(id: string) {
  form.roles = form.roles.includes(id) ? form.roles.filter((r) => r !== id) : [...form.roles, id];
}

onMounted(load);
</script>

<template>
  <Modal :inline="inline" :fill="inline && fill" title="Skills" wide @close="emit('close')">
    <p class="muted small">Agents see the list of skills offered to their role and load one when it applies (skills_list, skills_get).
      Every agent CLI gets the same skills the same way.</p>
    <p v-if="error" class="error" data-test="skills-error">{{ error }}</p>
    <div class="layout" :class="{ fill: inline && fill }" data-test="skills-layout">
      <aside class="list" data-test="skills-list">
        <LoadingNote v-if="!loaded" text="Loading skills…" />
        <button v-for="s in skills" :key="key(s)" class="item" :class="{ on: selected && key(selected) === key(s) }"
          :data-test="`skill-${s.name}`" @click="pick(s)">
          <strong>{{ s.name }}</strong>
          <span class="chip">{{ scope(s) }}</span>
          <span v-if="s.kind === 'builtin' && replaced.has(s.name)" class="chip warn">replaced</span>
          <span class="muted small desc">{{ s.description }}</span>
        </button>
        <button class="primary new" data-test="skill-new" @click="startNew()">+ New skill</button>
      </aside>

      <section class="pane" data-test="skills-pane">
        <template v-if="editing">
          <label>Name <input v-model="form.name" placeholder="release-notes" data-test="skill-name" /></label>
          <label>Description (one line: when an agent should load it)
            <input v-model="form.description" data-test="skill-description" /></label>
          <div class="roles row">Offered to
            <label v-for="r in roleChoices" :key="r.id" class="check">
              <input type="checkbox" :checked="form.roles.includes(r.id)" :data-test="`skill-role-${r.id}`" @change="toggleRole(r.id)" /> {{ r.label }}
            </label>
          </div>
          <label>Teams
            <select v-model="form.teamId" data-test="skill-team">
              <option :value="null">All teams</option>
              <option v-for="t in teams" :key="t.id" :value="t.id">Only team {{ t.name }}</option>
            </select>
          </label>
          <label>Instructions (markdown)
            <textarea v-model="form.body" rows="16" class="mono" data-test="skill-body"></textarea></label>
          <div class="row"><span class="grow"></span>
            <button :disabled="work.is('save')" @click="editing = false">Cancel</button>
            <button class="primary" data-test="skill-save" :disabled="work.is('save')" :aria-busy="work.is('save')" @click="save"><BusyLabel :busy="work.is('save')" text="Saving…">Save skill</BusyLabel></button>
          </div>
        </template>
        <template v-else-if="selected">
          <header class="row">
            <h3 class="grow">{{ selected.name }}</h3>
            <span class="chip">{{ scope(selected) }}</span>
            <span class="chip">for {{ selected.roles.join(', ') }}</span>
          </header>
          <p class="muted">{{ selected.description }}</p>
          <pre class="body" data-test="skill-view">{{ selected.body }}</pre>
          <div class="row"><span class="grow"></span>
            <span v-if="saved" class="muted small" data-test="skill-saved">{{ saved }}</span>
            <button v-if="selected.kind === 'builtin'" data-test="skill-customize" @click="startNew(selected)">Customize…</button>
            <template v-else>
              <button data-test="skill-edit" @click="startNew(selected)">Edit</button>
              <button class="danger" data-test="skill-delete" :disabled="work.is('delete')" :aria-busy="work.is('delete')" @click="remove(selected)"><BusyLabel :busy="work.is('delete')" text="Deleting…">Delete</BusyLabel></button>
            </template>
          </div>
        </template>
        <p v-else class="muted">Pick a skill to read it, or make a new one.</p>
      </section>
    </div>
  </Modal>
</template>

<style scoped>
.small { font-size: 12px; }
.layout { display: grid; grid-template-columns: 300px 1fr; gap: 14px; min-height: 420px; }
.list { display: flex; flex-direction: column; gap: 4px; overflow: auto; max-height: 560px; }
.item { text-align: left; display: flex; flex-wrap: wrap; gap: 4px 6px; align-items: baseline; padding: 6px 8px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }
.item.on { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 6%, var(--surface)); }
.item .desc { flex-basis: 100%; }
.chip.warn { background: color-mix(in srgb, var(--warn) 15%, var(--surface)); color: var(--warn); }
.new { margin-top: 8px; }
.pane label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--muted); margin-bottom: 10px; }
.pane label.check { flex-direction: row; align-items: center; margin: 0 0 0 10px; color: inherit; }
.roles { font-size: 12px; color: var(--muted); margin-bottom: 10px; }
.pane h3 { margin: 0; font-size: 15px; }
.body { white-space: pre-wrap; background: var(--bg); border: 1px solid var(--line); border-radius: 6px; padding: 10px; max-height: 420px; overflow: auto; font-size: 12px; }
textarea { width: 100%; font-size: 12px; }
/* Filling the page: one row as tall as the space left; the list and the right pane scroll on their own; the skill's
   text takes the right pane's height above its button row, which stays at the bottom. */
.layout.fill { flex: 1 1 0; min-height: 0; grid-template-rows: minmax(0, 1fr); }
.layout.fill > .list { max-height: none; min-height: 0; }
.layout.fill > .pane { min-height: 0; overflow: auto; display: flex; flex-direction: column; }
.layout.fill > .pane > * { flex: none; }
.layout.fill > .pane > .body { flex: 1 1 0; min-height: 60px; max-height: none; margin: 0 0 10px; }
/* A narrow window: the panes stack and the page scrolls. */
@media (max-width: 720px) {
  .layout { grid-template-columns: 1fr; min-height: 0; }
  .layout.fill { grid-template-rows: none; }
  .layout.fill > .list { max-height: 560px; }
  .layout.fill > .pane { display: block; overflow: visible; }
  .layout.fill > .pane > .body { max-height: 420px; }
}
</style>
