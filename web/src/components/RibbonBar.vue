<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type ComponentPublicInstance } from 'vue';
import { useRibbon } from '../lib/useRibbon';
import { commandRoom, fitCount, ribbonEntries, type RibbonEntry, type RibbonItem } from '../lib/ribbon';
import RibbonTeamList from './RibbonTeamList.vue';

/**
 * The command bar: ONE 44px row. An Outlook-style ribbon - a block per tab with a heading over it
 * and a caption under it - would spend about 90px more of the header than a row does, on every
 * screen, for labels a person reads once.
 *
 * The row is `ribbonEntries(Ribbon)` from `lib/ribbon.ts`: every item in tab order, a separator
 * where a tab starts. This component knows how to RENDER items and nothing about which ones exist.
 * Commands leave by the `action` event, named by the string in the constant, so adding a button is
 * an edit there plus a handler and never a change here.
 *
 * WHAT DOES NOT FIT GOES INTO THE OVERFLOW MENU, from the right, and the menu lists it under the
 * same tab names the drawer uses. Below `sm` (600px) MainLayout renders this row without its
 * commands and opens RibbonMobileMenu from a hamburger instead - same `useRibbon()` state, same
 * `Ribbon` constant, so the three cannot drift on which commands exist or when they disable.
 */
/** `fit` says how many commands are on the row after each fitting, so the header can give way. */
const emit = defineEmits<{ action: [action: string]; fit: [shown: number] }>();

/**
 * THE HEADER IS ONE ROW, AND THIS IS IT. The `start` slot holds the logo (and on a phone the
 * hamburger), the `end` slot the right cluster - version, live chip, Needs you, the activity gauge,
 * the account menu. Their widths come off the room the commands are fitted into, so the cluster
 * never sits on top of a command and a command never pushes the cluster off the bar.
 *
 * `commands` false is the phone row (below `sm`): the same logo and cluster with no commands, which
 * the drawer lists instead. `tight` is the row short of room (`lib/headerFit.ts`): each command's
 * padding drops from 8px to 4px a side, the active team's title is capped at 3rem with an ellipsis
 * (its full name the title's tooltip and aria-label, and whole on the strip's team tab), and the row
 * is measured again.
 */
const props = withDefaults(defineProps<{ commands?: boolean; tight?: boolean }>(), { commands: true, tight: false });

const {
  board,
  teams,
  ribbon,
  titleOf,
  needsTeam,
  tooltipFor,
  badgeFor,
} = useRibbon();

const entries = ribbonEntries(ribbon);

function run(item: RibbonItem) {
  if (item.action && !needsTeam(item)) emit('action', item.action);
}

// ---------------------------------------------------------------------------------------------
// OVERFLOW.
//
// Measured once with everything shown, then re-fitted on every resize from those numbers - see
// `fitCount` for why one measurement is enough. Re-measured only when an entry's WIDTH can change,
// which is the active team's name in front of its group, and once fonts land.

const bar = ref<HTMLElement | null>(null);
const band = ref<HTMLElement | null>(null);
const start = ref<HTMLElement | null>(null);
const end = ref<HTMLElement | null>(null);
const overflow = ref<ComponentPublicInstance | null>(null);
const entryEls: (HTMLElement | null)[] = [];
let edges: number[] = [];

const shown = ref(entries.length);

function setEntry(index: number, el: Element | ComponentPublicInstance | null) {
  entryEls[index] = el instanceof HTMLElement ? el : null;
}

function available() {
  if (!bar.value) return 0;

  const style = getComputedStyle(bar.value);
  const inner = bar.value.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0');

  return commandRoom(inner, slotWidth(start.value), slotWidth(end.value));
}

/** A slot's footprint; its spacing is its own padding, so the box is the whole of it. */
function slotWidth(el: HTMLElement | null) {
  return el?.getBoundingClientRect().width ?? 0;
}

/**
 * THE OVERFLOW BUTTON'S FOOTPRINT, READ FROM THE BUTTON: its box and the margin before it, never a
 * constant (a dense round button is 41.6px wide here, not the 40 once written down). The button is
 * always rendered so there is one to read; while nothing spills it is out of the flow, hidden and
 * out of the tab order.
 */
function overflowWidth() {
  const el = overflow.value?.$el as HTMLElement | undefined;
  if (!el) return 0;

  return el.getBoundingClientRect().width + parseFloat(getComputedStyle(el).marginLeft || '0');
}

