<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { BoardCardDetail, CardStatus, LaneId, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { renderMarkdown } from '../markdown';
import MemberAvatar from '../faces/MemberAvatar.vue';
import { displayName } from '../faces/faces';
import { ago, cardFace, StatusColor, StatusLabel } from './cards';
import LoadingNote from '../components/LoadingNote.vue';
import BusyLabel from '../components/BusyLabel.vue';

// A card in full, on the right: its body, its trail (oldest first), and what the person can do. There is no "send an
// instruction" box on purpose: every edit, move or comment is told to the team's Manager, which acts on it.
const props = defineProps<{ id: number; lanes: { id: LaneId; label: string }[]; api?: YawbleApi }>();
const emit = defineEmits<{ close: []; changed: []; openTeam: [teamId: number] }>();
const api = resolveApi(props.api);

const card = ref<BoardCardDetail | null>(null);
const error = ref<string | null>(null);
// The card whose action is running and which one (Save, Move, Comment): on that card the button spins and all three
// are held. Another card opened meanwhile is not held, and the answer does not touch its form.
const busyFor = ref<number | null>(null);
const doingWhat = ref<string | null>(null);
const busy = computed(() => busyFor.value === props.id);
const doing = computed(() => (busy.value ? doingWhat.value : null));
const title = ref('');
const status = ref<CardStatus>('queued');
const lane = ref<LaneId>('todo');
const note = ref('');
const comment = ref('');
const statuses: CardStatus[] = ['queued', 'running', 'blocked', 'needs-decision', 'failed', 'completed', 'handback', 'done'];

const body = computed(() => (card.value?.body ? renderMarkdown(card.value.body) : ''));

async function load() {
  // Another card than the one shown: the panel shows the loading state, not the old card, until it arrives.
  if (card.value && card.value.id !== props.id) card.value = null;
  const id = props.id;
  try {
    const got = await api.call<BoardCardDetail>('board.card', { id });
    if (id !== props.id) return;
    card.value = got;
    title.value = card.value.title;
    status.value = card.value.status;
    lane.value = card.value.lane;
    error.value = null;
  } catch (e) {
    if (id !== props.id) return; // an error about the card the person left
    error.value = errorText(e);
  }
}

async function act(method: string, params: Record<string, unknown>, after?: () => void) {
  if (busyFor.value !== null) return;
  const id = props.id;
  busyFor.value = id;
  doingWhat.value = method;
  error.value = null;
  try {
    const got = await api.call<BoardCardDetail>(method, params);
    emit('changed');
    if (id !== props.id) return; // the person opened another card meanwhile: its detail and form stay as they are
    card.value = got;
    title.value = got.title;
    status.value = got.status;
    lane.value = got.lane;
    after?.();
  } catch (e) { if (id === props.id) error.value = errorText(e); } finally { busyFor.value = null; doingWhat.value = null; }
}

const save = () => act('board.edit', {
  id: props.id,
  title: title.value !== card.value?.title ? title.value : undefined,
  status: status.value !== card.value?.status ? status.value : undefined,
  note: note.value || undefined,
}, () => { note.value = ''; });
const move = () => act('board.move', { id: props.id, lane: lane.value, note: note.value || undefined }, () => { note.value = ''; });
const say = () => act('board.comment', { id: props.id, text: comment.value }, () => { comment.value = ''; });

watch(() => props.id, load, { immediate: true });
defineExpose({ load });
</script>

<template>
  <aside class="panel" data-test="card-panel">
    <header>
      <MemberAvatar v-if="card?.member" :team="card.team" :member="card.member" :state="cardFace(card)" :size="30" />
      <div class="head">
        <div class="ttl">{{ card?.title ?? '' }}</div>
        <div v-if="card" class="sub">
          {{ card.member ? displayName(card.member) : 'Unassigned' }} ·
          <a href="#" @click.prevent="card.teamId && emit('openTeam', card.teamId)">{{ card.team }}</a>
          · card {{ card.id }}<template v-if="card.workflow"> · workflow #{{ card.workflow }}</template>
        </div>
      </div>
      <button class="icon" title="Close" data-test="card-close" @click="emit('close')">✕</button>
    </header>
    <p v-if="error" class="error" data-test="card-error">{{ error }}</p>
    <LoadingNote v-if="!card && !error" text="Loading the card…" />
    <template v-if="card">
      <div class="chips">
        <span class="chip" :style="{ color: StatusColor[card.status], borderColor: StatusColor[card.status] }">{{ StatusLabel[card.status] }}</span>
        <span class="chip">{{ lanes.find((l) => l.id === card!.lane)?.label }}</span>
        <span v-if="card.item" class="chip item" :title="card.itemTitle">{{ card.item }}<template v-if="card.itemTitle">: {{ card.itemTitle }}</template></span>
        <span v-if="card.awaitingManager" class="chip waiting">awaiting Manager</span>
      </div>
      <div v-if="body" class="md body" v-html="body"></div>

      <section class="edit">
        <h4>Change it <span class="muted">(the Manager is told and acts on it)</span></h4>
        <label>Title<input v-model="title" data-test="card-title" maxlength="80" /></label>
        <div class="row2">
          <label>Status
            <select v-model="status" data-test="card-status-select"><option v-for="s in statuses" :key="s" :value="s">{{ StatusLabel[s] }}</option></select>
          </label>
          <label>Lane
            <select v-model="lane" data-test="card-lane"><option v-for="l in lanes" :key="l.id" :value="l.id">{{ l.label }}</option></select>
          </label>
        </div>
        <label>Note <input v-model="note" placeholder="Goes with the edit or the move" data-test="card-note" /></label>
        <div class="buttons">
          <button data-test="card-save" :disabled="busy || (title === card.title && status === card.status)" :aria-busy="doing === 'board.edit'" @click="save"><BusyLabel :busy="doing === 'board.edit'" text="Saving…">Save</BusyLabel></button>
          <button class="primary" data-test="card-move" :disabled="busy || (lane === card.lane && !note)" :aria-busy="doing === 'board.move'" @click="move"><BusyLabel :busy="doing === 'board.move'" text="Moving…">Move</BusyLabel></button>
        </div>
        <label>Comment<textarea v-model="comment" rows="2" placeholder="Ask or tell the Manager about this card" data-test="card-comment"></textarea></label>
        <div class="buttons"><button data-test="card-comment-send" :disabled="busy || !comment.trim()" :aria-busy="doing === 'board.comment'" @click="say"><BusyLabel :busy="doing === 'board.comment'" text="Sending…">Comment</BusyLabel></button></div>
      </section>

      <section class="trail">
        <h4>Trail</h4>
        <ol>
          <li v-for="t in card.trail" :key="t.id" :title="t.kind">
            <span class="who">{{ t.source === 'you' ? 'You' : displayName(t.source) }}</span>
            <span class="kind">{{ t.kind }}</span>
            <span class="muted when">{{ ago(t.at) }}</span>
            <div class="txt">{{ t.text }}</div>
          </li>
          <li v-if="!card.trail.length" class="muted">Nothing yet.</li>
        </ol>
      </section>
    </template>
  </aside>
</template>

<style scoped>
.panel { width: 380px; flex: none; border-left: 1px solid var(--line); background: #fff; display: flex; flex-direction: column; overflow-y: auto; padding: 12px 16px 20px; gap: 10px; }
header { display: flex; gap: 8px; align-items: flex-start; }
.head { flex: 1; min-width: 0; }
.ttl { font-weight: 700; font-size: 15px; overflow-wrap: anywhere; }
.sub { font-size: 12px; color: var(--muted); margin-top: 2px; }
.icon { border: 0; background: transparent; font-size: 14px; }
.chips { display: flex; flex-wrap: wrap; gap: 6px; }
.chip { font-size: 11.5px; border: 1px solid var(--line); border-radius: 12px; padding: 1px 8px; }
.chip.item { font-family: ui-monospace, Consolas, monospace; color: var(--accent); }
.chip.waiting { color: #b91c1c; border-color: #fecaca; background: #fef2f2; }
.body { font-size: 13px; border: 1px solid var(--line-soft); border-radius: 8px; padding: 8px 10px; background: #fafbfc; max-height: 240px; overflow: auto; }
h4 { margin: 6px 0; font-size: 13px; }
.edit label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); margin-bottom: 6px; }
.edit input, .edit select, .edit textarea { font: inherit; font-size: 13px; padding: 5px 7px; border: 1px solid var(--line); border-radius: 6px; color: var(--ink); }
.row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.buttons { display: flex; gap: 6px; justify-content: flex-end; margin-bottom: 8px; }
.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
.trail ol { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.trail li { font-size: 12.5px; border-left: 2px solid var(--line); padding-left: 8px; }
.who { font-weight: 600; margin-right: 6px; }
.kind { font-size: 11px; color: var(--accent); margin-right: 6px; }
.when { font-size: 11px; }
.txt { white-space: pre-wrap; overflow-wrap: anywhere; color: var(--ink-soft); }
.muted { color: var(--muted); font-weight: 400; }
.error { color: var(--bad); font-size: 13px; }
</style>
