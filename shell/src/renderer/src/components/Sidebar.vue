<script setup lang="ts">
import { vChange } from '../remoteAccess';
import { teamState, teamStateLabel } from '../teamState';
import wordmark from '../assets/yawble-logo-light.png';
import wordmarkDark from '../assets/yawble-logo-dark.png';
import mark from '../assets/yawble-mark.png';
import { nextTick, ref } from 'vue';
import type { ConversationSummary, RunSlots, TeamSummary } from '../../../shared/api';
import Icon from './Icon.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import type { MenuItem as MenuBarItem } from './MenuBar.vue';

// The app's left pane, like the chat apps people know: New chat, the places (Concierge, Teams, and what comes next),
// the teams, and the person's chats with the Concierge; the run slots at the bottom. Settings is the gear at the top right (App.vue).
// B115: embedded in the cloud page (remote.html?embed=1) this pane is not drawn at all; the same content goes to the cloud as
// data (embed.ts buildNav, yawble.nav) and the cloud's clicks come back to App.vue's handlers for this pane's emits. A
// place, a badge or a chat action added here goes into buildNav too (tests/unit/embed.test.ts compares the two).
export type View = 'concierge' | 'documents' | 'backlog' | 'workflows' | 'teams' | 'board' | 'monitor' | 'logs' | 'settings';
export type MenuItem = 'agents' | 'skills' | 'globalPolicy' | 'integrations' | 'system' | 'link';

defineProps<{
  view: View;
  conversations: ConversationSummary[];
  teams: TeamSummary[];
  conversationId: string | null;
  teamId: number | null;
  slots: RunSlots | null;
  remote: boolean;
  /** Folded: a narrow rail of icons (the places, New chat), each named on hover. */
  rail?: boolean;
  /** The teams / the chats have not had their first answer yet. */
  loading?: { teams: boolean; chats: boolean };
  /** The chat being deleted (its row shows Deleting… until the core answers). */
  removing?: string | null;
  /** A phone (B037): the sidebar is a drawer over the page, shown while `open`; its rows are tap-sized and a chat's
   *  Rename and Delete always show (no hover on a phone). */
  drawer?: boolean;
  open?: boolean;
  /** A remote view on a phone has no menu bar: these of its items (theme, terminal, Help) sit at the drawer's bottom. */
  menu?: MenuBarItem[];
  /** The team whose page is open (card 316): on a phone its Documents and Workflows sit near the drawer's top, not in
   *  the team page's header. Null with no team page open; ignored on the desktop. */
  openTeamName?: string | null;
  /** B104: how many workflows are open across the teams (the Workflows badge); null: not known. */
  openWorkflows?: number | null;
}>();
const emit = defineEmits<{
  newChat: []; openConversation: [id: string]; openTeam: [id: number]; go: [view: View]; logs: [];
  teamDocuments: []; teamWorkflows: [];
  rename: [id: string, title: string]; remove: [id: string]; newTeam: []; collapse: []; expand: []; command: [id: string];
}>();

const railPlaces: { view: View; label: string; icon: string }[] = [
  { view: 'concierge', label: 'Concierge', icon: 'spark' }, { view: 'documents', label: 'Documents', icon: 'docs' },
  { view: 'backlog', label: 'Backlog', icon: 'backlog' }, { view: 'workflows', label: 'Workflows', icon: 'workflow' }, { view: 'teams', label: 'Teams', icon: 'teams' },
  { view: 'board', label: 'Kanban board', icon: 'board' }, { view: 'monitor', label: 'Monitor', icon: 'monitor' },
];