/**
 * NOT WHILE MEASURING, AND NOT BEFORE THE FIRST MEASUREMENT. The cluster is observed too, so a
 * resize can land between `measure` showing every entry and reading their edges; a fitting then
 * hides a tail whose edges read zero, and zero fits anywhere - every command shown, clipped.
 */
let measuring = false;

function fit() {
  if (measuring || edges.length === 0) return;

  shown.value = fitCount(edges, available(), overflowWidth());
  emit('fit', shown.value);
}

/** The layout fits the row again after the cluster gives way a step, whether or not that step
 *  changed a width the observer would see. */
defineExpose({ refit: fit });

/** One measurement at a time: two that overlapped (mount, then fonts landing) read each other's
 *  hidden tail. Each waits for the one before it. */
let measured: Promise<void> = Promise.resolve();

function measure(): Promise<void> {
  measured = measured.then(measureOnce);
  return measured;
}

async function measureOnce() {
  if (!props.commands) return;

  measuring = true;
  shown.value = entries.length;
  await nextTick();

  const left = band.value?.getBoundingClientRect().left ?? 0;

  edges = entryEls.map((el) => (el ? el.getBoundingClientRect().right - left : 0));
  measuring = false;
  fit();
}

let observer: ResizeObserver | null = null;

onMounted(() => {
  void measure();

  // The cluster is watched as well as the bar: a count that grows, live going offline or a longer
  // account name widens it without the bar changing size at all.
  if (typeof ResizeObserver !== 'undefined' && bar.value) {
    observer = new ResizeObserver(() => fit());
    for (const el of [bar.value, start.value, end.value, overflow.value?.$el as HTMLElement | undefined]) if (el) observer.observe(el);
  }

  // The first measurement can run before IBM Plex has loaded, against a fallback face with
  // different advance widths - and every edge after that would be off by the difference.
  void document.fonts?.ready.then(() => measure());
});

onBeforeUnmount(() => observer?.disconnect());

watch(() => entries.map((entry) => (entry.groupStart ? titleOf(entry.tab) : '')).join('\n'), () => void measure());
watch(() => props.tight, () => void measure());

/** The spilled tail, grouped under its tab's name the way the drawer groups it. */
const overflowGroups = computed(() => {
  const groups: { label: string; title: string; entries: RibbonEntry[] }[] = [];

  for (const entry of entries.slice(shown.value)) {
    const last = groups[groups.length - 1];

    if (last && last.label === entry.tab.label) last.entries.push(entry);
    else groups.push({ label: entry.tab.label, title: titleOf(entry.tab), entries: [entry] });
  }

  return groups;
});
</script>

