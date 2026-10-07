<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { createLocalRepo, listLocalRepos } from '../api/client';
import type { LocalRepo } from '../api/types';
import { firstProblem, localRepoName, repoFolderName } from '../lib/rules';

/**
 * BESIDE THE URL FIELD in New Team and Team settings: "Create a local repository" - a name, checked
 * as the Host checks it - and the instance's existing local repositories, each attachable. Either
 * way what joins the team's list is `local:<name>`, a reference and never a path. Both routes are a
 * person's; what a local repository is lives on Admin → Repositories.
 */
const props = withDefaults(defineProps<{
  /** The team's list as it stands, so a repository already in it reads as attached. */
  attached: readonly string[];

  /** Whether to offer "Create a local repository" at all. New Team has its own way to make one -
   *  named after the team, when it is created - and two ways to do one thing read as two things. */
  offerCreate?: boolean;
}>(), { offerCreate: true });

const emit = defineEmits<{ attach: [reference: string] }>();

const repos = ref<LocalRepo[]>([]);
const loadProblem = ref('');
const name = ref('');
const creating = ref(false);
const createProblem = ref('');

const nameRules = [(value: unknown) => (String(value ?? '').trim() === '' ? true : localRepoName(value))];
const canCreate = computed(
  () => name.value.trim().length > 0 && firstProblem([localRepoName], name.value) === null && !creating.value,
);

/** Folder names already in the list, compared as the server compares them. */
const takenFolders = computed(
  () => new Set(props.attached.map((entry) => repoFolderName(entry)?.toLowerCase()).filter(Boolean)),
);

function isAttached(repo: LocalRepo) {
  return takenFolders.value.has(repo.name.toLowerCase());
}

async function load() {
  loadProblem.value = '';
  try {
    repos.value = await listLocalRepos();
  } catch (cause) {
    loadProblem.value = cause instanceof Error ? cause.message : String(cause);
  }
}

async function create() {
  if (!canCreate.value) return;
  creating.value = true;
  createProblem.value = '';

  try {
    const made = await createLocalRepo(name.value.trim());
    name.value = '';
    await load();
    emit('attach', made.reference);
  } catch (cause) {
    createProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    creating.value = false;
  }
}

onMounted(load);
</script>

<template>
  <div class="q-mt-sm" data-local-repo-picker>
    <template v-if="props.offerCreate">
    <div class="row items-center no-wrap">
      <q-input
        v-model="name"
        class="col mono"
        outlined
        dense
        hide-bottom-space
        label="Create a local repository"
        placeholder="name"
        :rules="nameRules"
        :error="createProblem !== ''"
        :error-message="createProblem"
        @keydown.enter.prevent="create"
      />
      <q-btn
        class="col-auto q-ml-sm"
        outlined
        dense
        no-caps
        label="Create"
        :loading="creating"
        :disable="!canCreate"
        @click="create"
      />
    </div>
    <div class="text-caption os-text-muted q-mt-xs">
      A git repository kept on this instance only, with no hosting service. It starts on
      <span class="mono">main</span> with one empty commit.
    </div>
    </template>

    <div v-if="loadProblem" class="os-body text-negative q-mt-xs">
      Could not list the local repositories: {{ loadProblem }}
    </div>
    <div v-else-if="repos.length > 0" class="q-mt-sm">
      <div class="text-caption os-text-muted">Local repositories:</div>
      <div class="row q-gutter-xs q-mt-xs">
        <q-chip
          v-for="repo in repos"
          :key="repo.name"
          dense
          class="mono"
          :clickable="!isAttached(repo)"
          :disable="isAttached(repo)"
          :aria-label="isAttached(repo) ? `${repo.reference} is attached` : `Attach ${repo.reference}`"
          @click="isAttached(repo) || emit('attach', repo.reference)"
        >
          {{ repo.reference }}
        </q-chip>
      </div>
    </div>
  </div>
</template>