const editing = ref<string | null>(null);
const draft = ref('');
const confirming = ref<string | null>(null);
/** The drawer's menu group (a remote view on a phone) is open. */
const menuOpen = ref(false);
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
        <span v-else-if="p.view === 'workflows' && openWorkflows" class="rail-badge" data-test="rail-workflows-count" :title="`${openWorkflows} open`">{{ openWorkflows }}</span>
      </button>
      <button class="rail-btn" :class="{ on: view === 'logs' }" title="Logs" data-test="rail-logs" @click="emit('logs')"><Icon name="logs" /></button>
    </nav>
  </aside>
  <aside v-else class="side" :class="{ drawer, open: drawer && open }" data-test="sidebar" :data-drawer="drawer ? (open ? 'open' : 'closed') : undefined"
    :inert="drawer && !open" :aria-hidden="drawer && !open ? 'true' : undefined" :aria-label="drawer ? 'Menu' : undefined">
    <div class="brand">
      <img class="logo on-light" :src="wordmark" alt="Yawble" data-test="sidebar-logo" />
      <img class="logo on-dark" :src="wordmarkDark" alt="Yawble" data-test="sidebar-logo-dark" />
      <span class="grow" />
      <button class="ghost icon-btn" :title="drawer ? 'Close the menu' : 'Hide the sidebar'" :aria-label="drawer ? 'Close the menu' : undefined" data-test="sidebar-collapse"
        @click="emit('collapse')"><Icon :name="drawer ? 'close' : 'sidebar'" /></button>
    </div>

    <button class="item new" data-test="new-chat" @click="emit('newChat')"><Icon name="newChat" />New chat</button>

    <!-- A phone with a team page open: that team's Documents and Workflows (its header has no room for them). -->
    <div v-if="drawer && openTeamName" class="team-group" data-test="drawer-team">
      <h3 class="team-name" :title="`Team ${openTeamName}`">Team {{ openTeamName }}</h3>
      <button class="item" data-test="drawer-team-documents" @click="emit('teamDocuments')"><Icon name="docs" />Documents</button>
      <button class="item" data-test="drawer-team-workflows" @click="emit('teamWorkflows')"><Icon name="workflow" />Workflows</button>
    </div>

    <nav class="places">
      <button class="item" :class="{ on: view === 'concierge' }" data-test="nav-concierge" @click="emit('go', 'concierge')"><Icon name="spark" />Concierge</button>
      <button class="item" :class="{ on: view === 'teams' }" data-test="nav-teams" @click="emit('go', 'teams')"><Icon name="teams" />Teams<span v-if="teams.length" class="badge" data-test="nav-teams-count" :title="`${teams.length} team${teams.length === 1 ? '' : 's'}`">{{ teams.length }}</span></button>
      <button class="item" :class="{ on: view === 'backlog' }" data-test="nav-backlog" @click="emit('go', 'backlog')"><Icon name="backlog" />Backlog</button>
      <button class="item" :class="{ on: view === 'workflows' }" data-test="nav-workflows" @click="emit('go', 'workflows')"><Icon name="workflow" />Workflows<span v-if="openWorkflows" class="badge" data-test="nav-workflows-count" :title="`${openWorkflows} open workflow${openWorkflows === 1 ? '' : 's'}`">{{ openWorkflows }}</span></button>
      <button class="item" :class="{ on: view === 'board' }" data-test="nav-board" @click="emit('go', 'board')"><Icon name="board" />Kanban board</button>
      <button class="item" :class="{ on: view === 'documents' }" data-test="nav-documents" @click="emit('go', 'documents')"><Icon name="docs" />Documents</button>
      <button class="item" :class="{ on: view === 'monitor' }" data-test="nav-monitor" @click="emit('go', 'monitor')"><Icon name="monitor" />Monitor</button>
      <button class="item" :class="{ on: view === 'logs' }" data-test="nav-logs" @click="emit('logs')"><Icon name="logs" />Logs</button>
    </nav>

    <div class="lists">
      <h3>Teams</h3>
      <LoadingNote v-if="loading?.teams" class="side-note" text="Loading teams…" />
      <button v-for="t in teams" :key="t.id" class="item small" :class="{ on: view === 'teams' && teamId === t.id }" data-test="team-item" @click="emit('openTeam', t.id)">
        <span class="dot" :class="{ busy: t.running > 0 }" :data-state="teamState(t)" :title="teamStateLabel[teamState(t)]" />
        <span class="label">{{ t.name }}</span>
        <span v-if="t.running || t.queued" class="team-pills" data-test="team-counts">
          <span v-if="t.running" class="tpill running" data-test="team-running" :title="`${t.running} running`">{{ t.running }}</span>
          <span v-if="t.queued" class="tpill queued" data-test="team-queued" :title="`${t.queued} queued`">{{ t.queued }}</span>
        </span>
      </button>
      <button class="item small muted" v-change data-test="new-team" @click="emit('newTeam')"><Icon name="plus" :size="15" />New team</button>

      <h3>Chats</h3>
      <LoadingNote v-if="loading?.chats" class="side-note" text="Loading chats…" />
      <p v-else-if="!conversations.length" class="empty muted">Your chats with the Concierge appear here.</p>
      <div v-for="c in conversations" :key="c.id" class="chat" :class="{ on: view === 'concierge' && conversationId === c.id }" data-test="chat-item">
        <input v-if="editing === c.id" ref="editBox" v-model="draft" class="rename" data-test="chat-rename-box"
          @keydown.enter="finishRename(c.id)" @keydown.esc="editing = null" @blur="finishRename(c.id)" />
        <template v-else-if="confirming === c.id">
          <span class="label">Delete this chat?</span>
          <button class="mini danger" v-change data-test="chat-delete-yes" :disabled="!!removing" :aria-busy="removing === c.id" @click="emit('remove', c.id)"><BusyLabel :busy="removing === c.id" text="Deleting…">Delete</BusyLabel></button>
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
            <button class="ghost icon-btn" title="Rename" v-change data-test="chat-rename" @click="startRename(c)"><Icon name="edit" :size="14" /></button>
            <button class="ghost icon-btn" title="Delete" v-change data-test="chat-delete" @click="confirming = c.id"><Icon name="trash" :size="14" /></button>
          </span>
        </template>
      </div>
    </div>

    <div class="foot">
      <!-- Folded by default (B037 F2): seven items open would leave the Teams and Chats lists no room on a phone. -->
      <div v-if="menu?.length" class="drawer-menu" data-test="drawer-menu">
        <button class="item small more" data-test="drawer-more" :aria-expanded="menuOpen" aria-controls="drawer-menu-items" @click="menuOpen = !menuOpen">
          <span class="check" aria-hidden="true">{{ menuOpen ? '▾' : '▸' }}</span>Theme, terminal and help
        </button>
        <div v-show="menuOpen" id="drawer-menu-items" data-test="drawer-menu-items">
        <button v-for="m in menu" :key="m.id" class="item small" :role="m.radio ? 'menuitemradio' : undefined" :aria-checked="m.radio ? !!m.checked : undefined"
          :data-test="`drawer-${m.id}`" @click="emit('command', m.id)">
          <span class="check">{{ m.checked ? '✓' : '' }}</span>{{ m.label }}
        </button>
        </div>
      </div>
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
.rail-badge { position: absolute; top: 2px; right: 1px; font-size: 10px; background: var(--brand); color: var(--on-accent); border-radius: 8px; padding: 0 5px; line-height: 15px; }
/* overflow-y (card 334): a window shorter than the sidebar's fixed rows (a phone in landscape at a desktop width) scrolls
   the sidebar, its run slots reachable; a taller one shows no scrollbar (the Teams and Chats lists scroll). */
