<script setup lang="ts">
import { teamState, teamStateLabel } from '../teamState';
import wordmark from '../assets/yawble-logo-light.png';
import mark from '../assets/yawble-mark.png';
import { nextTick, ref } from 'vue';
import type { ConversationSummary, RunSlots, TeamSummary } from '../../../shared/api';
import Icon from './Icon.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// The app's left pane, like the chat apps people know: New chat, the places (Concierge, Teams, and what comes next),
// the teams, and the person's chats with the Concierge; settings at the bottom.
export type View = 'concierge' | 'documents' | 'backlog' | 'teams' | 'board' | 'monitor' | 'settings';
export type MenuItem = 'agents' | 'skills' | 'globalPolicy' | 'integrations' | 'system' | 'link';

defineProps<{
  view: View;
  conversations: ConversationSummary[];
  teams: TeamSummary[];
  conversationId: string | null;
  teamId: number | null;
  slots: RunSlots | null;
  remote: boolean;
  /** Folded: a narrow rail of icons (the places, New chat, Settings), each named on hover. */
  rail?: boolean;
  /** The teams / the chats have not had their first answer yet. */
  loading?: { teams: boolean; chats: boolean };
  /** The chat being deleted (its row shows Deleting… until the core answers). */
  removing?: string | null;
}>();
const emit = defineEmits<{
  newChat: []; openConversation: [id: string]; openTeam: [id: number]; go: [view: View];
  rename: [id: string, title: string]; remove: [id: string]; newTeam: []; collapse: []; expand: [];
}>();

const railPlaces: { view: View; label: string; icon: string }[] = [
  { view: 'concierge', label: 'Concierge', icon: 'spark' }, { view: 'documents', label: 'Documents', icon: 'docs' },
  { view: 'backlog', label: 'Backlog', icon: 'backlog' }, { view: 'teams', label: 'Teams', icon: 'teams' },
  { view: 'board', label: 'Board', icon: 'board' }, { view: 'monitor', label: 'Monitor', icon: 'monitor' },
];

const editing = ref<string | null>(null);
const draft = ref('');
const confirming = ref<string | null>(null);
const editBox = ref<HTMLInputElement[] | null>(null);

function startRename(c: ConversationSummary) {
  editing.value = c.id;
  draft.value = c.title;
  void nextTick(() => editBox.value?.[0]?.select());
}
function finishRename(id: string) {
  if (editing.value !== id) return;
  editing.value = null;
  if (draft.value.trim()) emit('rename', id, draft.value.trim());
}
</script>

