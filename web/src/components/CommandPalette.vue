<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { backlogItems } from '../api/client';
import { asTeamId, type BacklogItemView } from '../api/types';
import { useConsoleStore } from '../stores/console';
import { useKanbanStore } from '../stores/kanban';
import { useWorkflowNavStore } from '../stores/workflowNav';
import { follow, memberHref } from '../lib/consoleLinks';
import { Ribbon } from '../lib/ribbon';
import { teamLogFacts, teamStatus, teamTabChip } from '../lib/teamKpis';
import {
  PaletteShortcut,
  matchPalette,
  opensPalette,
  paletteEntries,
  type PaletteEntry,
  type PaletteTarget,
} from '../lib/palette';
import FieldHelp from './FieldHelp.vue';

/**
 * THE COMMAND PALETTE: Ctrl+K (Cmd+K), or `/` outside a text field, opens one text box over a list;
 * arrows move, Enter opens, Escape closes. It lists what the app already holds - the overview's teams
 * and members, the backlog list (read when it opens, kept while it is open), the outcomes and the
 * ribbon's commands, gated as the ribbon gates them - and matches through `lib/palette`.
 *
 * Every row OPENS something: a board, an item, a member's card, a view, a ribbon command (`action`,
 * which the layout runs as the ribbon's own click) or an outcome (`outcome`, which the layout opens).
 * The palette never types into the Concierge and never sends an instruction; a key pressed in the
 * Concierge's terminal is in a text field, so the shortcut is not taken there.
 */
const open = defineModel<boolean>('open', { required: true });

const emit = defineEmits<{ action: [action: string]; outcome: [id: string] }>();

const board = useConsoleStore();
const kanban = useKanbanStore();
const workflowNav = useWorkflowNavStore();

const query = ref('');
const active = ref(0);
const list = ref<HTMLElement | null>(null);

/** The backlog list as read when the palette opened; null until it answers. */
const items = ref<BacklogItemView[] | null>(null);
const itemsFailed = ref(false);
/** When it opened: the team chips are read at that instant, as the tab's are at its clock's. */
const openedAt = ref(Date.now());

let reads = 0;

watch(
  open,
  (showing) => {
    if (!showing) {
      items.value = null;
      itemsFailed.value = false;
      return;
    }

    query.value = '';
    active.value = 0;
    openedAt.value = Date.now();

    const read = ++reads;
    itemsFailed.value = false;
    backlogItems()
      .then((answer) => {
        if (read === reads && open.value) items.value = answer;
      })
      .catch(() => {
        if (read === reads) itemsFailed.value = true;
      });
    void kanban.loadOutcomes();
  },
  { immediate: true },
);

const entries = computed<PaletteEntry[]>(() =>
  paletteEntries({
    teams: board.choosableTeams.map((team) => ({
      id: team.id,
      name: team.name,
      state: teamTabChip(
        teamStatus(team.containers, teamLogFacts(board.teamActivity(team.id)), openedAt.value, team.paused === true),
        board.workflowsFor(team.id),
        team.containers,
        openedAt.value,
      ),
      members: team.containers.map((container) => ({ id: container.id, name: container.name })),
    })),
    items: items.value ?? [],
    outcomes: kanban.outcomes,
    ribbon: Ribbon,
    activeWorkTeamId: board.activeWorkTeamId || undefined,
    activeTeamName: board.activeWorkTeam?.name,
  }),
);

const matches = computed(() => matchPalette(entries.value, query.value));

watch(query, () => {
  active.value = 0;
});

const help =
  'Type a team, a member, an item’s title or citation, a view, a ribbon command or an outcome. ' +
  'Arrows move, Enter opens, Escape closes. It only opens things; it never sends an instruction.';

/** What a row does, said after it for a screen reader and on hover. */
function opensWords(target: PaletteTarget): string {
  switch (target.kind) {
    case 'team':
      return 'Opens the board';
    case 'item':
      return 'Opens the item';
    case 'member':
      return 'Opens the member’s card';
    case 'view':
      return 'Shows the view';
    case 'action':
      return 'Runs the command';
    case 'outcome':
      return 'Opens the outcome';
  }
}

function run(entry: PaletteEntry | undefined) {
  if (!entry) return;
  open.value = false;

  const target = entry.target;
  switch (target.kind) {
    case 'team':
      workflowNav.openBoard(asTeamId(target.team));
      return;
    case 'item':
      workflowNav.openItem(target.id);
      return;
    case 'member':
      follow(memberHref(target.team, target.member));
      return;
    case 'view':
      if (target.view === 'teams') board.showTeamsView();
      else if (target.view === 'kanban') kanban.showKanban(board.activeWorkTeamId);
      else board.showMonitor();
      return;
    case 'action':
      emit('action', target.action);
      return;
    case 'outcome':
      emit('outcome', target.id);
      return;
  }
}