<template>
  <div ref="bar" class="ribbon" :class="{ 'ribbon--tight': tight }">
    <div ref="start" class="ribbon-start">
      <slot name="start" />
    </div>

    <div v-if="commands" class="ribbon-commands" role="toolbar" aria-label="Commands">
      <div ref="band" class="ribbon-band">
        <div
          v-for="(entry, index) in entries"
          v-show="index < shown"
          :key="entry.key"
          :ref="(el) => setEntry(index, el)"
          class="ribbon-entry"
          :class="{ 'ribbon-group-start': entry.groupStart && index > 0 }"
          :aria-label="entry.groupStart ? entry.tab.label : undefined"
        >
          <!-- The heading a tab takes from live state - in practice the ACTIVE TEAM's name, in front
               of the commands that act on it. Its rule is the ACCENT and the one accent moment on the
               bar: it marks the group whose every control is scoped to that team. -->
          <span
            v-if="entry.groupStart && titleOf(entry.tab)"
            class="ribbon-group-title"
            :class="{ 'ribbon-group-title--capped': tight }"
            :aria-label="titleOf(entry.tab)"
            data-test="ribbon-group-title"
          >
            {{ titleOf(entry.tab) }}
            <q-tooltip>{{ tight ? `${titleOf(entry.tab)} - ${entry.tab.label}` : entry.tab.label }}</q-tooltip>
          </span>

          <q-separator v-if="entry.item.kind === 'separator'" vertical inset class="q-mx-xs" />

          <!-- Built from live state, so the file describes only that it belongs here. -->
          <q-btn-dropdown
            v-else-if="entry.item.kind === 'team-switcher'"
            flat
            dense
            no-caps
            class="ribbon-btn"
            content-class="above-concierge"
            :disable="teams.length === 0"
            :icon="entry.item.icon"
            :label="entry.item.label"
          >
            <RibbonTeamList @pick="board.openTeam" />
          </q-btn-dropdown>

          <span v-else class="ribbon-item-wrap">
            <q-btn
              flat
              dense
              no-caps
              class="ribbon-btn"
              :icon="entry.item.icon"
              :label="entry.item.label"
              :disable="needsTeam(entry.item)"
              @click="run(entry.item)"
            >
              <!-- TEXT PLUS ICON, NEVER COLOUR ALONE, and ABSENT WHEN THERE IS NOTHING TO SAY: a
                   badge that is always lit is wallpaper. -->
              <q-badge
                v-if="badgeFor(entry.item)"
                floating
                color="warning"
                text-color="dark"
                :aria-label="badgeFor(entry.item)?.label"
              >
                <q-icon :name="badgeFor(entry.item)?.icon" size="12px" class="q-mr-xs" />
                {{ badgeFor(entry.item)?.text }}
              </q-badge>
            </q-btn>

            <!-- On the WRAPPER, not the button: a disabled control swallows pointer events, so a
                 tooltip inside it never fires. -->
            <q-tooltip v-if="badgeFor(entry.item)">{{ badgeFor(entry.item)?.label }}</q-tooltip>
            <q-tooltip v-else-if="tooltipFor(entry.item)">{{ tooltipFor(entry.item) }}</q-tooltip>
          </span>
        </div>
      </div>

      <q-btn
        ref="overflow"
        flat
        dense
        round
        icon="more_horiz"
        class="ribbon-overflow"
        :class="{ 'ribbon-overflow--idle': shown >= entries.length }"
        :aria-label="shown < entries.length ? 'More commands' : undefined"
        :aria-hidden="shown < entries.length ? undefined : 'true'"
        :tabindex="shown < entries.length ? undefined : -1"
      >
        <q-menu v-if="shown < entries.length" anchor="bottom right" self="top right" class="above-concierge">
          <q-list dense class="ribbon-overflow-menu">
            <template v-for="group in overflowGroups" :key="group.label">
              <q-item-label header class="ribbon-overflow-section">{{ group.label }}</q-item-label>
              <q-item v-if="group.title" dense class="ribbon-overflow-title">
                <q-item-section>{{ group.title }}</q-item-section>
              </q-item>

              <template v-for="entry in group.entries" :key="entry.key">
                <q-separator v-if="entry.item.kind === 'separator'" />

                <q-item v-else-if="entry.item.kind === 'team-switcher'" clickable :disable="teams.length === 0">
                  <q-item-section v-if="entry.item.icon" avatar>
                    <q-icon :name="entry.item.icon" />
                  </q-item-section>
                  <q-item-section>{{ entry.item.label }}</q-item-section>
                  <q-item-section side>
                    <q-icon name="chevron_right" />
                  </q-item-section>

                  <q-menu anchor="top start" self="top end" class="above-concierge">
                    <RibbonTeamList @pick="board.openTeam" />
                  </q-menu>
                </q-item>

                <q-item
                  v-else
                  v-close-popup
                  clickable
                  :disable="needsTeam(entry.item)"
                  @click="run(entry.item)"
                >
                  <q-item-section v-if="entry.item.icon" avatar>
                    <q-icon :name="entry.item.icon" />
                  </q-item-section>
                  <q-item-section>
                    <q-item-label>{{ entry.item.label }}</q-item-label>
                    <!-- A disabled row in a menu fires no tooltip either, so its reason is a caption,
                         as in the phone menu. -->
                    <q-item-label v-if="needsTeam(entry.item)" caption>{{ tooltipFor(entry.item) }}</q-item-label>
                  </q-item-section>
                  <q-item-section v-if="badgeFor(entry.item)" side>
                    <q-badge
                      color="warning"
                      text-color="dark"
                      :aria-label="badgeFor(entry.item)?.label"
                    >{{ badgeFor(entry.item)?.text }}</q-badge>
                  </q-item-section>
                </q-item>
              </template>
            </template>
          </q-list>
        </q-menu>
      </q-btn>
    </div>

    <div ref="end" class="ribbon-end" data-test="ribbon-cluster">
      <slot name="end" />
    </div>
  </div>
</template>

<style scoped>
/* ONE ROW, 44px including its hairline - the whole of the command bar. The height is stated rather
   than left to the content, so a taller control added later spills visibly rather than quietly
   growing the header QLayout measures.

   The ink is STATED, not inherited: this lives inside the dark QHeader, which sets `color: white`
   on everything below it, and the bar's own ground is light. */
