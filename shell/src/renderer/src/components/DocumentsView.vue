<script setup lang="ts">
import { computed, ref } from 'vue';
import type { YawbleApi, TeamSummary } from '../../../shared/api';
import DocumentsBrowser from './DocumentsBrowser.vue';
import Icon from './Icon.vue';
import LoadingNote from './LoadingNote.vue';

/**
 * The Documents page: the root of every team's documents. Each team has its own folder (what its agents write for
 * the person: reports, specs, plans); open one to browse, read, edit and add files.
 */
// loading: the teams have not arrived from the core yet (the app's first overview).
const props = defineProps<{ teams: TeamSummary[]; archived: TeamSummary[]; api?: YawbleApi; loading?: boolean }>();

const openId = ref<number | null>(null);
const team = computed(() => [...props.teams, ...props.archived].find((t) => t.id === openId.value) ?? null);
</script>

<template>
  <section class="docs-place" :class="{ fill: !!team }" data-test="documents-view">
    <div v-if="team" class="wrap fill">
      <DocumentsBrowser :key="team.id" :team="team" :api="api" fill above="Documents" @up="openId = null" />
    </div>
    <div v-else class="wrap">
      <h2>Documents</h2>
      <p class="muted">Each team keeps its documents here: what its agents write for you (reports, specs, plans) and anything you add.</p>
      <LoadingNote v-if="loading" text="Loading the teams' documents…" />
      <div v-else class="grid" data-test="documents-root">
        <button v-for="t in teams" :key="t.id" class="folder" :data-test="`documents-team-${t.name}`" @click="openId = t.id">
          <span class="ico">📁</span>
          <span class="name">{{ t.name }}</span>
        </button>
        <button v-for="t in archived" :key="t.id" class="folder archived" @click="openId = t.id">
          <span class="ico">📁</span>
          <span class="name">{{ t.name }} <span class="muted small">(archived)</span></span>
        </button>
      </div>
      <p v-if="!loading && !teams.length && !archived.length" class="muted"><Icon name="teams" :size="14" /> No teams yet: create one and its documents appear here.</p>
    </div>
  </section>
</template>

<style scoped>
.docs-place { height: 100%; overflow: auto; padding: 4px 24px 24px; }
.wrap { max-width: 1100px; margin: 12px auto; }
/* An open team's browser fills the page's height (the list and the preview scroll on their own); the folder grid
   scrolls as a page. No CSS containment here: the dialogs opened from it must cover the window. */
.docs-place.fill { display: flex; flex-direction: column; overflow: hidden; }
.wrap.fill { flex: 1 1 0; min-height: 0; width: 100%; box-sizing: border-box; margin: 12px auto 0; display: flex; flex-direction: column; }
h2 { margin: 0 0 4px; font-size: 22px; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; margin: 18px 0; }
.folder { display: flex; align-items: center; gap: 10px; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--line); background: var(--surface); text-align: left; }
.folder:hover { border-color: #b9c2d3; background: var(--hover); }
.folder.archived { opacity: .75; }
.ico { font-size: 20px; }
.name { font-weight: 600; }
.muted { color: var(--muted); }
.small { font-size: 12px; font-weight: 400; }
</style>
