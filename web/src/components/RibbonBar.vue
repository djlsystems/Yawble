<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type ComponentPublicInstance } from 'vue';
import { useRibbon } from '../lib/useRibbon';
import { fitCount, ribbonEntries, type RibbonEntry, type RibbonItem } from '../lib/ribbon';
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
 * same tab names the drawer uses. Below `sm` (600px) MainLayout unmounts this and opens
 * RibbonMobileMenu from a hamburger instead - same `useRibbon()` state, same `Ribbon` constant, so
 * the three cannot drift on which commands exist or when they disable.
 */
const emit = defineEmits<{ action: [action: string] }>();

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

/** The overflow button's footprint: a dense round button and its margin. */
const OverflowWidth = 40;

const bar = ref<HTMLElement | null>(null);
const band = ref<HTMLElement | null>(null);
const entryEls: (HTMLElement | null)[] = [];
let edges: number[] = [];

const shown = ref(entries.length);

function setEntry(index: number, el: Element | ComponentPublicInstance | null) {
  entryEls[index] = el instanceof HTMLElement ? el : null;
}

function available() {
  if (!bar.value) return 0;

  const style = getComputedStyle(bar.value);

  return bar.value.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0');
}

function fit() {
  shown.value = fitCount(edges, available(), OverflowWidth);
}

async function measure() {
  shown.value = entries.length;
  await nextTick();

  const left = band.value?.getBoundingClientRect().left ?? 0;

  edges = entryEls.map((el) => (el ? el.getBoundingClientRect().right - left : 0));
  fit();
}

let observer: ResizeObserver | null = null;

onMounted(() => {
  void measure();

  if (typeof ResizeObserver !== 'undefined' && bar.value) {
    observer = new ResizeObserver(() => fit());
    observer.observe(bar.value);
  }

  // The first measurement can run before IBM Plex has loaded, against a fallback face with
  // different advance widths - and every edge after that would be off by the difference.
  void document.fonts?.ready.then(() => measure());
});

onBeforeUnmount(() => observer?.disconnect());

watch(() => entries.map((entry) => (entry.groupStart ? titleOf(entry.tab) : '')).join('\n'), () => void measure());

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
  <div ref="bar" class="ribbon" role="toolbar" aria-label="Commands">
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
        <span v-if="entry.groupStart && titleOf(entry.tab)" class="ribbon-group-title">
          {{ titleOf(entry.tab) }}
          <q-tooltip>{{ entry.tab.label }}</q-tooltip>
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
      v-if="shown < entries.length"
      flat
      dense
      round
      icon="more_horiz"
      class="ribbon-overflow"
      aria-label="More commands"
    >
      <q-menu anchor="bottom right" self="top right" class="above-concierge">
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
  font-size: 12.5px;
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

.ribbon-item-wrap {
  display: inline-flex;
}

.ribbon-btn {
  font-size: 12.5px;
  font-weight: 500;
  padding: 0 8px;
  min-height: 32px;
  border-radius: 4px;
}

.ribbon-overflow {
  flex: none;
  margin-left: 8px;
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
  font-size: 11px;
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
