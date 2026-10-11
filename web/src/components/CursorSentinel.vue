<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';

/**
 * THE BOTTOM OF AN INFINITE LIST. Asks for more when it scrolls into view, and says why it
 * has stopped asking when it has.
 *
 * AN IntersectionObserver RATHER THAN A SCROLL HANDLER, because the lists it ends live inside dialog
 * bodies and card sections whose scroll parent is not the window, and the observer's root of `null`
 * is right whichever of those it is in. It asks again after every read that leaves it visible: a page
 * too short to push the sentinel off screen would otherwise stop the list at the first page.
 *
 * `manual` holds the observer back until the button has been pressed once. A board of short feeds has
 * every card's sentinel on screen at load, and reading every card's history unasked would be a burst
 * of requests nobody wanted.
 *
 * The button is not decoration. It is the same request for a browser with no observer, for a keyboard,
 * and for a read that failed - an error that could only be retried by scrolling away and back is one
 * nobody finds.
 */
const props = defineProps<{
  loading: boolean;
  exhausted: boolean;
  error?: string;
  label?: string;
  doneLabel?: string;
  manual?: boolean;
}>();

const emit = defineEmits<{ more: [] }>();

const el = ref<HTMLElement | null>(null);
const visible = ref(false);
let observer: IntersectionObserver | null = null;

function ask() {
  if (props.manual) return;
  if (props.loading || props.exhausted || props.error) return;
  emit('more');
}

onMounted(() => {
  if (typeof IntersectionObserver === 'undefined' || !el.value) return;

  observer = new IntersectionObserver((entries) => {
    visible.value = entries.some((entry) => entry.isIntersecting);
    if (visible.value) ask();
  });
  observer.observe(el.value);
});

// A read that finished with the sentinel still on screen asks again.
watch(
  () => props.loading,
  (now) => {
    if (!now && visible.value) ask();
  },
);

onBeforeUnmount(() => observer?.disconnect());
</script>

<template>
  <div ref="el" class="cursor-sentinel text-caption os-text-muted">
    <q-spinner v-if="loading" size="16px" />
    <span v-else-if="error" class="text-negative">
      {{ error }}
      <q-btn flat dense no-caps size="sm" label="Try again" @click="emit('more')" />
    </span>
    <span v-else-if="exhausted">{{ doneLabel ?? 'Nothing older.' }}</span>
    <q-btn v-else flat dense no-caps size="sm" :label="label ?? 'Load older'" @click="emit('more')" />
  </div>
</template>

<style scoped>
.cursor-sentinel {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 28px;
  padding: 4px 0;
}
</style>
