<script setup lang="ts">
/**
 * FORK IT FOR ME: a person gives only the upstream's URL, and GitHub makes the fork - in the
 * account the instance's GH_TOKEN belongs to, or in the organisation typed here (`--org`), left
 * empty unless the person sets it. The fork becomes the repository's URL (origin) and the upstream
 * its upstream; the parent dialog adds both. A token that cannot fork is answered with GitHub's
 * own sentence and what the token needs, and the person can fork on GitHub and give both URLs.
 */
import { computed, ref } from 'vue'
import { forkUpstream } from '../api/client'
import type { ForkResult } from '../api/types'
import { upstreamUrlProblem } from '../lib/contributor'

const emit = defineEmits<{ forked: [fork: ForkResult] }>()

const upstream = ref('')
const organisation = ref('')
const forking = ref(false)
const error = ref<string | null>(null)

const upstreamProblem = computed(() =>
  upstream.value.trim() === '' ? null : upstreamUrlProblem(upstream.value, undefined),
)
const canFork = computed(() => upstream.value.trim() !== '' && upstreamProblem.value === null && !forking.value)

async function fork() {
  if (!canFork.value) return
  forking.value = true
  error.value = null
  try {
    const made = await forkUpstream(upstream.value.trim(), organisation.value)
    emit('forked', made)
    upstream.value = ''
    organisation.value = ''
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    forking.value = false
  }
}
</script>

<template>
  <div class="fork-it">
    <div class="text-caption os-text-muted">
      Contributing to a project you cannot push to? Give its URL and the platform forks it for you.
    </div>
    <div class="row items-start no-wrap q-gutter-sm q-mt-xs">
      <q-input
        v-model="upstream"
        class="col mono"
        outlined
        dense
        hide-bottom-space
        label="Upstream URL"
        placeholder="https://github.com/project/repo"
        aria-label="Upstream URL to fork"
        :error="upstreamProblem !== null"
        :error-message="upstreamProblem ?? undefined"
        @keydown.enter.prevent="fork"
      />
      <q-input
        v-model="organisation"
        class="col-4"
        outlined
        dense
        hide-bottom-space
        label="Organisation (optional)"
        aria-label="Organisation to fork into"
      />
      <q-btn
        class="col-auto"
        outlined
        dense
        no-caps
        label="Fork it for me"
        :loading="forking"
        :disable="!canFork"
        @click="fork"
      />
    </div>
    <p v-if="error" class="fork-it-error">{{ error }}</p>
  </div>
</template>

<style scoped>
.fork-it {
  margin-top: 10px;
}

.fork-it-error {
  margin: 6px 0 0;
  color: var(--q-negative);
  font-size: 12px;
}
</style>