.ribbon {
  box-sizing: border-box;
  height: 44px;
  display: flex;
  align-items: center;
  padding: 0 8px;
  background: var(--os-chrome);
  color: var(--os-ink-muted);
  border-bottom: 1px solid var(--os-rule-strong);
}

/* The logo at the left and the cluster at the end never shrink: the commands between them are what
   gives way, into the overflow menu. */
.ribbon-start,
.ribbon-end {
  flex: none;
  height: 100%;
  display: flex;
  align-items: center;
}

.ribbon-start:not(:empty) {
  padding-right: 8px;
}

.ribbon-end {
  margin-left: auto;
  padding-left: 8px;
  gap: 4px;
}

.ribbon-commands {
  position: relative;
  flex: 1 1 auto;
  min-width: 0;
  height: 100%;
  display: flex;
  align-items: center;
}

/* Clipped rather than scrolled: what does not fit is in the overflow menu, and a sideways
   scrollbar on a toolbar is a second, worse answer to the same question. */
.ribbon-band {
  flex: 1 1 auto;
  min-width: 0;
  height: 100%;
  display: flex;
  align-items: center;
  overflow: hidden;
}

.ribbon-entry {
  flex: none;
  display: inline-flex;
  align-items: center;
  position: relative;
}

/* The group separator, INSET: a rule that touched the bar's top and bottom would box each group in
   like a table cell; one that stops short reads as a separator between groups. */
.ribbon-group-start {
  margin-left: 6px;
  padding-left: 7px;
}

.ribbon-group-start::before {
  content: '';
  position: absolute;
  left: 0;
  top: 6px;
  bottom: 6px;
  width: 1px;
  background: var(--os-rule-strong);
}

/* The active team's name. Ellipsised rather than allowed to widen the group: a long team name
   would otherwise push half the bar into the overflow menu. */
.ribbon-group-title {
  font-size: 0.7812rem;
  font-weight: 600;
  line-height: 18px;
  color: var(--os-ink);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 12rem;
  margin: 0 6px 0 4px;
  border-bottom: 2px solid var(--os-primary, var(--q-primary));
}

/* Steps five and six: a row that short keeps its commands rather than a long team name. */
.ribbon-group-title--capped {
  max-width: 3rem;
}

.ribbon-item-wrap {
  display: inline-flex;
}

.ribbon-btn {
  font-size: 0.7812rem;
  font-weight: 500;
  padding: 0 8px;
  min-height: 32px;
  border-radius: 4px;
}

/* The fifth give-way step: half the padding a side, the labels whole. */
.ribbon--tight .ribbon-btn {
  padding: 0 4px;
}

.ribbon-overflow {
  flex: none;
  margin-left: 8px;
}

/* Nothing spills: kept for its measurement, out of the flow and out of sight. */
.ribbon-overflow--idle {
  position: absolute;
  right: 0;
  visibility: hidden;
  pointer-events: none;
}

/* A QBtn with no colour prop paints with currentColor, so the ink above reaches every control. */
.ribbon :deep(.q-btn) {
  color: inherit;
}

/* Hover lifts to full ink and brings the accent with it, on the ICON alone - colouring the whole
   button would flash every label vermilion as the pointer crossed the bar. */
.ribbon :deep(.q-btn:hover) {
  color: var(--os-ink);
}

.ribbon :deep(.q-btn:hover .q-icon) {
  color: var(--os-primary, var(--q-primary));
}

/* A disabled control is genuinely quiet rather than merely dimmed: Quasar's own opacity leaves a
   glyph that still reads as available at a glance, and half this bar is disabled whenever no team
   is active - the first thing a new tenant sees. */
.ribbon :deep(.q-btn:disabled) {
  color: var(--os-ink-faint);
  opacity: 1;
}

.ribbon-overflow-menu {
  min-width: 14rem;
  color: var(--os-ink-muted);
}

.ribbon-overflow-section {
  font-size: 0.6875rem;
  font-weight: 500;
  letter-spacing: 0.09em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
}

.ribbon-overflow-title {
  color: var(--os-ink);
  font-weight: 600;
  border-bottom: 2px solid var(--os-primary, var(--q-primary));
  margin: 0 16px 4px;
  padding-left: 0;
  min-height: 28px;
}
</style>