<template>
  <aside v-if="rail" class="side rail" data-test="sidebar-rail">
    <button class="rail-btn logo-btn" title="Show the sidebar" data-test="sidebar-expand" @click="emit('expand')"><img :src="mark" alt="Yawble" /></button>
    <button class="rail-btn" title="New chat" data-test="rail-new-chat" @click="emit('newChat')"><Icon name="newChat" /></button>
    <nav class="rail-places">
      <button v-for="p in railPlaces" :key="p.view" class="rail-btn" :class="{ on: view === p.view }" :title="p.label" :data-test="`rail-${p.view}`"
        @click="emit('go', p.view)">
        <Icon :name="p.icon" />
        <span v-if="p.view === 'teams' && slots?.running" class="rail-badge" title="agents running">{{ slots.running }}</span>
      </button>
    </nav>
    <span class="grow" />
    <button class="rail-btn" :class="{ on: view === 'settings' }" title="Settings" data-test="rail-settings" @click="emit('go', 'settings')"><Icon name="settings" /></button>
  </aside>
  <aside v-else class="side" data-test="sidebar">
    <div class="brand">
      <img class="logo" :src="wordmark" alt="Yawble" />
      <span class="grow" />
      <button class="ghost icon-btn" title="Hide the sidebar" data-test="sidebar-collapse" @click="emit('collapse')"><Icon name="sidebar" /></button>
    </div>

    <button class="item new" data-test="new-chat" @click="emit('newChat')"><Icon name="newChat" />New chat</button>

    <nav class="places">
      <button class="item" :class="{ on: view === 'concierge' }" data-test="nav-concierge" @click="emit('go', 'concierge')"><Icon name="spark" />Concierge</button>
      <button class="item" :class="{ on: view === 'documents' }" data-test="nav-documents" @click="emit('go', 'documents')"><Icon name="docs" />Documents</button>
      <button class="item" :class="{ on: view === 'backlog' }" data-test="nav-backlog" @click="emit('go', 'backlog')"><Icon name="backlog" />Backlog</button>
      <button class="item" :class="{ on: view === 'teams' }" data-test="nav-teams" @click="emit('go', 'teams')"><Icon name="teams" />Teams<span v-if="slots?.running" class="badge" title="agents running">{{ slots.running }}</span></button>
      <button class="item" :class="{ on: view === 'board' }" data-test="nav-board" @click="emit('go', 'board')"><Icon name="board" />Board</button>
      <button class="item" :class="{ on: view === 'monitor' }" data-test="nav-monitor" @click="emit('go', 'monitor')"><Icon name="monitor" />Monitor</button>
    </nav>

    <div class="lists">
      <h3>Teams</h3>
      <LoadingNote v-if="loading?.teams" class="side-note" text="Loading teams…" />
      <button v-for="t in teams" :key="t.id" class="item small" :class="{ on: view === 'teams' && teamId === t.id }" data-test="team-item" @click="emit('openTeam', t.id)">
        <span class="dot" :class="{ busy: t.running > 0 }" :data-state="teamState(t)" :title="teamStateLabel[teamState(t)]" />
        <span class="label">{{ t.name }}</span>
        <span v-if="t.running || t.queued" class="muted tiny" data-test="team-counts">{{ t.running }} running · {{ t.queued }} queued</span>
      </button>
      <button class="item small muted" data-test="new-team" @click="emit('newTeam')"><Icon name="plus" :size="15" />New team</button>

      <h3>Chats</h3>
      <LoadingNote v-if="loading?.chats" class="side-note" text="Loading chats…" />
      <p v-else-if="!conversations.length" class="empty muted">Your chats with the Concierge appear here.</p>
      <div v-for="c in conversations" :key="c.id" class="chat" :class="{ on: view === 'concierge' && conversationId === c.id }" data-test="chat-item">
        <input v-if="editing === c.id" ref="editBox" v-model="draft" class="rename" data-test="chat-rename-box"
          @keydown.enter="finishRename(c.id)" @keydown.esc="editing = null" @blur="finishRename(c.id)" />
        <template v-else-if="confirming === c.id">
          <span class="label">Delete this chat?</span>
          <button class="mini danger" data-test="chat-delete-yes" :disabled="!!removing" :aria-busy="removing === c.id" @click="emit('remove', c.id)"><BusyLabel :busy="removing === c.id" text="Deleting…">Delete</BusyLabel></button>
          <button class="mini" data-test="chat-delete-no" @click="confirming = null">Keep</button>
        </template>
        <template v-else>
          <button class="open" :title="c.title" @click="emit('openConversation', c.id)">
            <span v-if="c.running" class="dot busy" title="answering" />
            <span class="label" :class="{ strong: c.unread > 0 }">{{ c.title }}</span>
            <span v-if="c.unread > 0" class="unread" data-test="chat-unread" :title="`${c.unread} new update${c.unread === 1 ? '' : 's'}`">{{ c.unread }}</span>
            <Icon v-else-if="c.watching > 0" name="eye" :size="13" class="watching" data-test="chat-watching" :title="`Keeping tabs on ${c.watching} thing${c.watching === 1 ? '' : 's'}`" />
          </button>
          <span class="actions">
            <button class="ghost icon-btn" title="Rename" data-test="chat-rename" @click="startRename(c)"><Icon name="edit" :size="14" /></button>
            <button class="ghost icon-btn" title="Delete" data-test="chat-delete" @click="confirming = c.id"><Icon name="trash" :size="14" /></button>
          </span>
        </template>
      </div>
    </div>

    <div class="foot">
      <button class="item" :class="{ on: view === 'settings' }" data-test="open-app-settings" @click="emit('go', 'settings')"><Icon name="settings" />Settings</button>
      <div class="status">
        <span class="muted tiny" data-test="slots" :title="'global run slots'">{{ slots ? `Run slots: ${slots.running} / ${slots.limit} in use · ${slots.queued} queued` : 'Starting…' }}</span>
      </div>
    </div>
  </aside>
