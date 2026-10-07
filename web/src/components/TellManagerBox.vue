<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { tell } from '../api/client';
import { asMemberId, type TeamId, type TeamWorkflowTiming } from '../api/types';

/**
 * WHERE A PERSON GIVES A TEAM WORK FROM THE TEAM PAGE. The words go to the Manager as the person's
 * own instruction, through the same `tell` route as any person's tell - no second door - and the
 * Manager plans it from there.
 *
 * NEW WORK BY DEFAULT. Choosing an open workflow passes its correlation id as the instruction's
 * causation, the number steering hands the Concierge, so the Manager's run joins that thread.
 */
const props = defineProps<{ team: TeamId; workflows: TeamWorkflowTiming[] }>();

// The Manager's NAME, which a relabel does not change; the route addresses members by name.
const manager = asMemberId('Manager');

const words = ref('');
const workflow = ref<number | null>(null);
const sending = ref(false);
const sent = ref(false);
const problem = ref('');

const options = computed(() => [
  { label: 'New work', value: null as number | null },
  ...props.workflows
    .filter((w) => w.correlation != null && w.endedAt == null)
    .map((w) => ({
      label: w.subject ? `#${w.correlation} ${w.subject}` : `#${w.correlation}`,
      value: w.correlation as number | null,
    })),
]);

// A workflow that has since closed, or another team's, is not something to continue.
watch(options, (now) => {
  if (!now.some((o) => o.value === workflow.value)) workflow.value = null;
});

watch(() => props.team, () => {
  words.value = '';
  workflow.value = null;
  sent.value = false;
  problem.value = '';
});

async function submit() {
  const instruction = words.value.trim();
  if (!instruction || sending.value) return;

  sending.value = true;
  sent.value = false;
  problem.value = '';
  try {
    await tell(props.team, manager, instruction, workflow.value);
    words.value = '';
    sent.value = true;
  } catch (cause) {
    // The words stay, so a refusal costs the person nothing to retry.
    problem.value = `Not sent: ${cause instanceof Error ? cause.message : String(cause)}`;
  } finally {
    sending.value = false;
  }
}
</script>

<template>
  <div class="tell-manager q-mb-sm" data-tell-manager>
    <q-input
      v-model="words"
      type="textarea"
      autogrow
      outlined
      dense
      label="Tell the Manager what you want done"
      data-tell-manager-input
      @update:model-value="sent = false"
    />
    <div class="row items-center q-gutter-sm q-mt-xs">
      <q-select
        v-model="workflow"
        :options="options"
        emit-value
        map-options
        dense
        outlined
        options-dense
        label="For"
        class="tell-manager-workflow"
        data-tell-manager-workflow
      />
      <q-btn
        unelevated
        no-caps
        color="primary"
        label="Tell the Manager"
        :loading="sending"
        :disable="!words.trim()"
        data-tell-manager-send
        @click="submit"
      />
      <span class="text-caption os-text-muted" data-tell-manager-next>
        The Manager plans the work and hands it to the team. The board shows it as it moves.
      </span>
    </div>
    <div v-if="sent" class="text-caption text-positive q-mt-xs" data-tell-manager-sent>Sent. The Manager has it.</div>
    <div v-if="problem" class="text-negative q-mt-xs" data-tell-manager-error>{{ problem }}</div>
  </div>
</template>

<style scoped>
.tell-manager {
  max-width: 48rem;
}

.tell-manager-workflow {
  min-width: 14rem;
}
</style>
