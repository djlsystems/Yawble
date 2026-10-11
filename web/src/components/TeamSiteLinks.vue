<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { listTeamSites, type Site } from '../api/sites';
import type { TeamId } from '../api/types';

/**
 * THE TEAM'S SITES, BESIDE ITS NAME: one globe link per published site, opening it in a new tab.
 * A site's cards leave the board once their work is done, so without this nothing on the team's
 * screen says the team has a site at all.
 *
 * Only a LIVE site (a published version) gets a link: an unpublished one has nothing to open. The
 * link is the site's own `url`, same origin, and the Host issues the capability when the tab asks,
 * as the Sites dialog's Open does. Re-read when the team changes and when the window comes back
 * into focus (a site published while the person was away appears then); no polling. A failed read
 * shows nothing: the links are a convenience, the Sites dialog is the record.
 */
const props = defineProps<{ team: TeamId }>();

const sites = ref<Site[]>([]);

const live = computed(() =>
  sites.value
    .filter((site) => site.liveVersion !== null)
    .sort((a, b) => a.name.localeCompare(b.name)),
);

async function load() {
  const team = props.team;
  try {
    const answer = await listTeamSites(team);
    if (team === props.team) sites.value = answer;
  } catch {
    if (team === props.team) sites.value = [];
  }
}

watch(() => props.team, () => {
  sites.value = [];
  void load();
}, { immediate: true });

const onFocus = () => void load();
onMounted(() => window.addEventListener('focus', onFocus));
onUnmounted(() => window.removeEventListener('focus', onFocus));
</script>

<template>
  <span v-if="live.length > 0" class="team-site-links" data-team-site-links>
    <q-btn
      v-for="site in live"
      :key="site.name"
      flat
      round
      dense
      size="sm"
      icon="language"
      type="a"
      :href="site.url"
      target="_blank"
      rel="noopener"
      :aria-label="`Open the ${site.name} site`"
      :data-team-site="site.name"
    >
      <q-tooltip>Open the {{ site.name }} site (version {{ site.liveVersion }})</q-tooltip>
    </q-btn>
  </span>
</template>

<style scoped>
.team-site-links {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}
</style>
