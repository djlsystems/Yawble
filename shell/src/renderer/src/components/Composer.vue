<script setup lang="ts">
import { vChange } from '../remoteAccess';
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import type { TeamSummary } from '../../../shared/api';
import Icon from './Icon.vue';
import BusyLabel from './BusyLabel.vue';
import { useImageAttachments, type AttachedImage } from '../imageAttachments';
import { usePhone } from '../phone';
import { draftOf, keepDraft } from '../conciergeDrafts';

// The Concierge's message box: grows with the text, Enter sends (Shift+Enter: a new line). Below the text: the team in
// focus and the agent that answers; while an answer is running, Send becomes Stop.
export interface AgentOption { id: string; label: string; ready: boolean }

const props = defineProps<{
  teams: TeamSummary[];
  agents: AgentOption[];
  /** The agent CLIs have been looked for (an empty list then means none is installed). */
  agentsLoaded?: boolean;
  cli: string;
  teamId: number | null;
  running: boolean;
  /** Stop was clicked and the core has not answered yet: the Stop button is held with a spinner. */
  stopping?: boolean;
  placeholder?: string;
  autofocus?: boolean;
  /** A line about the chat (the page shows it under the box on the desktop); on a phone it folds out with the pickers. */
  note?: string;
  /** The chat this box writes to: its unsent text is kept under this key and shown again when the box is rebuilt. */
  draftKey?: string;
}>();
const emit = defineEmits<{ send: [text: string, images: { name: string; base64: string }[], previews: AttachedImage[]]; stop: []; 'update:cli': [cli: string]; 'update:teamId': [teamId: number | null]; pickers: [open: boolean] }>();

const text = ref(props.draftKey ? draftOf(props.draftKey) : '');
const box = ref<HTMLTextAreaElement | null>(null);
const picker = ref<HTMLInputElement | null>(null);
const attach = useImageAttachments();
// A phone: the box takes the width its page gives it (ConciergeView), 16 px text (a phone browser zooms into a
// smaller field), tap-sized pickers and buttons; Enter makes a new line there (the keyboard's own key), Send sends.
// No autofocus on a phone: it would bring the keyboard up over a screen the person has only just opened.
const phone = usePhone();
const canSend = computed(() => (text.value.trim().length > 0 || attach.images.value.length > 0) && !props.running);
// On a phone the team and agent pickers (and the note) fold behind one button, so the idle box is one row.
const pickersOpen = ref(false);
// The page folds its other dock rows away while they are open in landscape (ConciergeView's watch row).
watch(pickersOpen, (open) => emit('pickers', open));
const noAgents = computed(() => (props.agentsLoaded ? 'No agent CLI found (Settings > Agents)' : 'Finding agents…'));
const pickersTitle = computed(() => {
  const team = props.teams.find((t) => t.id === props.teamId)?.name ?? 'All teams';
  const agent = props.agents.find((a) => a.id === props.cli)?.label ?? props.cli;
  return `Team: ${team} · Agent: ${agent}`;
});
function pickTeam(e: Event) { const v = (e.target as HTMLSelectElement).value; emit('update:teamId', v ? Number(v) : null); }
function pickCli(e: Event) { emit('update:cli', (e.target as HTMLSelectElement).value); }

function grow() {
  const el = box.value;
  if (!el) return;
  el.style.height = 'auto';
  el.style.height = Math.min(el.scrollHeight, 240) + 'px';
}

function send() {
  if (!canSend.value) return;
  emit('send', text.value.trim(), attach.payload(), [...attach.images.value]);
  text.value = '';
  attach.clear();
  void nextTick(grow);
}

/** Empties the box (the attached images stay: each has its own Remove). */
function clear() {
  text.value = '';
  void nextTick(() => { grow(); focus(); });
}

// Escape pressed twice within this time empties the box.
const DOUBLE_ESCAPE_MS = 600;
let lastEscape = 0;