.side { background: var(--side); border-right: 1px solid var(--line-soft); display: flex; flex-direction: column; min-height: 0; overflow-y: auto; padding: 8px 8px 6px; }
.brand { display: flex; align-items: center; gap: 8px; padding: 6px 6px 10px; }
.logo { height: 26px; width: auto; flex: none; margin-left: 2px; }
/* The wordmark's dark letters vanish on the dark sidebar: the dark theme shows the white-lettered one. */
.logo.on-dark { display: none; }
:root[data-theme="dark"] .logo.on-light { display: none; }
:root[data-theme="dark"] .logo.on-dark { display: block; }
.grow { flex: 1; }
.item { display: flex; align-items: center; gap: 10px; width: 100%; text-align: left; border: 0; background: transparent; padding: 7px 10px; border-radius: 9px; color: var(--ink); font-size: 14px; }
.item:hover:not(:disabled) { background: var(--hover); }
.item.on { background: var(--selected); }
.item.small { font-size: 13.5px; padding: 6px 10px; }
.item.new { font-weight: 500; }
.places { margin: 4px 0 6px; }
.soon { margin-left: auto; font-size: 10px; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); background: var(--chip); padding: 1px 6px; border-radius: 6px; }
.badge { margin-left: auto; font-size: 11px; background: var(--brand); color: var(--on-accent); border-radius: 9px; padding: 0 7px; }
.lists { flex: 1; overflow: auto; min-height: min(96px, 100%); }
h3 { font-size: 12px; font-weight: 500; color: var(--muted); margin: 14px 10px 4px; }
.label { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tiny { font-size: 11px; }
/* A team's running and queued counts as pills in the state colours (running solid, queued dashed), each named on hover. */
.team-pills { display: inline-flex; gap: 3px; flex: none; }
.tpill { font-size: 11px; line-height: 16px; min-width: 18px; text-align: center; border-radius: 9px; padding: 0 6px; }
.tpill.running { background: color-mix(in srgb, var(--state-running) 18%, transparent); color: var(--state-running); }
.tpill.queued { border: 1px dashed var(--state-waiting); color: var(--state-waiting); line-height: 14px; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: var(--dot-off); flex: none; }
.dot.busy { background: var(--state-running); box-shadow: 0 0 0 3px color-mix(in srgb, var(--state-running) 15%, transparent); animation: dot-pulse 1.6s ease-in-out infinite; }
.dot[data-state=queued] { background: var(--state-waiting); }
.dot[data-state=paused] { background: var(--state-blocked); }
@keyframes dot-pulse { 50% { box-shadow: 0 0 0 5px color-mix(in srgb, var(--state-running) 5%, transparent); } }
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
.unread { flex: none; min-width: 18px; height: 18px; border-radius: 9px; background: var(--brand); color: var(--on-accent); font-size: 11px; font-weight: 600;
  display: grid; place-items: center; padding: 0 5px; }
.watching { color: var(--muted); }
.rename { flex: 1; margin: 2px 4px; font-size: 13px; }
.mini { font-size: 11px; padding: 1px 6px; }
.ghost { border: 0; background: transparent; }
.icon-btn { padding: 4px; border-radius: 6px; color: var(--muted); display: grid; place-items: center; }
.icon-btn:hover { background: var(--hover-strong); color: var(--ink); }
.foot { position: relative; border-top: 1px solid var(--line-soft); padding-top: 6px; }
.status { display: flex; align-items: center; justify-content: space-between; gap: 6px; padding: 2px 10px 0; }
/* A phone (App.vue, phone.ts): the drawer slides in from the left over the page; closed, it is off screen and inert. */
.side.drawer { position: fixed; top: 0; bottom: 0; left: 0; z-index: 30; width: min(320px, 86vw); border-right: 1px solid var(--line);
  padding: calc(8px + env(safe-area-inset-top, 0px)) 8px calc(6px + env(safe-area-inset-bottom, 0px)) calc(8px + env(safe-area-inset-left, 0px));
  transform: translateX(-105%); visibility: hidden; transition: transform .2s ease, visibility 0s linear .2s; }
.side.drawer.open { transform: none; visibility: visible; transition: transform .2s ease; box-shadow: 0 10px 40px var(--shadow); }
@media (prefers-reduced-motion: reduce) { .side.drawer, .side.drawer.open { transition: none; } }
.drawer .item, .drawer .chat .open { min-height: 44px; }
.drawer .brand .icon-btn { width: 44px; height: 44px; }
.drawer .chat .actions { display: flex; }
.drawer .chat .actions .icon-btn { width: 44px; height: 44px; }
/* The drawer scrolls as ONE list (a phone's screen is short): the Teams and Chats lists are not a box of their own, so
   every chat is reached by scrolling the drawer, and the footer (the folded menu, the run slots) follows them
   instead of being pinned under a squeezed list. */
.side.drawer { overflow-y: auto; overscroll-behavior: contain; }
.drawer .brand, .drawer .item.new, .drawer .places, .drawer .foot, .drawer .lists { flex: none; }
.drawer .lists { overflow: visible; }
.drawer .team-group { flex: none; }
.team-group { border-top: 1px solid var(--line-soft); border-bottom: 1px solid var(--line-soft); margin: 4px 0 2px; padding-bottom: 4px; }
.team-group .team-name { margin-top: 8px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.drawer-menu { border-bottom: 1px solid var(--line-soft); margin-bottom: 4px; padding-bottom: 4px; }
.drawer-menu .check { width: 14px; flex: none; color: var(--accent); font-size: 12px; }
</style>
