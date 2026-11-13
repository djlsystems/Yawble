<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import type { TeamSummary } from '../../../shared/api';
import Icon from './Icon.vue';
import BusyLabel from './BusyLabel.vue';
import { useImageAttachments, type AttachedImage } from '../imageAttachments';

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
}>();
const emit = defineEmits<{ send: [text: string, images: { name: string; base64: string }[], previews: AttachedImage[]]; stop: []; 'update:cli': [cli: string]; 'update:teamId': [teamId: number | null] }>();

const text = ref('');
const box = ref<HTMLTextAreaElement | null>(null);
const picker = ref<HTMLInputElement | null>(null);
const attach = useImageAttachments();
const canSend = computed(() => (text.value.trim().length > 0 || attach.images.value.length > 0) && !props.running);

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

function key(e: KeyboardEvent) {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    send();
  }
}

function focus() { box.value?.focus(); }
defineExpose({ focus, set: (t: string) => { text.value = t; void nextTick(() => { grow(); focus(); }); } });

watch(text, () => void nextTick(grow));
onMounted(() => { if (props.autofocus) focus(); });
</script>

<template>
  <div class="composer" data-test="composer">
    <div v-if="attach.images.value.length" class="thumbs" data-test="composer-images">
      <span v-for="(im, i) in attach.images.value" :key="i" class="thumb">
        <img :src="im.url" :alt="im.name" :title="im.name" />
        <button class="x" title="Remove" data-test="composer-image-remove" @click="attach.remove(i)">✕</button>
      </span>
    </div>
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
      <button v-else class="round send" :disabled="!canSend" title="Send (Enter)" data-test="composer-send" @click="send"><Icon name="send" :size="18" /></button>
    </div>
  </div>
</template>

<style scoped>
.composer { background: var(--surface); border: 1px solid var(--line); border-radius: 22px; padding: 12px 12px 8px 16px; box-shadow: 0 4px 18px rgba(20, 28, 45, .07); }
.composer:focus-within { border-color: #c4ccda; }
textarea { width: 100%; border: 0; outline: 0; resize: none; background: transparent; font-size: 15px; line-height: 1.5; padding: 4px 0; max-height: 240px; color: var(--ink); }
.bar { display: flex; align-items: center; gap: 8px; margin-top: 4px; }
.pick { display: inline-flex; align-items: center; gap: 4px; color: var(--muted); font-size: 13px; padding: 2px 6px; border-radius: 8px; }
.pick:hover { background: var(--hover); }
.pick select { border: 0; background: transparent; color: var(--ink); font-size: 13px; padding: 2px 0; cursor: pointer; appearance: none; }
.pick select:focus { outline: none; }
.round { width: 34px; height: 34px; border-radius: 50%; display: grid; place-items: center; padding: 0; border: 0; }
.send { background: var(--ink); color: #fff; }
.send:disabled { background: #c9ced8; opacity: 1; }
.stop { background: var(--ink); color: #fff; }
.grow { flex: 1; }
.attach { border: 0; background: transparent; cursor: pointer; }
.thumbs { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 6px; }
.thumb { position: relative; width: 64px; height: 64px; border-radius: 10px; overflow: hidden; border: 1px solid var(--line); }
.thumb img { width: 100%; height: 100%; object-fit: cover; }
.thumb .x { position: absolute; top: 2px; right: 2px; width: 18px; height: 18px; padding: 0; border: 0; border-radius: 50%; background: rgba(0,0,0,.6); color: #fff; font-size: 10px; line-height: 18px; }
.problem { color: var(--bad); font-size: 12px; margin: 2px 0; }
</style>
