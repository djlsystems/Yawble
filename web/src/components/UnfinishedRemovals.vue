<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useQuasar } from 'quasar';
import { listRemovals, retryRemoval } from '../api/client';
import type { UnfinishedRemoval } from '../api/types';
import { removalKindLabel, removalOwner } from '../lib/removals';
import { useConsoleStore } from '../stores/console';

/**
 * EVERY REMOVAL THAT DID NOT FINISH, from `GET /api/removals`: a deleted team's root, a deleted
 * member's workspace, and a folder a Reset emptied. Each names every path still on disk and has its
 * own Retry (`POST /api/removals/retry` with its path). The Host also retries every one at start, so
 * one that is still here has outlived that too.
 *
 * On the Teams view because that is where teams are deleted and where a person comes looking for
 * what a deletion or Reset left behind. Nothing renders when there is nothing to report.
 */
const $q = useQuasar();
const board = useConsoleStore();

const removals = ref<UnfinishedRemoval[]>([]);
/** The note a retry gave, per path, until the row goes: why it was set aside. */
const notes = ref<Record<string, string>>({});
const retrying = ref<string | null>(null);
const problem = ref('');

async function load() {
  try {
    removals.value = await listRemovals();
    problem.value = '';
  } catch (cause) {
    problem.value = `Could not read the unfinished removals: ${cause instanceof Error ? cause.message : String(cause)}`;
  }
}

defineExpose({ load });

onMounted(load);

function teamLabel(team: string) {
  return board.teams.find((candidate) => candidate.id === team)?.name ?? null;
}

async function retry(removal: UnfinishedRemoval) {
  retrying.value = removal.path;

  try {
    const { retried } = await retryRemoval(removal.path);
    const result = retried.find((entry) => entry.path === removal.path) ?? retried[0];

    if (!result || result.finished) {
      removals.value = removals.value.filter((entry) => entry.path !== removal.path);
      $q.notify({ type: 'positive', timeout: 5000, message: `${removal.path} has been removed.` });
      return;
    }

    removals.value = removals.value.map((entry) =>
      entry.path === removal.path ? { ...entry, remaining: result.remaining, attempts: entry.attempts + 1 } : entry,
    );
    if (result.note) notes.value = { ...notes.value, [removal.path]: result.note };
  } catch (cause) {
    $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
  } finally {
    retrying.value = null;
  }
}
</script>

<template>
  <div v-if="removals.length > 0 || problem" class="q-mt-md" data-removals>
    <div class="text-subtitle2 q-mb-xs">Unfinished removals</div>
    <div class="text-caption os-text-muted q-mb-sm">
      Folders a deletion or Reset could not remove whole. The Host retries each one when it next
      starts, or retry it now.
    </div>

    <q-banner v-if="problem" dense class="os-bg-tint-error text-negative q-mb-sm">{{ problem }}</q-banner>

    <q-card
      v-for="removal in removals"
      :key="removal.path"
      flat
      bordered
      class="q-mb-sm removal-row"
      :data-removal="removal.path"
    >
      <q-card-section class="row items-center q-gutter-x-sm q-py-sm">
        <q-badge outline color="warning" :label="removalKindLabel(removal.kind)" data-removal-kind />
        <span class="text-weight-medium">{{ removalOwner(removal, teamLabel(removal.team)) }}</span>
        <code class="os-text-muted ellipsis">{{ removal.path }}</code>
        <q-space />
        <span class="text-caption os-text-muted">{{ removal.attempts }} attempt(s)</span>
        <q-btn
          flat
          dense
          no-caps
          size="sm"
          icon="refresh"
          label="Retry"
          :aria-label="`Retry removal of ${removal.path}`"
          :loading="retrying === removal.path"
          :disable="retrying !== null"
          @click="retry(removal)"
        />
      </q-card-section>

      <q-card-section class="q-pt-none">
        <div class="text-caption os-text-muted">Still on disk:</div>
        <ul class="q-pl-md q-my-none removal-paths">
          <li v-for="path in removal.remaining" :key="path"><code>{{ path }}</code></li>
        </ul>
        <div v-if="notes[removal.path]" class="text-negative text-caption q-mt-xs">{{ notes[removal.path] }}</div>
      </q-card-section>
    </q-card>
  </div>
</template>

<style scoped>
.removal-paths {
  word-break: break-all;
}
</style>
