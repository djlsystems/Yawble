<script setup lang="ts">
import type { RepoCheckRefusal, RepoChoice } from '../api/types';
import { RepoChoiceLabels, offeredChoices } from '../lib/repoChoices';

/**
 * A REFUSED REPOSITORY CHECK, in the dialog that sent it: the Host's sentence as it came (it names
 * the URL and git's reason), then each URL with ONLY the choices the Host listed for it. Picking one
 * is emitted; the dialog resends the same request with `repoChoices`, and nothing exists until then.
 */
defineProps<{
  refusal: RepoCheckRefusal;
  /** The choices already picked this round, so a picked button reads as picked. */
  chosen: Record<string, RepoChoice>;
  busy?: boolean;
}>();

const emit = defineEmits<{ choose: [url: string, choice: RepoChoice] }>();
</script>

<template>
  <q-banner dense class="os-bg-tint-error" data-repo-check-refusal>
    <template #avatar><q-icon name="error" class="text-negative" /></template>
    <div class="os-body text-negative" data-repo-check-sentence>{{ refusal.error }}</div>

    <div v-for="entry in refusal.repos" :key="entry.url" class="q-mt-sm" :data-repo-check-url="entry.url">
      <div v-if="refusal.repos.length > 1" class="mono os-body">{{ entry.url }}</div>
      <div class="row q-gutter-sm q-mt-xs">
        <q-btn
          v-for="choice in offeredChoices(entry)"
          :key="choice"
          outline
          dense
          no-caps
          :color="chosen[entry.url] === choice ? 'primary' : undefined"
          :label="RepoChoiceLabels[choice]"
          :data-repo-choice="choice"
          :disable="busy"
          :loading="busy && chosen[entry.url] === choice"
          @click="emit('choose', entry.url, choice)"
        />
      </div>
    </div>
  </q-banner>
</template>
