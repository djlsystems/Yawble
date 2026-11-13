<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ConciergeMessage, ConciergeTurnEvent, ConciergeUpdateEvent, ConciergeWatch, Conversation, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import ChatMessage from './ChatMessage.vue';
import Composer, { type AgentOption } from './Composer.vue';
import Icon from './Icon.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

// The Concierge (the app's home): a new chat (a greeting and the message box) or an open conversation. Each message
// is answered by one headless run of the chosen agent, which sees every team and acts through its tools.
const props = defineProps<{
  conversationId: string | null;
  teams: TeamSummary[];
  agents: AgentOption[];
  defaultCli: string;
  focusTeamId: number | null;
  api?: YawbleApi;
  /** Text to start a new chat with (a team's question to discuss), put in the box for the person to finish and send. */
  draft?: string | null;
}>();
const emit = defineEmits<{ opened: [id: string]; changed: []; drafted: [] }>();
function applyDraft() {
  if (!props.draft || props.conversationId) return;
  const text = props.draft;
  void nextTick(() => { composer.value?.set(text); emit('drafted'); });
}
watch(() => props.draft, applyDraft);
onMounted(applyDraft);
const api = resolveApi(props.api);

const conversation = ref<Conversation | null>(null);
const pending = ref<ConciergeMessage | null>(null); // the person's message, shown before the core has it
const turn = ref<ConciergeTurnEvent | null>(null);
const error = ref<string | null>(null);
const cli = ref(props.defaultCli);
const teamId = ref<number | null>(props.focusTeamId);
const scroller = ref<HTMLElement | null>(null);
const composer = ref<InstanceType<typeof Composer> | null>(null);
let off: (() => void) | null = null;
// Stop and the watch chips' buttons await the core: held (and spinning) until it answers.
const work = useBusy();

const running = computed(() => turn.value?.state === 'running');
const messages = computed(() => [...(conversation.value?.messages ?? []), ...(pending.value ? [pending.value] : [])]);
const greeting = computed(() => {
  const h = new Date().getHours();
  return h < 5 ? 'Working late' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
});
const suggestions = computed(() => {
  const team = props.teams.find((t) => t.id === teamId.value)?.name;
  return team
    ? [`What is ${team} working on right now?`, `Is anything in ${team} waiting on me?`, `Summarize what ${team} did today`]
    : ['What is every team doing right now?', 'Is anything waiting on me?', 'Start a new team for a project'];
});

async function load() {
  error.value = null;
  if (!props.conversationId) {
    conversation.value = null;
    turn.value = null;
    cli.value = props.defaultCli;
    teamId.value = props.focusTeamId;
    return;
  }
  // Another chat than the one shown: clear it, so the thread shows the loading state, not the old chat, until it arrives.
  if (conversation.value && conversation.value.id !== props.conversationId) { conversation.value = null; turn.value = null; }
  const id = props.conversationId;
  try {
    const c = await api.call<Conversation>('concierge.get', { id });
    if (id !== props.conversationId) return; // the person opened another chat meanwhile
    conversation.value = c;
    pending.value = null;
    turn.value = c.turn;
    cli.value = c.cli;
    teamId.value = c.teamId;
    if (c.unread > 0) void markRead();
    await scrollDown();
  } catch (e) {
    if (id !== props.conversationId) return; // an error about the chat the person left: not shown on this one
    error.value = errorText(e);
  }
}

async function scrollDown() {
  await nextTick();
  const el = scroller.value;
  if (el) el.scrollTop = el.scrollHeight;
}

async function send(text: string, images: { name: string; base64: string }[] = []) {
  error.value = null;
  const shown = images.length ? `${text}${text ? '\n\n' : ''}📎 ${images.map((i) => i.name).join(', ')}` : text;
  pending.value = { id: -1, role: 'user', text: shown, at: new Date().toISOString(), state: null, error: null, tools: [], cli: null };
  turn.value = { conversationId: props.conversationId ?? '', state: 'running', text: '', tools: [], error: null };
  await scrollDown();
  try {
    const r = await api.call<{ id: string }>('concierge.send', {
      id: props.conversationId, text, cli: cli.value, teamId: teamId.value, ...(images.length ? { images } : {}),
    });
    if (!props.conversationId) emit('opened', r.id);
    else emit('changed');
  } catch (e) {
    error.value = errorText(e);
    pending.value = null;
    turn.value = null;
  }
}

function stop() {
  if (!props.conversationId) return;
  const id = props.conversationId;
  return work.run('stop', async () => { try { await api.call('concierge.stop', { id }); } catch (e) { error.value = errorText(e); } });
}