function move(by: number) {
  const count = matches.value.length;
  if (count === 0) return;
  active.value = (active.value + by + count) % count;
  void nextTick(() => {
    list.value?.querySelector('[aria-selected="true"]')?.scrollIntoView?.({ block: 'nearest' });
  });
}

function onKey(event: KeyboardEvent) {
  // The shortcut again, from the palette's own box: it closes.
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault();
    open.value = false;
  } else if (event.key === 'ArrowDown') {
    event.preventDefault();
    move(1);
  } else if (event.key === 'ArrowUp') {
    event.preventDefault();
    move(-1);
  } else if (event.key === 'Enter') {
    event.preventDefault();
    run(matches.value[active.value]);
  } else if (event.key === 'Escape') {
    event.preventDefault();
    open.value = false;
  }
}

/** The shortcut, anywhere on the page but a text field. */
function onDocumentKey(event: KeyboardEvent) {
  if (open.value || event.defaultPrevented || !opensPalette(event)) return;
  event.preventDefault();
  open.value = true;
}

onMounted(() => document.addEventListener('keydown', onDocumentKey));
onUnmounted(() => document.removeEventListener('keydown', onDocumentKey));
</script>

<template>
  <q-dialog v-model="open" position="top" data-test="command-palette">
    <q-card class="os-dialog-md command-palette">
      <q-card-section class="q-pb-sm">
        <q-input
          v-model="query"
          autofocus
          dense
          outlined
          placeholder="Find a team, member, item, view, command or outcome"
          aria-label="Find"
          role="combobox"
          aria-controls="command-palette-list"
          aria-autocomplete="list"
          :aria-expanded="matches.length > 0 ? 'true' : 'false'"
          :aria-activedescendant="matches[active] ? `command-palette-${active}` : undefined"
          data-test="command-palette-input"
          @keydown="onKey"
        >
          <template #prepend><q-icon name="search" /></template>
          <template #append><FieldHelp :text="help" /></template>
        </q-input>
      </q-card-section>

      <div
        id="command-palette-list"
        ref="list"
        class="command-palette-list"
        role="listbox"
        aria-label="Matches"
        data-test="command-palette-list"
      >
        <div
          v-for="(entry, index) in matches"
          :id="`command-palette-${index}`"
          :key="entry.key"
          role="option"
          class="command-palette-row"
          :class="{ 'command-palette-row--active': index === active }"
          :aria-selected="index === active ? 'true' : 'false'"
          :title="opensWords(entry.target)"
          :data-key="entry.key"
          @mousemove="active = index"
          @click="run(entry)"
        >
          <q-icon :name="entry.icon" size="18px" class="command-palette-icon" />
          <span v-if="entry.citation" class="k-card-item command-palette-citation">{{ entry.citation }}</span>
          <span class="command-palette-label">{{ entry.label }}</span>
          <span class="command-palette-detail">{{ entry.detail }}</span>
          <span
            v-if="entry.state && entry.target.kind === 'team'"
            class="console-tab-status"
            :class="`console-tab-status--${entry.state.token}`"
          >{{ entry.state.text }}</span>
          <span v-else-if="entry.state" class="command-palette-state">{{ entry.state.text }}</span>
        </div>
        <div v-if="matches.length === 0" class="command-palette-empty" data-test="command-palette-empty">
          Nothing matches.
        </div>
      </div>

      <div class="command-palette-foot text-caption" data-test="command-palette-foot">
        <span v-if="itemsFailed">The backlog could not be read; its items are not listed.</span>
        <span v-else-if="items === null">Reading the backlog…</span>
        <span v-else>{{ PaletteShortcut }} opens this anywhere but a text field.</span>
      </div>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.command-palette-list {
  max-height: min(60vh, 28rem);
  overflow-y: auto;
  padding: 0 8px;
}

.command-palette-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 36px;
  padding: 0 8px;
  border-radius: 4px;
  cursor: pointer;
}

.command-palette-row--active {
  background: color-mix(in srgb, var(--os-primary) 12%, transparent);
  outline: 2px solid var(--os-primary);
  outline-offset: -2px;
}

.command-palette-icon,
.command-palette-detail,
.command-palette-state {
  color: var(--os-ink-muted);
}

.command-palette-citation {
  flex: 0 0 auto;
  padding: 0 4px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  line-height: 14px;
}

.command-palette-label {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.command-palette-detail {
  flex: 1 0 auto;
  font-size: 0.75rem;
  white-space: nowrap;
}

.command-palette-state {
  font-size: 0.6875rem;
  white-space: nowrap;
}

.command-palette-empty {
  padding: 12px 8px;
  color: var(--os-ink-muted);
}

.command-palette-foot {
  padding: 8px 16px;
  color: var(--os-ink-muted);
}
</style>
