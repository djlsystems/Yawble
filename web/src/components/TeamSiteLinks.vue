<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { listTeamSites, type Site } from '../api/sites';
import type { TeamId } from '../api/types';
import { openSiteTab } from '../lib/siteOpen';
import { siteLinkWords } from '../lib/teamPicture';

/**
 * THE TEAM'S SITES, BESIDE ITS NAME: one globe link per published site, opening it in a new tab.
 * A site's cards leave the board once their work is done, so without this nothing on the team's
 * screen says the team has a site at all.
 *
 * Only a LIVE site (a published version) gets a link: an unpublished one has nothing to open. The
 * link is the site's own `url`, same origin, so a middle-click, a modified click or "copy link" still
 * names the entry. A plain click opens the site the way the Sites dialog's Open does (`openSiteTab`):
 * through a capability the app asks for, so the new tab never depends on the browser sending the
 * session cookie with its first request (W27510). Re-read when the team changes and when the window comes back
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

/** A plain click: the app's own open. A modified or middle click is left to the browser's link. */
function open(event: Event, site: Site) {
  if (!(event instanceof MouseEvent)) return;
  if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  void openSiteTab(site);
}

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
      :aria-label="`Open ${siteLinkWords(site.name)}`"
      :data-team-site="site.name"
      @click="open($event, site)"
    >
      <q-tooltip>Open {{ siteLinkWords(site.name) }} (version {{ site.liveVersion }})</q-tooltip>
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
