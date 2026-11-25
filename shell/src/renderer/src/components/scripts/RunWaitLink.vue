<script setup lang="ts">
import { openWorkflowsPage } from '../../workflows/page';

// What a waiting run waits on: its tell's workflow, as a link that opens it on the Workflows page (every team: a tell
// can go to another team). Without a workflow id (an older core) it just says the run waits.
const props = defineProps<{ workflow?: number | null }>();
const emit = defineEmits<{ open: [workflow: number] }>();

function open() {
  if (props.workflow == null) return;
  emit('open', props.workflow);
  openWorkflowsPage({ teamId: null, workflow: props.workflow });
}
</script>

<template>
  <span class="wait" data-test="run-waiting">
    <template v-if="workflow != null">waiting on
      <button type="button" class="link" data-test="run-wait-workflow" :data-workflow="workflow"
        :title="`Open workflow ${workflow} on the Workflows page`" @click.stop="open">workflow #{{ workflow }}</button>
    </template>
    <template v-else>waiting on a tell</template>
  </span>
</template>

<style scoped>
.wait { color: var(--ink-soft); font-size: 12px; }
.link { border: 0; background: transparent; padding: 0; color: var(--accent); font: inherit; cursor: pointer; text-decoration: underline; }
</style>
