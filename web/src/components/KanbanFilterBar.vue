<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { useConsoleStore } from '../stores/console';
import { useKanbanStore } from '../stores/kanban';
import { KanbanStatusLabel, SwimlanesTeamCaption } from '../lib/kanban';
import { KanbanStatuses, NoOutcome, type KanbanFilters } from '../api/kanban';
import { outcomeLabel } from '../api/outcomes';
import { useEndedOutcomeOption } from '../lib/currentOutcome';
import { activeFilterCount } from '../lib/kanban';

/**
 * The filter bar: four filters the SERVER resolves, and one box this browser resolves.
 *
 * EACH OF THE FOUR TAKES SEVERAL VALUES. A dropdown is a multiple select; what it holds is sent as
 * one comma-separated value (`KanbanFilter.Values` on the server), so the store, the query string
 * and the board request keep one string per filter. Within a dropdown any value keeps a card;
 * across dropdowns every one must hold. Nothing picked is no filter.
 *
 * Each of the three writes through `setFilters`, which drops anything that became empty and
 * refetches - so "cleared" has ONE spelling and the query string, the store and this bar always
 * agree. Nothing narrows the fetched cards here: the board is a server-side projection and
 * narrowing it locally would show a different set from the one the `kanban` tool returns
 * for the same filter.
 *
 * THE SEARCH BOX IS THE DELIBERATE EXCEPTION AND IT IS NOT A FILTER. It never reaches the server,
 * so it is view state on the store (`kanban.text`) rather than a fourth `KanbanFilters` field, and
 * what it narrows is what is RENDERED - `board.cards` keeps every card the server answered with.
 * Client-side on purpose: instant, no route change, and it composes with
 * the three beside it. It stops being right once a board needs paging, which is a separate change.
 *
 * THERE IS NO `workflow`, `from` OR `to` CONTROL: `/api/kanban/board` does not bind them,
 * so a control here would write a filter the server ignores - a date picker that visibly does
 * nothing.
 */
defineEmits<{ 'manage-outcomes': [] }>();

const kanban = useKanbanStore();
const board = useConsoleStore();

onMounted(() => {
  void kanban.loadOutcomes();
});

/**
 * The teams offered.
 *
 * The console's team list FIRST, because it is the whole tenant and it is named the way a person
 * named it; the board's own teams are unioned in so a card belonging to a team this browser cannot
 * currently see is still filterable rather than silently unreachable.
 */
const teamOptions = computed(() => {
  const seen = new Map<string, string>();

  for (const team of board.choosableTeams) seen.set(team.id, team.name);
  for (const id of kanban.teamOptions) if (!seen.has(id)) seen.set(id, id);

  return [...seen].map(([value, label]) => ({ label, value }));
});

const memberOptions = computed(() =>
  kanban.memberOptions.map((member) => ({ label: member, value: member })),
);

const statusOptions = computed(() =>
  KanbanStatuses.map((status) => ({ label: KanbanStatusLabel[status], value: status })),
);

/**
 * The Outcome filter: All (nothing sent), No outcome (`none`), then each active and proposed
 * outcome by id. A retired or merged outcome filtered on is shown by name with its status, and is
 * not offered. The names are the q-select's labels, rendered as text.
 */
/** A filter's values: the comma-separated string the store holds, as the select's list. */
function listOf(value: string | undefined): string[] {
  return (value ?? '').split(',').map((part) => part.trim()).filter(Boolean);
}

/** The one picked outcome that is no longer on offer (retired or merged), shown by name. */
const endedOutcomeId = computed(() =>
  listOf(kanban.filters.outcome).find((id) => id !== NoOutcome && !kanban.outcomes.some((outcome) => outcome.id === id)) ?? null);

const endedOutcome = useEndedOutcomeOption(
  () => endedOutcomeId.value,
  () => kanban.outcomes,
  { known: () => kanban.board?.cards.find((card) => card.outcome?.id === endedOutcomeId.value)?.outcome },
);

const outcomeOptions = computed(() => [
  { label: 'No outcome', value: NoOutcome },
  ...kanban.outcomes.map((outcome) => ({ label: outcomeLabel(outcome), value: outcome.id })),
  ...endedOutcome.value,
]);