async function changeTeam(id: number | null) {
  teamId.value = id;
  if (!props.conversationId) return;
  try { await api.call('concierge.setTeam', { id: props.conversationId, teamId: id }); emit('changed'); } catch (e) { error.value = errorText(e); }
}

async function markRead() {
  if (!props.conversationId) return;
  try { await api.call('concierge.read', { id: props.conversationId }); emit('changed'); } catch { /* the dot stays; harmless */ }
}

/** An update the core posted while this chat is open: show it and count it as seen. */
function onUpdate(e: ConciergeUpdateEvent) {
  if (!props.conversationId || e.conversationId !== props.conversationId || !conversation.value) return;
  if (!conversation.value.messages.some((m) => m.id === e.message.id)) conversation.value.messages.push(e.message);
  // A workflow watch ends with its workflow; reload the watches when one may have ended.
  if (e.message.kind === 'done' || e.message.kind === 'expired') void refreshWatches();
  void markRead();
  void scrollDown();
}

async function refreshWatches() {
  if (!props.conversationId || !conversation.value) return;
  try {
    const c = await api.call<Conversation>('concierge.get', { id: props.conversationId });
    if (conversation.value) conversation.value.watches = c.watches;
  } catch { /* keep what is shown */ }
}

const unwatch = (w: ConciergeWatch) => work.run(`watch-${w.id}`, () => doUnwatch(w), 'stop');
async function doUnwatch(w: ConciergeWatch) {
  try {
    await api.call('concierge.unwatch', { id: props.conversationId, watchId: w.id });
    if (conversation.value) conversation.value.watches = conversation.value.watches.filter((x) => x.id !== w.id);
    emit('changed');
  } catch (e) { error.value = errorText(e); }
}

const toggleReview = (w: ConciergeWatch) => work.run(`watch-${w.id}`, () => doToggleReview(w), 'review');
async function doToggleReview(w: ConciergeWatch) {
  try {
    const updated = await api.call<ConciergeWatch>('concierge.setWatch', { id: props.conversationId, watchId: w.id, followUp: !w.followUp });
    w.followUp = updated.followUp;
  } catch (e) { error.value = errorText(e); }
}

const watchTitle = (w: ConciergeWatch) =>
  `${w.workflow ? `Workflow ${w.workflow} of ${w.team}` : `Team ${w.team}`}: ${w.what}\n` +
  `Until ${new Date(w.expiresAt).toLocaleString()}.\n` +
  (w.followUp ? 'The Concierge reviews each update (an agent turn).' : 'Updates are posted here; the Concierge is not woken for them.');

function onTurn(e: ConciergeTurnEvent) {
  if (!props.conversationId || e.conversationId !== props.conversationId) return;
  // A review turn the core started for a watch: show it running like any other.
  if (e.state === 'running' && !turn.value) void refreshWatches();
  turn.value = e;
  void scrollDown();
  if (e.state !== 'running') {
    turn.value = null;
    void load();
    emit('changed');
  }
}

onMounted(() => {
  off = api.onEvent((e) => {
    if (e.event === 'concierge.turn') onTurn(e.data as ConciergeTurnEvent);
    else if (e.event === 'concierge.update') onUpdate(e.data as ConciergeUpdateEvent);
  });
  void load();
});
onBeforeUnmount(() => off?.());
// A new chat that just got its id: the core already has the person's message and the running turn, so loading it
// replaces what is on screen without a gap (and catches a turn that ended before the id arrived).
watch(() => props.conversationId, () => void load());
watch(() => props.focusTeamId, (id) => { if (!props.conversationId) teamId.value = id; });
watch(() => props.defaultCli, (c) => { if (!props.conversationId) cli.value = c; });
</script>