function key(e: KeyboardEvent) {
  if (e.key === 'Escape' && !e.isComposing) {
    const now = Date.now();
    if (text.value && now - lastEscape <= DOUBLE_ESCAPE_MS) { lastEscape = 0; clear(); }
    else lastEscape = now;
    return;
  }
  lastEscape = 0;
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && !phone.value) {
    e.preventDefault();
    send();
  }
}

function focus() { box.value?.focus(); }
defineExpose({ focus, set: (t: string) => { text.value = t; void nextTick(() => { grow(); focus(); }); } });

watch(text, (t) => { if (props.draftKey) keepDraft(props.draftKey, t); void nextTick(grow); });
// Another chat in the same box: its own unsent text.
watch(() => props.draftKey, (k) => { text.value = k ? draftOf(k) : ''; });
onMounted(() => { grow(); if (props.autofocus && !phone.value) focus(); });
</script>

<template>
  <div class="composer" :class="{ phone }" data-test="composer">
    <div v-if="attach.images.value.length" class="thumbs" data-test="composer-images">
      <span v-for="(im, i) in attach.images.value" :key="i" class="thumb">
        <img :src="im.url" :alt="im.name" :title="im.name" />
        <button class="x" title="Remove" data-test="composer-image-remove" @click="attach.remove(i)">✕</button>
      </span>
    </div>
    <!-- A phone: ONE row (the pickers' button, the text, Attach, Send); the team and agent pickers and the note
         fold out above it, each picker on its own full-width row. -->
    <template v-if="phone">
      <div v-if="pickersOpen" class="pickers" data-test="composer-pickers">
        <label class="row-pick"><span class="row-label"><Icon name="teams" :size="15" />Team</span>
          <select :value="teamId ?? ''" data-test="composer-team" @change="pickTeam">
            <option value="">All teams</option>
            <option v-for="t in teams" :key="t.id" :value="t.id">{{ t.name }}</option>
          </select>
        </label>
        <label class="row-pick"><span class="row-label"><Icon name="spark" :size="15" />Agent</span>
          <select :value="cli" data-test="composer-agent" @change="pickCli">
            <option v-if="!agents.length" :value="cli" data-test="composer-no-agents">{{ noAgents }}</option>
            <option v-for="a in agents" :key="a.id" :value="a.id" :disabled="!a.ready">{{ a.label }}{{ a.ready ? '' : ' (not signed in)' }}</option>
          </select>
        </label>
        <button v-if="text" class="clear-row" data-test="composer-clear" @click="clear">Clear the text</button>
        <p v-if="note" class="note" data-test="composer-note">{{ note }}</p>
      </div>
      <p v-if="attach.problem.value" class="problem" data-test="composer-image-problem">{{ attach.problem.value }}</p>
      <div class="bar">
        <button class="pick toggle" :class="{ on: pickersOpen }" :title="pickersTitle" :aria-label="pickersTitle" :aria-expanded="pickersOpen"
          data-test="composer-pickers-toggle" @click="pickersOpen = !pickersOpen"><Icon name="teams" :size="17" /></button>
        <textarea ref="box" v-model="text" rows="1" :placeholder="placeholder ?? 'Ask the Concierge…'" data-test="composer-text" @keydown="key" @paste="attach.onPaste" />
        <button class="pick attach" title="Attach an image (or paste one)" data-test="composer-attach" @click="picker?.click()"><Icon name="image" :size="15" /></button>
        <input ref="picker" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden data-test="composer-attach-input"
          @change="attach.add(($event.target as HTMLInputElement).files ?? []); ($event.target as HTMLInputElement).value = ''" />
        <button v-if="running" class="round stop" :title="stopping ? 'Stopping…' : 'Stop the answer'" data-test="composer-stop" :disabled="stopping" :aria-busy="stopping"
          @click="emit('stop')"><BusyLabel :busy="!!stopping"><Icon name="stop" :size="16" /></BusyLabel></button>
        <button v-else class="round send" :disabled="!canSend" title="Send" v-change data-test="composer-send" @click="send"><Icon name="send" :size="18" /></button>
      </div>
    </template>
    <template v-else>
    <textarea ref="box" v-model="text" rows="1" :placeholder="placeholder ?? 'Ask the Concierge…'" data-test="composer-text" @keydown="key" @paste="attach.onPaste" />
    <p v-if="attach.problem.value" class="problem" data-test="composer-image-problem">{{ attach.problem.value }}</p>
    <div class="bar">
      <label class="pick" title="The team in focus for this chat">
        <Icon name="teams" :size="15" />
        <select :value="teamId ?? ''" data-test="composer-team"
          @change="emit('update:teamId', ($event.target as HTMLSelectElement).value ? Number(($event.target as HTMLSelectElement).value) : null)">
          <option value="">All teams</option>
          <option v-for="t in teams" :key="t.id" :value="t.id">{{ t.name }}</option>
        </select>
      </label>
      <button class="pick attach" title="Attach an image (or paste one)" data-test="composer-attach" @click="picker?.click()"><Icon name="image" :size="15" /></button>
      <input ref="picker" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden data-test="composer-attach-input"
        @change="attach.add(($event.target as HTMLInputElement).files ?? []); ($event.target as HTMLInputElement).value = ''" />
      <button v-if="text" class="pick clear" title="Clear the text (Esc twice)" data-test="composer-clear" @click="clear">Clear</button>
      <span class="grow" />
      <label class="pick" title="The agent that answers">
        <select :value="cli" data-test="composer-agent" @change="emit('update:cli', ($event.target as HTMLSelectElement).value)">
          <option v-if="!agents.length" :value="cli" data-test="composer-no-agents">{{ agentsLoaded ? 'No agent CLI found (Settings > Agents)' : 'Finding agents…' }}</option>
          <option v-for="a in agents" :key="a.id" :value="a.id" :disabled="!a.ready">{{ a.label }}{{ a.ready ? '' : ' (not signed in)' }}</option>
        </select>
        <Icon name="chevron" :size="14" />
      </label>
      <button v-if="running" class="round stop" :title="stopping ? 'Stopping…' : 'Stop the answer'" data-test="composer-stop" :disabled="stopping" :aria-busy="stopping"
        @click="emit('stop')"><BusyLabel :busy="!!stopping"><Icon name="stop" :size="16" /></BusyLabel></button>
      <button v-else class="round send" :disabled="!canSend" title="Send (Enter)" v-change data-test="composer-send" @click="send"><Icon name="send" :size="18" /></button>
    </div>
    </template>
  </div>
</template>

<style scoped>
.composer { background: var(--surface); border: 1px solid var(--line); border-radius: 22px; padding: 12px 12px 8px 16px; box-shadow: 0 4px 18px var(--shadow-soft); }
.composer:focus-within { border-color: var(--line-strong); }
textarea { width: 100%; border: 0; outline: 0; resize: none; background: transparent; font-size: 15px; line-height: 1.5; padding: 4px 0; max-height: 240px; color: var(--ink); }
.bar { display: flex; align-items: center; gap: 8px; margin-top: 4px; }
.pick { display: inline-flex; align-items: center; gap: 4px; color: var(--muted); font-size: 13px; padding: 2px 6px; border-radius: 8px; }
.pick:hover { background: var(--hover); }
.pick select { border: 0; background: transparent; color: var(--ink); font-size: 13px; padding: 2px 0; cursor: pointer; appearance: none; }
.pick select:focus { outline: none; }
.round { width: 34px; height: 34px; border-radius: 50%; display: grid; place-items: center; padding: 0; border: 0; }
.send { background: var(--ink); color: var(--surface); }
.send:disabled { background: var(--line-strong); opacity: 1; }
.stop { background: var(--ink); color: var(--surface); }
.grow { flex: 1; }
.attach { border: 0; background: transparent; cursor: pointer; }
.clear { border: 0; background: transparent; cursor: pointer; }
.clear-row { min-height: 44px; border: 1px solid var(--line); background: transparent; border-radius: 10px; font-size: 14px; color: var(--ink); }
.thumbs { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 6px; }
.thumb { position: relative; width: 64px; height: 64px; border-radius: 10px; overflow: hidden; border: 1px solid var(--line); }
.thumb img { width: 100%; height: 100%; object-fit: cover; }
.thumb .x { position: absolute; top: 2px; right: 2px; width: 18px; height: 18px; padding: 0; border: 0; border-radius: 50%; background: var(--scrim); color: var(--on-scrim); font-size: 10px; line-height: 18px; }
.problem { color: var(--bad); font-size: 12px; margin: 2px 0; }
/* A phone: full width, 16 px text, the box no taller than a third of the screen (the keyboard takes much of it).
   Idle it is ONE 44 px row (pickers' button, text, Attach, Send) in 5 px of padding: 56 px with the border, so
   the thread keeps the screen (a 263 px landscape frame left the messages 69 px under the old two-row box). */
.composer.phone { width: 100%; border-radius: 18px; padding: 5px 6px; }
/* The box is a 44 px tap target even with one row (grow() sets its height to scrollHeight: border-box, padding included). */
.phone textarea { font-size: 16px; max-height: 33dvh; min-height: 44px; box-sizing: border-box; padding: 10px 4px; flex: 1; min-width: 0; width: auto; }
/* The buttons stay at the bottom as the text grows. */
.phone .bar { gap: 4px; margin-top: 0; align-items: flex-end; }
.phone .pick { min-height: 44px; padding: 0 6px; min-width: 0; }
/* The Attach button is an icon only: a 44 x 44 square that the bar never squeezes (the pickers' min-width: 0 is for the selects). */
.phone .pick.attach { min-width: 44px; width: 44px; height: 44px; padding: 0; flex: none; justify-content: center; }
.phone .pick.toggle { min-width: 44px; width: 44px; height: 44px; padding: 0; flex: none; justify-content: center; border: 0; background: transparent; cursor: pointer; }
.phone .pick.toggle.on { background: var(--selected); color: var(--ink); }
/* The folded-out pickers: each on its own full-width row, the select taking all but its label (16 px, 44 px tall), so a
   long team name shows whole; the note under them. */
.pickers { display: flex; flex-direction: column; gap: 4px; padding: 2px 2px 6px; margin-bottom: 4px; border-bottom: 1px solid var(--line); }
.row-pick { display: flex; align-items: center; gap: 8px; width: 100%; color: var(--muted); font-size: 13px; }
.row-label { display: inline-flex; align-items: center; gap: 4px; flex: none; width: 64px; }
.row-pick select { flex: 1; min-width: 0; width: 100%; font-size: 16px; min-height: 44px; box-sizing: border-box; color: var(--ink); text-overflow: ellipsis; }
.note { color: var(--muted); font-size: 12px; line-height: 1.4; margin: 2px 2px 0; }
/* A phone in landscape (263 px tall): the two pickers side by side on one row, the note under them, and the
   panel never taller than 40 % of the screen (it scrolls past that), so the text row under it always stays on screen. */
@media (orientation: landscape) {
  .pickers { flex-direction: row; flex-wrap: wrap; gap: 4px 8px; box-sizing: border-box; max-height: 40dvh; overflow-y: auto; }
  .row-pick { flex: 1 1 0; width: auto; min-width: 0; }
  .row-label { width: auto; }
  .note { flex: 1 0 100%; }
}
.phone .round { width: 44px; height: 44px; flex: none; }
.phone .thumb { width: 88px; height: 88px; }
.phone .thumb .x { top: 0; right: 0; width: 44px; height: 44px; border-radius: 0 0 0 12px; font-size: 13px; line-height: 44px; }
</style>
