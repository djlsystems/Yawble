<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue';
import { usePhone } from '../phone';
import type { YawbleApi, Skill, SkillVersion, TeamSummary } from '../../../shared/api';
import { SKILLS_RESTORE, SKILLS_VERSIONS, TEAM_RETROS, writerText } from '../../../shared/api';
import { errorText, resolveApi, when } from '../api';
import { skillLink } from '../skillLink';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import { confirmDialog } from '../confirm';

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
    followLink();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loaded.value = true;
  }
}

// A phone (B054): the list and the skill are two views, list → skill → back, in one scroll level (the page's). A picked
// skill (or the editor) replaces the list, scrolled into view under the sticky Settings bar.
const phone = usePhone();
const detailOpen = computed(() => editing.value || selected.value !== null);
const showList = computed(() => !phone.value || !detailOpen.value);
const showPane = computed(() => !phone.value || detailOpen.value);
const paneEl = ref<HTMLElement | null>(null);
async function revealPane() {
  if (!phone.value) return;
  await nextTick();
  const el = paneEl.value;
  if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView({ block: 'start' });
}
function backToList() {
  selected.value = null;
  editing.value = false;
  saved.value = null;
}

function pick(s: Skill) {
  selected.value = s;
  editing.value = false;
  saved.value = null;
  void revealPane();
}

function startNew(from?: Skill) {
  Object.assign(form, from
    ? { name: from.name, description: from.description, roles: [...from.roles], body: from.body, teamId: from.kind === 'custom' ? from.teamId : null }
    : { name: '', description: '', roles: ['member'], body: '', teamId: null });
  selected.value = null;
  editing.value = true;
  saved.value = null;
  void revealPane();
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

async function remove(s: Skill) {
  if (s.id === null || !(await confirmDialog({ message: `Delete the skill ${s.name} (${scope(s)})?`, okLabel: 'Delete', danger: true }))) return;
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

// B100: a team skill's kept versions (the team's team_retros: each Manager's retro and each of the person's edits), with
// who last wrote it and Restore. A skill without versions (an ordinary one, an older core) shows none of this.
const versions = ref<SkillVersion[]>([]);
const versionsFor = ref<string | null>(null);
const showVersion = ref<number | null>(null);
async function loadVersions(s: Skill | null) {
  versions.value = [];
  showVersion.value = null;
  versionsFor.value = s ? key(s) : null;
  if (!s || s.kind !== 'custom' || s.teamId === null) return;
  try {
    const list = await api.call<SkillVersion[]>(SKILLS_VERSIONS, { teamId: s.teamId, name: s.name });
    if (versionsFor.value === key(s)) versions.value = Array.isArray(list) ? list : [];
  } catch { /* no versions to show (an older core) */ }
}
watch(selected, (s) => void loadVersions(s));
const lastWrite = computed(() => versions.value[0] ?? null);

async function restore(v: SkillVersion) {
  if (!(await confirmDialog({ message: `Bring back version ${v.version} of ${v.name}? Its text becomes the current text (a new version); agents get it at their next run.`, okLabel: 'Restore' }))) return;
  return work.run('restore', async () => {
    error.value = null;
    try {
      const s = await api.call<Skill>(SKILLS_RESTORE, { id: v.id });
      await load();
      selected.value = skills.value.find((x) => key(x) === key(s)) ?? s;
      saved.value = `Restored version ${v.version} of ${s.name}. Agents get it at their next run.`;
    } catch (e) {
      error.value = errorText(e);
    }
  }, String(v.id));
}

// Team settings' "Lessons" link: select that team's skill once the list is in.
function followLink() {
  const link = skillLink.value;
  if (!link) return;
  const s = skills.value.find((x) => x.kind === 'custom' && x.teamId === link.teamId && x.name === link.name);
  skillLink.value = null;
  if (s) pick(s);
}
watch(skillLink, (l) => { if (l && loaded.value) followLink(); });

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
    <div class="layout" :class="{ fill: inline && fill, phone }" data-test="skills-layout" :data-phone-view="phone ? (detailOpen ? 'detail' : 'list') : undefined">
      <aside v-if="showList" class="list" data-test="skills-list">
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

      <section v-if="showPane" ref="paneEl" class="pane" data-test="skills-pane">
        <button v-if="phone" class="back" data-test="skills-back" aria-label="Back to the skills" @click="backToList"><span class="back-icon" aria-hidden="true">‹</span>Skills</button>
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
          <p v-if="selected.name === TEAM_RETROS && selected.teamId !== null" class="muted small" data-test="skill-lessons-note">
            The team's lessons: every run of team {{ selected.team }} gets this text in its prompt. The Manager rewrites it after each completed
            workflow's retrospective, starting from the text here, so your edits are kept.</p>
          <p v-if="lastWrite" class="muted small" data-test="skill-last-writer">Last written by {{ writerText(lastWrite) }}, {{ when(lastWrite.writtenAt) }} (version {{ lastWrite.version }}).</p>
          <pre class="body" data-test="skill-view">{{ selected.body }}</pre>
          <details v-if="versions.length > 0" class="versions" data-test="skill-versions">
            <summary>Versions ({{ versions.length }})</summary>
            <ul>
              <li v-for="v in versions" :key="v.id" :data-test="`skill-version-${v.version}`">
                <div class="row">
                  <span class="grow"><b>Version {{ v.version }}</b> <span class="muted small">· {{ writerText(v) }} · {{ when(v.writtenAt) }}</span>
                    <span v-if="v === versions[0]" class="chip">current</span></span>
                  <button class="small-btn" :data-test="`skill-version-show-${v.version}`" @click="showVersion = showVersion === v.id ? null : v.id">{{ showVersion === v.id ? 'Hide' : 'Show' }}</button>
                  <button v-if="v !== versions[0]" class="small-btn" :data-test="`skill-version-restore-${v.version}`" :disabled="work.is('restore')"
                    :aria-busy="work.is('restore', String(v.id))" @click="restore(v)"><BusyLabel :busy="work.is('restore', String(v.id))" text="Restoring…">Restore</BusyLabel></button>
                </div>
                <pre v-if="showVersion === v.id" class="body" :data-test="`skill-version-body-${v.version}`">{{ v.body }}</pre>
              </li>
            </ul>
          </details>
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
.versions { margin: 0 0 10px; font-size: 12px; }
.versions ul { list-style: none; margin: 6px 0 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.versions li { border-top: 1px solid var(--line); padding-top: 4px; }
.versions .row { align-items: center; gap: 6px; }
.small-btn { font-size: 12px; padding: 2px 8px; }
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
/* A phone (B054): the list or the skill, one at a time, in the page's flow; nothing inside scrolls on its own. */
.layout.phone { display: block; min-height: 0; }
.layout.phone > .list { max-height: none; overflow: visible; }
.layout.phone > .pane { overflow: visible; scroll-margin-top: 56px; }
.layout.phone > .pane > .body { max-height: none; overflow: visible; overflow-wrap: anywhere; }
.layout.phone .item { min-height: 44px; }
.back { display: inline-flex; align-items: center; gap: 2px; border: 0; background: transparent; color: var(--accent); padding: 0 8px 0 0; margin-bottom: 6px; font-size: 15px; }
.back-icon { font-size: 22px; line-height: 1; margin-right: 2px; }
</style>
