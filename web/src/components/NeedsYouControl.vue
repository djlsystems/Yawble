<script setup lang="ts">
import { computed, onMounted, onUnmounted, watch } from 'vue';
import { useRouter } from 'vue-router';
import { storeToRefs } from 'pinia';
import type { NeedsYouItem } from '../api/types';
import { asTeamId } from '../api/types';
import { StoredEveryMs, useNeedsYouStore } from '../stores/needsYou';
import { useConsoleStore } from '../stores/console';
import { useKanbanStore } from '../stores/kanban';
import { useWorkflowNavStore } from '../stores/workflowNav';
import { cardHref, follow, memberHref } from '../lib/consoleLinks';
import { localStamp, wireInstant } from '../lib/localTime';
import {
  groupNeedsYou,
  needsYouDecision,
  needsYouOpen,
  needsYouOpenWords,
  needsYouSubject,
  type NeedsYouOpen,
} from '../lib/needsYou';
import DiscussWithConcierge from './DiscussWithConcierge.vue';
import FieldHelp from './FieldHelp.vue';

/**
 * "NEEDS YOU" ON THE APP BAR: the count of what is waiting on a person, left of the activity
 * gauge, in the needs-you state colour above 0 and muted at 0. A click opens the list grouped by
 * kind, each row one way in.
 *
 * The count is the route's (`GET /api/needs-you`, both parts), never the Kanban lane's or the
 * Monitor's. The live part is re-read on the hub's pushes (IndexPage); the stored part here, every
 * 30 s and when the panel opens.
 */
/** `word: false` is the row short of room (`lib/headerFit.ts`): the count pill alone, as on a phone. */
const props = withDefaults(defineProps<{ word?: boolean }>(), { word: true });

const needsYou = useNeedsYouStore();
const { count, items, error } = storeToRefs(needsYou);
const board = useConsoleStore();
const router = useRouter();

let timer: ReturnType<typeof setInterval> | 0 = 0;

onMounted(() => {
  void needsYou.load();
  timer = setInterval(() => void needsYou.loadStored(), StoredEveryMs);
});

onUnmounted(() => {
  if (timer) clearInterval(timer);
});

// Back from offline: what was pushed meanwhile was missed.
watch(
  () => board.connected,
  (connected, was) => {
    if (connected && was === false) void needsYou.loadLive();
  },
);

const waiting = computed(() => (count.value ?? 0) > 0);
const groups = computed(() => groupNeedsYou(items.value));

const label = computed(() => {
  if (count.value === null) return error.value ? `Needs you: not read (${error.value})` : 'Needs you: not read yet';
  return `Needs you: ${count.value}`;
});

const HelpText =
  'What is waiting on a person, across every team you can see: decisions, blocked and failed members, ' +
  'inputs a solution waits for, work ready to merge, stranded workflows and unfinished removals. ' +
  'A row leaves the list when what it says is no longer true.';

function since(item: NeedsYouItem): string {
  const at = wireInstant(item.since);
  return Number.isNaN(at) ? '' : localStamp(at);
}

/** The Console, under any address that shows it over something else (a solution screen). */
function toConsole() {
  if (router && router.currentRoute.value.path !== '/console') void router.push('/console');
}

function open(target: NeedsYouOpen) {
  const nav = useWorkflowNavStore();
  switch (target.to) {
    case 'member':
      follow(memberHref(target.team, target.member));
      return;
    case 'workflows':
      toConsole();
      nav.openWorkflows(asTeamId(target.team));
      return;
    case 'card':
      useKanbanStore().panelAsk = target.card;
      follow(cardHref(target.card));
      return;
    case 'item':
      nav.openItem(target.item);
      return;
    case 'git':
      toConsole();
      nav.openGit(asTeamId(target.team));
      return;
    case 'solution':
      void router.push(`/solutions/${encodeURIComponent(target.team)}`);
      return;
    case 'removals':
      toConsole();
      board.showTeamsView();
      return;
  }
}
</script>

