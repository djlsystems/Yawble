<script setup lang="ts">
import { ref, watch } from 'vue';
import { teamSolution } from '../api/client';
import { uploadDocument } from '../api/documents';
import { asDocumentsFolderKey, type SolutionMissing, type TeamId } from '../api/types';
import { missingLine } from '../lib/solutions';

/**
 * A TEAM INSTALLED FROM A SOLUTION PACKAGE THAT STILL WAITS FOR ITS PERSON: one line per input the
 * install asked for and did not get ("Waiting for Resume/ - your reference resume"), until each is
 * provided. A document gets an upload box into its folder, and the banner re-reads after it; a
 * connection or setting is provided in the member's settings or Admin -> Connections.
 *
 * Renders nothing for a team not installed from a package (a 404) and for one missing nothing.
 */
const props = defineProps<{ team: TeamId }>();

const missing = ref<SolutionMissing[]>([]);
const problem = ref('');
const uploading = ref('');

async function load() {
  const team = props.team;
  try {
    const record = await teamSolution(team);
    if (team !== props.team) return;
    missing.value = record?.missing ?? [];
    problem.value = '';
  } catch {
    // The banner is advisory: a failed read shows nothing rather than a false "blocked".
    if (team === props.team) missing.value = [];
  }
}

watch(() => props.team, () => void load(), { immediate: true });

async function upload(item: SolutionMissing, file: File | null) {
  if (!file) return;
  uploading.value = item.name;
  problem.value = '';
  try {
    // The live team's documents folder is addressed by the team's identifier.
    await uploadDocument(asDocumentsFolderKey(props.team), file, item.name.replace(/\/$/, ''));
    await load();
  } catch (cause) {
    problem.value = `${file.name} was not uploaded: ${cause instanceof Error ? cause.message : String(cause)}`;
  } finally {
    uploading.value = '';
  }
}
</script>

<template>
  <q-banner v-if="missing.length > 0" dense class="os-bg-tint-warn q-mb-sm" data-solution-blocked>
    <template #avatar><q-icon name="block" /></template>
    <div class="text-weight-medium">Blocked until you provide what this team waits for:</div>
    <div
      v-for="item in missing"
      :key="`${item.kind}/${item.member ?? ''}/${item.name}`"
      class="row items-center q-gutter-x-sm q-mt-xs"
      :data-missing="item.name"
    >
      <span>{{ missingLine(item) }}</span>
      <q-file
        v-if="item.kind === 'document'"
        :model-value="null"
        dense
        outlined
        :label="`Upload to ${item.name.replace(/\/$/, '')}/`"
        :loading="uploading === item.name"
        :disable="uploading !== ''"
        class="blocked-upload"
        @update:model-value="(file: File | null) => upload(item, file)"
      />
      <span v-else-if="item.kind === 'connection'" class="text-caption os-text-muted">
        Bind one in the member's settings; connect an account in Admin → Connections.
      </span>
      <span v-else class="text-caption os-text-muted">Set it in the member's settings.</span>
    </div>
    <div v-if="problem" class="text-negative q-mt-xs" data-blocked-problem>{{ problem }}</div>
  </q-banner>
</template>

<style scoped>
.blocked-upload {
  min-width: 14rem;
}
</style>
