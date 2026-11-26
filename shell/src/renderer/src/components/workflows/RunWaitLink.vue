<script setup lang="ts">
import { openDeliverablesPage } from '../../deliverables/page';

// What a waiting run waits on: its tell's deliverable, as a link that opens it on the Deliverables page (every team: a tell
// can go to another team). Without a deliverable id (an older core) it just says the run waits.
const props = defineProps<{ deliverable?: number | null }>();
const emit = defineEmits<{ open: [deliverable: number] }>();

function open() {
  if (props.deliverable == null) return;
  emit('open', props.deliverable);
  openDeliverablesPage({ teamId: null, deliverable: props.deliverable });
}
</script>

<template>
  <span class="wait" data-test="run-waiting">
    <template v-if="deliverable != null">waiting on
      <button type="button" class="link" data-test="run-wait-deliverable" :data-deliverable="deliverable"
        :title="`Open deliverable ${deliverable} on the Deliverables page`" @click.stop="open">deliverable #{{ deliverable }}</button>
    </template>
    <template v-else>waiting on a tell</template>
  </span>
</template>

<style scoped>
.wait { color: var(--ink-soft); font-size: 12px; }
.link { border: 0; background: transparent; padding: 0; color: var(--accent); font: inherit; cursor: pointer; text-decoration: underline; }
</style>
