<script setup lang="ts" generic="T extends string">
/**
 * THE ONE TAB STRIP every dialog uses, so tabs look and overflow the same everywhere.
 *
 * - Left-aligned at every width: `:breakpoint="0"`, or Quasar stretches each tab across the strip
 *   once it is under 600px wide, and two tabs read as two halves.
 * - A strip wider than its dialog shows an arrow at each end (`outside-arrows`), on touch screens
 *   too (`mobile-arrows`), so a tab past the edge is never simply cut off with nothing saying
 *   there is more. Each dialog is sized so its tabs fit at its usual width; the arrows are for a
 *   narrow window or a phone.
 * - Labels as written (`no-caps`), muted, the open tab in the primary colour with its underline.
 * - MEASURED AGAIN once the strip is really its size. QTabs adds up its tabs with
 *   `getBoundingClientRect`, which includes transforms, and measures as it mounts: inside a dialog
 *   that is while the dialog's opening scale-up has it shrunk, so tabs that overflow read as
 *   fitting, the arrows are hidden, and nothing measures again because the strip's own size never
 *   changes (Team settings' last tab was cut off with no arrow). So the strip asks QTabs to measure
 *   again when a transition or animation around it ends, when the fonts have loaded, and when a
 *   tab's own size changes (a label like "Members (3)"). QTabs measures again whenever
 *   `outside-arrows` changes, so `remeasure` turns it off and on within one tick.
 *
 * Extra classes and `data-` attributes on `<DialogTabs>` land on the strip. Pinned by
 * `__tests__/dialog-tabs.spec.ts`, which also refuses a bare `<q-tabs>` anywhere else, and by
 * `__tests__/dialog-tabs-remeasure.mount.spec.ts`.
 */
import { nextTick, onBeforeUnmount, onMounted, onUpdated, ref } from 'vue';

const model = defineModel<T>({ required: true });

const outsideArrows = ref(true);
const strip = ref<{ $el: HTMLElement } | null>(null);

let pending = false;
function remeasure() {
  if (pending) return;
  pending = true;
  outsideArrows.value = false;
  void nextTick(() => {
    outsideArrows.value = true;
    pending = false;
  });
}

/** A transition or animation that ends on something around the strip (the dialog opening). */
function onSettled(event: Event) {
  const root = strip.value?.$el;
  if (root && event.target instanceof Node && event.target !== root && event.target.contains(root)) remeasure();
}

let tabSizes: ResizeObserver | null = null;
let observed: Element[] = [];
/** Watches each tab's size; again only when the tabs themselves changed, never on a re-render alone. */
function observeTabs() {
  const content = strip.value?.$el?.querySelector?.('.q-tabs__content');
  if (!tabSizes || !content) return;
  const tabs = Array.from(content.children);
  if (tabs.length === observed.length && tabs.every((tab, i) => tab === observed[i])) return;
  for (const tab of tabs) if (!observed.includes(tab)) tabSizes.observe(tab);
  for (const tab of observed) if (!tabs.includes(tab)) tabSizes.unobserve(tab);
  observed = tabs;
}

onMounted(() => {
  document.addEventListener('transitionend', onSettled, true);
  document.addEventListener('animationend', onSettled, true);
  void document.fonts?.ready.then(remeasure);
  if (typeof ResizeObserver !== 'undefined') {
    // The observer reports each tab once as it starts observing it; only a later size is a change.
    const sizes = new WeakMap<Element, number>();
    tabSizes = new ResizeObserver((entries) => {
      let changed = false;
      for (const entry of entries) {
        const width = entry.contentRect.width;
        if (sizes.has(entry.target) && sizes.get(entry.target) !== width) changed = true;
        sizes.set(entry.target, width);
      }
      if (changed) remeasure();
    });
    observeTabs();
  }
});

onUpdated(observeTabs);

onBeforeUnmount(() => {
  document.removeEventListener('transitionend', onSettled, true);
  document.removeEventListener('animationend', onSettled, true);
  tabSizes?.disconnect();
});
</script>

<template>
  <q-tabs
    ref="strip"
    v-model="model"
    dense
    no-caps
    align="left"
    :breakpoint="0"
    :outside-arrows="outsideArrows"
    mobile-arrows
    active-color="primary"
    indicator-color="primary"
    class="os-text-muted"
  >
    <slot />
  </q-tabs>
</template>