/** One writer for every control. `null` from a cleared q-select becomes an absent key. */
function set(patch: KanbanFilters) {
  kanban.setFilters(patch);
}

/** A multiple select's answer as the store's one string: values comma-separated, cleared is empty. */
function joined(values: unknown): string {
  return Array.isArray(values) ? values.map(String).join(',') : '';
}

/**
 * Counts the search box as well as the filters IN EFFECT - see `activeFilterCount`. In Swimlanes
 * the kept team is not filtering anything, so it is not counted; Clear still clears it.
 */
const filterCount = computed(() => activeFilterCount(kanban.effectiveFilters, kanban.text));

/**
 * SWIMLANES SHOW EVERY TEAM, so the Team filter does not apply there. It stays on the bar, greyed
 * out and holding the team kept for Board, and says why in its tooltip rather than vanishing. NOT
 * A CAPTION UNDER IT: a hint lines the Team box up a line taller than every other filter.
 */
const teamLocked = computed(() => kanban.view === 'swimlanes');
</script>

<template>
  <div class="k-filters">
    <!-- The tooltip is on a wrapper: a disabled field takes no pointer events of its own. -->
    <div class="k-filter k-filter--wide" data-filter="team" :title="teamLocked ? SwimlanesTeamCaption : undefined">
      <q-select
        :model-value="listOf(kanban.filters.team)"
        :options="teamOptions"
        :disable="teamLocked"
        multiple
        use-chips
        dense
        outlined
        clearable
        emit-value
        map-options
        label="Team"
        @update:model-value="(value) => set({ team: joined(value) })"
      />
    </div>

    <q-select
      :model-value="listOf(kanban.filters.member)"
      :options="memberOptions"
      multiple
      use-chips
      dense
      outlined
      clearable
      emit-value
      map-options
      label="Member"
      class="k-filter k-filter--wide"
      data-filter="member"
      @update:model-value="(value) => set({ member: joined(value) })"
    />

    <q-select
      :model-value="listOf(kanban.filters.status)"
      :options="statusOptions"
      multiple
      use-chips
      dense
      outlined
      clearable
      emit-value
      map-options
      label="Status"
      class="k-filter"
      data-filter="status"
      @update:model-value="(value) => set({ status: joined(value) })"
    />

    <q-select
      :model-value="listOf(kanban.filters.outcome)"
      :options="outcomeOptions"
      multiple
      use-chips
      dense
      outlined
      clearable
      emit-value
      map-options
      label="Outcome"
      class="k-filter k-filter--wide"
      data-filter="outcome"
      @update:model-value="(value) => set({ outcome: joined(value) })"
    />

    <!-- NOT DEBOUNCED, because it fetches nothing. The four above are debounced where they are
         typed rather than chosen, since a board fetch per character is wasted projections; this one
         narrows the cards already on screen, so per-keystroke IS the feature.

         THE HINT IS NOT DECORATION. A card that does not match for a reason nobody can see reads as
         a missing card, so the box says what it looks at; the sentence lives beside the matcher in
         `lib/kanban` so the two cannot drift apart. -->
    <q-input
      :model-value="kanban.text"
      dense
      outlined
      clearable
      label="Search"
      class="k-filter k-filter--search"
      @update:model-value="(value) => kanban.setText(String(value ?? ''))"
    >
      <template #prepend><q-icon name="search" /></template>
    </q-input>

    <q-btn
      v-if="filterCount"
      flat
      dense
      no-caps
      icon="filter_alt_off"
      :label="`Clear (${filterCount})`"
      @click="kanban.clearFilters()"
    />

    <!-- Manage Outcomes; the board hosts the dialog, which a card's outcome tag also opens. -->
    <q-btn flat dense no-caps icon="flag" label="Manage outcomes" data-manage-outcomes @click="$emit('manage-outcomes')" />
  </div>
</template>

<style scoped>
.k-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.k-filter {
  min-width: 8.5rem;
}

.k-filter--wide {
  min-width: 12rem;
}

.k-filter--search {
  min-width: 16rem;
  flex: 1 1 16rem;
}
</style>
