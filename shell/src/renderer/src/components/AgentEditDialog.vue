<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { AgentModelList, AgentProfileRow, ArgGroup, ArgRefusal, CommandLines, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { groupRemoved, toggleRemoved, type AgentDraft } from '../agentArgs';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import AgentModelPicker from './AgentModelPicker.vue';

// One of the person's own agents: a built-in CLI with extra arguments before / after the built-in ones and unlocked
// built-ins removed. The command line is shown as it will run, as chips: the launcher (plain), the person's Before
// (editable), the built-in groups (locked ones with the reason; unlocked ones removable and restorable), the person's
// After (editable), then the prompt (locked). Each argument is one argv item: no shell, no splitting.
// "new" creates nothing until Save (agentProfiles.duplicate, then agentProfiles.update); Cancel creates nothing.
// The core is the authority: agentProfiles.check gives the refusals live, and agents.commandLine describes the draft
// with the same builder the launch uses.
const props = defineProps<{ api?: YawbleApi; mode: 'new' | 'edit'; baseCli: string; baseTitle: string; initial: AgentDraft }>();
// created: agentProfiles.duplicate made the agent (even when the update after it failed), so the list must reload.
const emit = defineEmits<{ close: []; saved: [string]; created: [] }>();
const api = resolveApi(props.api);

/** The preview (agents.commandLine) waits this long after the last change: describing a CLI detects it on the host. */
const PREVIEW_DELAY_MS = 400;

interface Chip { id: number; text: string }
let nextChip = 0;
const chip = (text: string): Chip => ({ id: ++nextChip, text });

const id = ref<number | undefined>(props.initial.id);
const creating = ref(props.mode === 'new');
const savedName = ref(props.initial.name); // the agent's name in the store (after a partial failure: the one just made)
const name = ref(props.initial.name);
const model = ref(props.initial.defaultModel ?? '');
const before = ref<Chip[]>(props.initial.argsBefore.map(chip));
const after = ref<Chip[]>(props.initial.argsAfter.map(chip));
const removed = ref<string[]>([...props.initial.removedArgs]);
const models = ref<string[]>([]);
const modelsReady = ref(false); // the picker waits for the list, so a listed model is not first shown as Custom…
const modelIncomplete = ref(false); // Custom… with no name yet
const refusals = ref<ArgRefusal[]>([]);
const lines = ref<CommandLines | null>(null);
const stale = ref(false);
const checking = ref(false);
const error = ref<string | null>(null);
const saving = ref(false);
const addBefore = ref('');
const addAfter = ref('');
const editingChip = ref<{ list: 'before' | 'after'; id: number; text: string } | null>(null);

const groups = computed<ArgGroup[]>(() => lines.value?.headless.groups ?? []);
const middle = computed(() => groups.value.filter((g) => !g.tail));
const tail = computed(() => groups.value.filter((g) => g.tail));
const launch = computed(() => lines.value?.headless.launch ?? []);
// Save follows the refusals check only; a preview being refreshed never disables it.
const canSave = computed(() => !saving.value && !checking.value && !modelIncomplete.value && refusals.value.length === 0 && name.value.trim().length > 0);

const texts = (list: Chip[]) => list.map((c) => c.text);
const draft = () => ({ argsBefore: texts(before.value), argsAfter: texts(after.value), removedArgs: [...removed.value] });

// ---- the refusals: right away on every change of the lists (the core's AgentArgsValidator; no CLI is run)
let checkSeq = 0;
async function check(previewNow = false) {
  const mine = ++checkSeq;
  checking.value = true;
  try {
    const found = await api.call<ArgRefusal[]>('agentProfiles.check', { baseCli: props.baseCli, ...draft() });
    if (mine !== checkSeq) return;
    refusals.value = found ?? [];
    if (refusals.value.length) {
      stale.value = true;
      cancelPreview();
      // Still show the chips the first time (the built-in groups without the draft's changes).
      if (!lines.value) lines.value = await api.call<CommandLines>('agents.commandLine', { cli: props.baseCli, model: model.value || undefined });
    } else if (previewNow) void preview();
    else schedulePreview();
  } catch (e) {
    if (mine === checkSeq) error.value = errorText(e);
  } finally {
    if (mine === checkSeq) checking.value = false;
  }
}

// ---- the preview: debounced, and only while the draft is clean
let previewSeq = 0;
let timer: ReturnType<typeof setTimeout> | null = null;
function cancelPreview() { if (timer) { clearTimeout(timer); timer = null; } }
let closed = false;
let previewError: string | null = null;
function schedulePreview() {
  cancelPreview();
  if (closed) return;
  previewSeq++; // an answer already in flight is for an older draft
  timer = setTimeout(() => { timer = null; void preview(); }, PREVIEW_DELAY_MS);
}
async function preview() {
  if (closed || refusals.value.length) return;
  const mine = ++previewSeq;
  try {
    const shown = await api.call<CommandLines>('agents.commandLine', { cli: props.baseCli, model: model.value || undefined, ...draft() });
    if (mine !== previewSeq || refusals.value.length) return;
    lines.value = shown;
    stale.value = false;
    if (previewError !== null && error.value === previewError) error.value = null; // only the preview's own error
    previewError = null;
  } catch (e) {
    if (mine === previewSeq) { previewError = errorText(e); error.value = previewError; }
  }
}

onMounted(async () => {
  void check(true);
  try { models.value = (await api.call<AgentModelList>('agents.models', { cli: props.baseCli })).models; }
  catch (e) { error.value = errorText(e); }
  modelsReady.value = true;
});
onBeforeUnmount(() => { closed = true; cancelPreview(); });
// The model changes only the preview (the refusals do not depend on it): typing a Custom… name is debounced.
watch(model, () => { if (!refusals.value.length) schedulePreview(); });

function listOf(which: 'before' | 'after') { return which === 'before' ? before : after; }

function add(which: 'before' | 'after') {
  const box = which === 'before' ? addBefore : addAfter;
  const arg = box.value.trim();
  if (!arg) return;
  listOf(which).value = [...listOf(which).value, chip(arg)]; // one argv item, exactly as typed
  box.value = '';
  void check();
}

function removeArg(which: 'before' | 'after', chipId: number) {
  // An edit in progress is settled first: cancelled when it is this chip, committed (by its own id) when it is another.
  if (editingChip.value?.id === chipId) editingChip.value = null;
  else if (editingChip.value) commitEdit(false);
  listOf(which).value = listOf(which).value.filter((c) => c.id !== chipId);
  void check();
}

async function startEdit(which: 'before' | 'after', c: Chip) {
  if (editingChip.value) commitEdit(false);
  editingChip.value = { list: which, id: c.id, text: c.text };
  await nextTick();
  (document.querySelector('[data-test=chip-edit]') as HTMLInputElement | null)?.focus();
}

function commitEdit(recheck = true) {
  const e = editingChip.value;
  if (!e) return;
  editingChip.value = null;
  const text = e.text.trim();
  const list = listOf(e.list);
  list.value = text ? list.value.map((c) => (c.id === e.id ? { ...c, text } : c)) : list.value.filter((c) => c.id !== e.id);
  if (recheck) void check();
}

function toggle(g: ArgGroup) {
  removed.value = toggleRemoved(g, removed.value);
  void check();
}

async function save() {
  if (!canSave.value) return;
  saving.value = true;
  error.value = null;
  const fields = { name: name.value.trim(), defaultModel: model.value, ...draft() };
  try {
    if (creating.value) {
      const made = await api.call<AgentProfileRow>('agentProfiles.duplicate', { baseCli: props.baseCli, name: fields.name, ...(fields.defaultModel ? { defaultModel: fields.defaultModel } : {}) });
      id.value = made.id;
      savedName.value = made.name;
      creating.value = false; // made: a failed update below is retried as an edit, never a second copy
      emit('created');
    }
    await api.call('agentProfiles.update', { id: id.value, ...fields });
    emit('saved', `${fields.name} saved.`);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    saving.value = false;
  }
}

const title = computed(() => (creating.value ? `New agent based on ${props.baseTitle}` : `Edit ${savedName.value}`));
</script>

<template>
  <Modal :title="title" wide @close="emit('close')">
    <div class="editor" data-test="agent-editor" :data-mode="creating ? 'new' : 'edit'">
      <label class="field">Name <input v-model="name" data-test="editor-name" /></label>
      <div class="field">Default model <AgentModelPicker v-if="modelsReady" v-model="model" :models="models" test-id="editor-model" @incomplete="modelIncomplete = $event" />
        <LoadingNote v-else text="Loading the models…" /></div>

      <div class="field">Command line (headless run)
        <div class="chips" data-test="editor-chips">
          <span v-for="(t, i) in launch" :key="`l${i}`" class="arg plain mono" data-test="chip-launch">{{ t }}</span>
          <span v-for="c in before" :key="c.id" class="arg mine mono" data-test="chip-before">
            <input v-if="editingChip?.list === 'before' && editingChip.id === c.id" v-model="editingChip.text" class="chip-input" data-test="chip-edit"
              @keydown.enter.prevent="commitEdit()" @keydown.esc.prevent="editingChip = null" @blur="commitEdit()" />
            <button v-else class="text" title="Edit" data-test="chip-text" @click="startEdit('before', c)">{{ c.text }}</button>
            <button class="x" title="Remove" data-test="chip-remove" @click="removeArg('before', c.id)">×</button>
          </span>
          <span v-for="(g, i) in middle" :key="`g${i}`" class="arg mono" :class="{ locked: g.locked, removed: groupRemoved(g, removed) }"
            data-test="chip-group" :data-locked="g.locked ? 'true' : 'false'" :data-removed="groupRemoved(g, removed) ? 'true' : 'false'"
            :title="g.locked ? `Locked: ${g.reason ?? 'the app depends on it'}` : groupRemoved(g, removed) ? 'Removed (↺ puts it back)' : 'A built-in argument you may remove'">
            <span v-if="g.locked" class="lock" aria-label="locked">🔒</span>
            <span class="tokens">{{ g.tokens.join(' ') }}</span>
            <template v-if="!g.locked">
              <button v-if="groupRemoved(g, removed)" class="x" title="Restore" data-test="chip-restore" @click="toggle(g)">↺</button>
              <button v-else class="x" title="Remove" data-test="chip-remove" @click="toggle(g)">×</button>
            </template>
          </span>
          <span v-for="c in after" :key="c.id" class="arg mine mono" data-test="chip-after">
            <input v-if="editingChip?.list === 'after' && editingChip.id === c.id" v-model="editingChip.text" class="chip-input" data-test="chip-edit"
              @keydown.enter.prevent="commitEdit()" @keydown.esc.prevent="editingChip = null" @blur="commitEdit()" />
            <button v-else class="text" title="Edit" data-test="chip-text" @click="startEdit('after', c)">{{ c.text }}</button>
            <button class="x" title="Remove" data-test="chip-remove" @click="removeArg('after', c.id)">×</button>
          </span>
          <span v-for="(g, i) in tail" :key="`t${i}`" class="arg locked mono" data-test="chip-tail" data-locked="true"
            :title="`Locked: ${g.reason ?? 'the run\'s prompt'}`"><span class="lock" aria-label="locked">🔒</span>{{ g.tokens.join(' ') }}</span>
        </div>
        <div class="adds">
          <input v-model="addBefore" placeholder="Add before (Enter)" data-test="add-before" @keydown.enter.prevent="add('before')" />
          <input v-model="addAfter" placeholder="Add after (Enter)" data-test="add-after" @keydown.enter.prevent="add('after')" />
        </div>
        <p class="muted small">Each entry is one argument exactly as typed (no shell, spaces stay inside it). 🔒 arguments are what the app depends on: hover one for why.</p>
      </div>

      <ul v-if="refusals.length" class="error refusals" data-test="editor-refusals">
        <li v-for="r in refusals" :key="r.argument + r.reason"><code>{{ r.argument }}</code>: {{ r.reason }}</li>
      </ul>

      <div v-if="lines" class="preview" :class="{ stale }" data-test="editor-preview">
        <div data-test="preview-headless"><b>Headless run</b> <code class="mono">{{ lines.headless.argv.join(' ') }}</code></div>
        <div data-test="preview-interactive"><b>Interactive run</b> <code class="mono">{{ lines.interactive.argv.join(' ') }}</code></div>
        <p v-if="stale" class="muted small">The lines update once the refusals above are fixed.</p>
      </div>
      <LoadingNote v-else-if="!error" text="Loading the command line…" />

      <p v-if="error" class="error" data-test="editor-error">{{ error }}</p>
      <div class="row"><span class="grow"></span>
        <button data-test="editor-cancel" :disabled="saving" @click="emit('close')">Cancel</button>
        <button class="primary" data-test="editor-save" :disabled="!canSave" :aria-busy="saving" @click="save"><BusyLabel :busy="saving" text="Saving…">Save</BusyLabel></button>
      </div>
    </div>
  </Modal>
</template>

<style scoped>
.editor { display: flex; flex-direction: column; gap: 10px; }
.field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--muted); }
.field input { color: var(--ink); }
.chips { display: flex; flex-wrap: wrap; gap: 4px; padding: 6px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }
.arg { display: inline-flex; align-items: center; gap: 3px; padding: 1px 6px; border-radius: 9px; background: var(--chip); color: var(--ink); border: 1px solid var(--line-soft); }
.arg.plain { background: transparent; border-color: transparent; color: var(--muted); }
.arg.mine { border-color: var(--accent); }
.arg.locked { color: var(--ink-soft); }
.arg.removed .tokens { text-decoration: line-through; color: var(--muted); }
.lock { font-size: 10px; }
.arg button { border: 0; background: transparent; padding: 0 2px; color: inherit; font: inherit; }
.arg button.x { color: var(--muted); }
.arg button.x:hover { color: var(--bad); }
.chip-input { font-size: 12px; padding: 0 4px; min-width: 120px; }
.adds { display: flex; gap: 8px; }
.adds input { flex: 1; }
.refusals { margin: 0; padding-left: 18px; font-size: 12px; }
.preview { display: flex; flex-direction: column; gap: 4px; font-size: 12px; }
.preview.stale code { opacity: .55; }
.small { font-size: 12px; }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; word-break: break-all; font-size: 12px; }
</style>
