<script setup lang="ts">
import { ref, watch } from 'vue';
import { deleteLocalRepo, listLocalRepos } from '../api/client';
import type { LocalRepo } from '../api/types';
import { useConsoleStore } from '../stores/console';

/**
 * ADMIN > REPOSITORIES: the instance's local repositories, from `GET /api/local-repos` - each one's
 * name and reference, size, default branch, last commit, and the teams using it. Delete asks first
 * and is refused by the Host while a team uses the repository, with a sentence naming the teams;
 * that sentence is shown as it came. New ones are made beside a team's URL field, in New Team and
 * Team settings.
 */
const open = defineModel<boolean>({ required: true });

const board = useConsoleStore();

const repos = ref<LocalRepo[]>([]);
const loading = ref(false);
const error = ref('');

async function load() {
  loading.value = true;
  error.value = '';

  try {
    repos.value = await listLocalRepos();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

watch(open, (showing) => {
  if (showing) void load();
}, { immediate: true });

/** Bytes as a person reads them. */
function size(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

function when(at: string | null) {
  return at ? new Date(at).toLocaleString() : '';
}

function teamName(id: string) {
  return board.teams.find((team) => team.id === id)?.name ?? id;
}

// --- Delete, which asks first --------------------------------------------------------------------

const confirming = ref<LocalRepo | null>(null);
const deleting = ref(false);
const deleteProblem = ref('');

function askDelete(repo: LocalRepo) {
  deleteProblem.value = '';
  confirming.value = repo;
}

async function confirmDelete() {
  const repo = confirming.value;
  if (!repo || deleting.value) return;

  deleting.value = true;
  deleteProblem.value = '';

  try {
    await deleteLocalRepo(repo.name);
    confirming.value = null;
    await load();
  } catch (cause) {
    deleteProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    deleting.value = false;
  }
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-lg" data-local-repos-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Repositories</div>
        <q-space />
        <q-btn flat dense no-caps icon="refresh" label="Refresh" :loading="loading" @click="load" />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section>
        <div class="os-body os-text-muted q-mb-sm">
          Local repositories live on this instance only. A team uses one as
          <span class="mono">local:&lt;name&gt;</span> in its repository list; create one beside the URL
          field in New Team or Team settings.
        </div>

        <div v-if="error" class="os-body text-negative">Could not list the local repositories: {{ error }}</div>
        <div v-else-if="!loading && repos.length === 0" class="os-body os-text-muted">
          No local repositories yet.
        </div>

        <q-markup-table v-else flat bordered dense separator="horizontal">
          <thead>
            <tr>
              <th class="text-left">Name</th>
              <th class="text-right">Size</th>
              <th class="text-left">Default branch</th>
              <th class="text-left">Last commit</th>
              <th class="text-left">Teams</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr v-for="repo in repos" :key="repo.name" :data-local-repo="repo.name">
              <td class="text-left mono">{{ repo.reference }}</td>
              <td class="text-right">{{ size(repo.sizeBytes) }}</td>
              <td class="text-left mono">{{ repo.defaultBranch ?? 'not known' }}</td>
              <td class="text-left">
                <template v-if="repo.lastCommit">
                  <span class="mono">{{ repo.lastCommit.sha.slice(0, 7) }}</span>
                  {{ repo.lastCommit.subject }}
                  <div class="text-caption os-text-muted">{{ when(repo.lastCommit.committedAt) }}</div>
                </template>
                <span v-else class="os-text-muted">none</span>
              </td>
              <td class="text-left">
                <template v-if="!(repo.unused ?? repo.teams.length === 0)">{{ repo.teams.map(teamName).join(', ') }}</template>
                <!-- UNUSED, said as such: a deleted team's local repository is kept, and this is
                     where a person finds it and deletes it. The Host's flag, with the empty list
                     as the answer from a Host that predates it. -->
                <span v-else class="os-text-muted" data-local-repo-unused>unused - no team uses it</span>
              </td>
              <td class="text-right">
                <q-btn
                  flat
                  dense
                  round
                  color="negative"
                  icon="delete"
                  :aria-label="`Delete ${repo.name}`"
                  @click="askDelete(repo)"
                />
              </td>
            </tr>
          </tbody>
        </q-markup-table>
      </q-card-section>
    </q-card>
  </q-dialog>

  <q-dialog :model-value="confirming !== null" @update:model-value="(showing) => showing || (confirming = null)">
    <q-card v-if="confirming" class="os-dialog-sm" data-local-repo-delete>
      <q-card-section>
        <div class="os-dialog-title">Delete {{ confirming.name }}?</div>
        <div class="os-body q-mt-sm">
          This removes the repository and every branch and commit in it from this instance. It cannot
          be undone.
        </div>
        <div v-if="confirming.teams.length > 0" class="os-body text-warning q-mt-sm">
          Used by {{ confirming.teams.map(teamName).join(', ') }}: it will be refused until it is
          removed from their repositories.
        </div>
        <div v-if="deleteProblem" class="os-body text-negative q-mt-sm">{{ deleteProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="confirming = null" />
        <q-btn
          unelevated
          no-caps
          color="negative"
          :label="`Delete ${confirming.name}`"
          :loading="deleting"
          @click="confirmDelete"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