<template>
  <q-btn
    flat
    dense
    no-caps
    class="needs-you"
    :class="waiting ? 'needs-you--waiting' : 'needs-you--muted'"
    data-test="needs-you"
    :data-state="waiting ? 'needs-you' : 'muted'"
    :aria-label="label"
  >
    <span v-if="props.word" class="gt-xs" data-test="needs-you-word">Needs you</span>
    <span class="needs-you-count" data-test="needs-you-count">{{ count ?? '–' }}</span>
    <q-tooltip>{{ label }}</q-tooltip>

    <q-menu anchor="bottom right" self="top right" class="above-concierge" @before-show="needsYou.load()">
      <div class="needs-you-panel q-pa-sm" data-test="needs-you-panel">
        <div class="row items-center no-wrap q-mb-xs">
          <div class="text-subtitle2">Needs you</div>
          <FieldHelp class="q-ml-xs" :text="HelpText" />
        </div>

        <div v-if="error" class="text-caption os-text-muted q-mb-xs" data-test="needs-you-error">
          Not read: {{ error }}
        </div>

        <div v-if="count === 0" class="text-caption os-text-muted" data-test="needs-you-empty">
          Nothing is waiting on you.
        </div>

        <section v-for="group in groups" :key="group.kind" class="q-mb-sm" :data-needs-you-group="group.kind">
          <div class="needs-you-heading text-overline">{{ group.words }} ({{ group.items.length }})</div>

          <div
            v-for="(item, index) in group.items"
            :key="`${group.kind}:${item.team.id}:${item.member ?? ''}:${item.card ?? ''}:${item.item?.id ?? ''}:${index}`"
            class="needs-you-row row items-start no-wrap q-py-xs"
            data-test="needs-you-row"
            :data-kind="item.kind"
          >
            <div class="col needs-you-text">
              <div class="needs-you-who">
                <span class="text-weight-medium" data-test="needs-you-team">{{ item.team.name || item.team.id }}</span>
                <span v-if="needsYouSubject(item)" data-test="needs-you-subject"> · {{ needsYouSubject(item) }}</span>
              </div>
              <div class="needs-you-words" data-test="needs-you-words">{{ item.words }}</div>
              <div class="text-caption os-text-muted" data-test="needs-you-since">{{ since(item) }}</div>
            </div>

            <div class="col-auto q-pl-sm" v-close-popup>
              <DiscussWithConcierge v-if="needsYouDecision(item)" :decision="needsYouDecision(item)!" />
              <template v-else-if="needsYouOpen(item)">
                <q-btn
                  flat
                  dense
                  no-caps
                  size="sm"
                  color="primary"
                  label="Open"
                  data-test="needs-you-open"
                  @click="open(needsYouOpen(item)!)"
                >
                  <q-tooltip>{{ needsYouOpenWords(needsYouOpen(item)!) }}</q-tooltip>
                </q-btn>
              </template>
            </div>
          </div>
        </section>
      </div>
    </q-menu>
  </q-btn>
</template>

<style scoped>
.needs-you {
  gap: 6px;
}

.needs-you--waiting {
  color: var(--os-state-needs-you);
}

.needs-you--muted {
  color: var(--os-ink-muted);
}

/* THE COUNT IS THE ICON: a 20px badge, the size of the other tools' glyphs. Muted it is an outline;
   while anything waits it is solid yellow (`--os-attention`) with dark ink, to catch the eye. */
.needs-you-count {
  box-sizing: border-box;
  min-width: 20px;
  height: 20px;
  padding: 0 5px;
  border: 1.5px solid currentColor;
  border-radius: 10px;
  font-size: 0.75rem;
  font-weight: 700;
  line-height: 17px;
  text-align: center;
}

span + .needs-you-count {
  margin-left: 6px;
}

.needs-you--waiting .needs-you-count {
  border-color: var(--os-attention);
  background: var(--os-attention);
  color: var(--os-attention-ink);
}

.needs-you-panel {
  width: 26rem;
  max-width: 92vw;
  max-height: 70vh;
  overflow-y: auto;
}

.needs-you-heading {
  color: var(--os-ink-muted);
}

.needs-you-row + .needs-you-row {
  border-top: 1px solid var(--os-rule);
}

.needs-you-text {
  min-width: 0;
}

.needs-you-words {
  overflow-wrap: anywhere;
}
</style>