<template>
  <section class="concierge">
    <!-- A new chat -->
    <div v-if="!conversationId && !pending" class="hero" data-test="concierge-hero">
      <h1><span class="mark"><Icon name="spark" :size="22" /></span>{{ greeting }}</h1>
      <p class="sub">What should your teams work on?</p>
      <Composer ref="composer" v-model:cli="cli" :team-id="teamId" :teams="teams" :agents="agents" :running="false" autofocus
        @update:team-id="changeTeam" @send="send" />
      <div class="suggest">
        <button v-for="s in suggestions" :key="s" class="chip-btn" data-test="concierge-suggestion" @click="composer?.set(s)">{{ s }}</button>
      </div>
      <p v-if="error" class="error center">{{ error }}</p>
    </div>

    <!-- A conversation -->
    <template v-else>
      <div ref="scroller" class="scroll" data-test="concierge-thread">
        <div class="thread">
          <LoadingNote v-if="conversationId && !conversation && !pending && !error" text="Loading the chat…" />
          <ChatMessage v-for="m in messages" :key="m.id" :role="m.role" :text="m.text" :tools="m.tools" :state="m.state" :error="m.error" :cli="m.cli"
            :kind="m.kind" :at="m.at" />
          <ChatMessage v-if="running" role="assistant" :text="turn?.text ?? ''" :tools="turn?.tools ?? []" :cli="cli" live />
          <p v-if="error" class="error">{{ error }}</p>
        </div>
      </div>
      <div class="dock">
        <div v-if="conversation?.watches?.length" class="watches" data-test="watches">
          <span class="w-label"><Icon name="eye" :size="13" />Keeping tabs on</span>
          <span v-for="w in conversation.watches" :key="w.id" class="watch" data-test="watch" :title="watchTitle(w)">
            <strong>{{ w.team }}</strong><span class="w-what">{{ w.what }}</span>
            <button class="w-btn" :class="{ on: w.followUp }" data-test="watch-review" :aria-pressed="w.followUp"
              :title="w.followUp ? 'The Concierge reviews each update: click to just post updates' : 'Click to have the Concierge review each update (an agent turn)'"
              :disabled="work.is(`watch-${w.id}`)" @click="toggleReview(w)"><BusyLabel :busy="work.is(`watch-${w.id}`, 'review')"><Icon name="spark" :size="12" /></BusyLabel></button>
            <button class="w-btn" data-test="watch-stop" :title="work.is(`watch-${w.id}`, 'stop') ? 'Stopping…' : 'Stop watching'" :disabled="work.is(`watch-${w.id}`)"
              @click="unwatch(w)"><BusyLabel :busy="work.is(`watch-${w.id}`, 'stop')">×</BusyLabel></button>
          </span>
        </div>
        <Composer v-model:cli="cli" :team-id="teamId" :teams="teams" :agents="agents" :running="running" :stopping="work.is('stop')" placeholder="Reply to the Concierge…" autofocus
          @update:team-id="changeTeam" @send="send" @stop="stop" />
        <p class="hint">The Concierge sees every team and acts through the Managers. It can make mistakes; check what it did on the team.</p>
      </div>
    </template>
  </section>
</template>

<style scoped>
.concierge { height: 100%; display: flex; flex-direction: column; min-height: 0; }
.hero { margin: auto; width: min(760px, 100% - 48px); padding: 0 0 12vh; }
.hero h1 { display: flex; align-items: center; justify-content: center; gap: 10px; font-size: 30px; font-weight: 600; margin: 0 0 6px; letter-spacing: -.01em; }
.mark { width: 36px; height: 36px; border-radius: 50%; background: var(--brand); color: #fff; display: grid; place-items: center; }
.sub { text-align: center; color: var(--muted); font-size: 16px; margin: 0 0 22px; }
.suggest { display: flex; flex-wrap: wrap; gap: 8px; justify-content: center; margin-top: 16px; }
.chip-btn { border: 1px solid var(--line); background: var(--surface); border-radius: 18px; padding: 6px 14px; font-size: 13px; color: var(--ink-soft); }
.chip-btn:hover { background: var(--hover); border-color: var(--line); }
.scroll { flex: 1; overflow: auto; min-height: 0; }
.thread { width: min(780px, 100% - 48px); margin: 0 auto; padding: 28px 0 12px; }
.dock { width: min(780px, 100% - 48px); margin: 0 auto; padding: 4px 0 14px; }
.watches { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0 0 8px; font-size: 12px; }
.w-label { display: inline-flex; align-items: center; gap: 4px; color: var(--muted); }
.watch { display: inline-flex; align-items: center; gap: 5px; background: var(--chip); border-radius: 12px; padding: 2px 4px 2px 10px; max-width: 100%; }
.w-what { color: var(--ink-soft); max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.w-btn { border: 0; background: transparent; color: var(--muted); border-radius: 10px; width: 20px; height: 20px; display: grid; place-items: center; padding: 0; font-size: 14px; line-height: 1; cursor: pointer; }
.w-btn:hover { background: var(--hover); color: var(--ink); }
.w-btn.on { color: var(--brand); }
.hint { text-align: center; color: var(--muted); font-size: 12px; margin: 8px 0 0; }
.center { text-align: center; }
</style>