</template>

<style scoped>
.rail { width: 56px; box-sizing: border-box; align-items: center; padding: 8px 6px; gap: 2px; }
.rail-places { display: flex; flex-direction: column; gap: 2px; margin-top: 6px; }
.rail-btn { position: relative; width: 40px; height: 38px; display: grid; place-items: center; border: 0; background: transparent; border-radius: 9px; color: var(--ink); padding: 0; }
.rail-btn:hover { background: var(--hover); }
.rail-btn.on { background: var(--selected); }
.logo-btn { height: 44px; margin-bottom: 4px; }
.logo-btn img { width: 28px; height: 28px; object-fit: contain; }
.rail-badge { position: absolute; top: 2px; right: 1px; font-size: 10px; background: var(--brand); color: #fff; border-radius: 8px; padding: 0 5px; line-height: 15px; }
.side { background: var(--side); border-right: 1px solid var(--line-soft); display: flex; flex-direction: column; min-height: 0; padding: 8px 8px 6px; }
.brand { display: flex; align-items: center; gap: 8px; padding: 6px 6px 10px; }
.logo { height: 26px; width: auto; flex: none; margin-left: 2px; }
.grow { flex: 1; }
.item { display: flex; align-items: center; gap: 10px; width: 100%; text-align: left; border: 0; background: transparent; padding: 7px 10px; border-radius: 9px; color: var(--ink); font-size: 14px; }
.item:hover:not(:disabled) { background: var(--hover); }
.item.on { background: var(--selected); }
.item.small { font-size: 13.5px; padding: 6px 10px; }
.item.new { font-weight: 500; }
.places { margin: 4px 0 6px; }
.soon { margin-left: auto; font-size: 10px; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); background: var(--chip); padding: 1px 6px; border-radius: 6px; }
.badge { margin-left: auto; font-size: 11px; background: var(--brand); color: #fff; border-radius: 9px; padding: 0 7px; }
.lists { flex: 1; overflow: auto; min-height: 0; }
h3 { font-size: 12px; font-weight: 500; color: var(--muted); margin: 14px 10px 4px; }
.label { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tiny { font-size: 11px; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: #c3c8d2; flex: none; }
.dot.busy { background: var(--state-running); box-shadow: 0 0 0 3px rgba(29, 107, 47, .15); animation: dot-pulse 1.6s ease-in-out infinite; }
.dot[data-state=queued] { background: var(--state-waiting); }
.dot[data-state=paused] { background: var(--state-blocked); }
@keyframes dot-pulse { 50% { box-shadow: 0 0 0 5px rgba(29, 107, 47, .05); } }
@media (prefers-reduced-motion: reduce) { .dot.busy { animation: none; } }
.empty { font-size: 12.5px; padding: 2px 10px; margin: 0; }
.side-note { padding: 0 10px; font-size: 12.5px; }
.chat { display: flex; align-items: center; border-radius: 9px; padding-right: 4px; gap: 4px; }
.chat:hover { background: var(--hover); }
.chat.on { background: var(--selected); }
.chat .open { flex: 1; min-width: 0; display: flex; align-items: center; gap: 8px; border: 0; background: transparent; text-align: left; padding: 6px 10px; font-size: 13.5px; color: var(--ink); }
.chat .actions { display: none; }
.chat:hover .actions, .chat.on .actions { display: flex; }
.chat .label { padding: 0; }
.chat .label.strong { font-weight: 600; }
.unread { flex: none; min-width: 18px; height: 18px; border-radius: 9px; background: var(--brand); color: #fff; font-size: 11px; font-weight: 600;
  display: grid; place-items: center; padding: 0 5px; }
.watching { color: var(--muted); }
.rename { flex: 1; margin: 2px 4px; font-size: 13px; }
.mini { font-size: 11px; padding: 1px 6px; }
.ghost { border: 0; background: transparent; }
.icon-btn { padding: 4px; border-radius: 6px; color: var(--muted); display: grid; place-items: center; }
.icon-btn:hover { background: var(--hover-strong); color: var(--ink); }
.foot { position: relative; border-top: 1px solid var(--line-soft); padding-top: 6px; }
.status { display: flex; align-items: center; justify-content: space-between; gap: 6px; padding: 2px 10px 0; }
</style>
