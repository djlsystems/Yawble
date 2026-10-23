<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { getMarketplace, refreshMarketplace } from '../api/client';
import type { MarketplaceCatalog, MarketplacePackage } from '../api/types';
import { contentsLines, getLabel, includedInWords, installedWords, kindWords, noneMatchWords, notCheckedWords, packageMatches, plainNeeds, unreachableWords } from '../lib/marketplace';
import { fetchIntoDocuments } from '../lib/marketplaceFetch';
import { claimFirstOpenRefresh } from '../lib/marketplaceRefresh';
import { whenWords } from '../lib/solutionPanel';
import FilterText from './FilterText.vue';

/**
 * MARKETPLACE > BROWSE: the published package catalog as the Host read it, one tile per package -
 * its name, summary, kind, version, what it needs in a few short plain lines (the full lines under a
 * Details expander, closed until asked), and whether this instance has it - and Refresh, which asks
 * the Host to read the catalog again.
 *
 * A CARD WEARS ITS KIND: a solution's edge and kind icon are `--os-kind-solution` (the accent), a
 * plugin's `--os-kind-plugin`, set in every built-in theme (`os-tile-kind-*`, css/tiles.scss). When the
 * catalog says what a package holds (`contents`), the card opens with it in bullets
 * (`contentsLines`): a solution's plugins, members, skills, triggers and sites; a plugin's settings,
 * keys and sign-ins, its events and skills, then the solutions that include it. A plugin's bullets
 * say what it asks for, so its short needs lines give way to them; a catalog without `contents`
 * shows the card as it was. ONE Documentation link opens the package's page on the catalog's own
 * site (`page`, from the Host) in a new tab; the long description stays on that page.
 *
 * THE FILTER narrows the cards as you type, case-insensitively, by name, summary, description and
 * the words for what a package needs (`packageMatches`); it reads nothing again, and a filter that
 * keeps no card says so in a sentence. Each read is handed to the caller (`read`), which is how
 * Installed knows which teams the catalog lists a newer version for.
 *
 * A PLUGIN ALREADY INSTALLED offers no Get (it is installed once for the instance): its card says
 * Installed, and Update when the catalog lists a newer version (`getLabel`). A solution keeps Get,
 * since each install makes another team.
 *
 * GET FETCHES, IT NEVER INSTALLS: the Host downloads and checks the package and unpacks it into the
 * documents; `get` hands the folder to the caller, which opens the install wizard (a solution) or the
 * plugin install dialog (a plugin) on it, so the person reviews and installs exactly as from a folder.
 *
 * A catalog the Host has not read says so and why, never as an empty list; a catalog the console
 * cannot reach and a Get the Host refused each read as a sentence. EVERYTHING FROM THE CATALOG IS
 * TEXT: names, summaries and needs are interpolated, never bound as HTML.
 *
 * THE FIRST OPEN IN A PAGE LOAD READS THE CATALOG AGAIN: after the catalog the Host has, Browse
 * refreshes once (`claimFirstOpenRefresh`), showing what it has with Refresh's loading indicator
 * until the answer comes. A catalog not checked asks nothing more. A Refresh that fails keeps the
 * catalog shown, with the sentence above it.
 */
const emit = defineEmits<{ get: [pkg: MarketplacePackage, folder: string]; read: [catalog: MarketplaceCatalog] }>();

const catalog = ref<MarketplaceCatalog | null>(null);
const loading = ref(false);
const unreachable = ref('');
const getting = ref('');
const refused = ref<{ id: string; text: string } | null>(null);
const details = ref<Record<string, boolean>>({});
const filterText = ref('');

const shownPackages = computed(() => (catalog.value?.packages ?? []).filter((pkg) => packageMatches(pkg, filterText.value)));

async function read(how: () => Promise<MarketplaceCatalog>) {
  loading.value = true;
  unreachable.value = '';
  try {
    catalog.value = await how();
    emit('read', catalog.value);
  } catch (cause) {
    unreachable.value = unreachableWords(cause instanceof Error ? cause.message : String(cause));
  } finally {
    loading.value = false;
  }
}

onMounted(async () => {
  await read(getMarketplace);
  if (catalog.value?.checked && claimFirstOpenRefresh()) await read(refreshMarketplace);
});

function refresh() {
  refused.value = null;
  void read(refreshMarketplace);
}

async function get(pkg: MarketplacePackage) {
  if (getting.value) return;

  getting.value = pkg.id;
  refused.value = null;
  try {
    const fetched = await fetchIntoDocuments(pkg);
    if (fetched.ok) emit('get', { ...pkg, kind: fetched.kind }, fetched.folder);
    else refused.value = { id: pkg.id, text: fetched.text };
  } finally {
    getting.value = '';
  }
}
</script>

<template>
  <div data-get-started>
    <div class="row items-center q-mb-md">
      <div class="os-body os-text-muted col">
        Packages published for this product. Get downloads one into Documents and opens its install, where you review it first.
        <span v-if="catalog?.checkedAt" data-catalog-checked-at>Checked {{ whenWords(catalog.checkedAt) }}.</span>
      </div>
      <q-btn flat dense no-caps icon="refresh" label="Refresh" :loading="loading" data-catalog-refresh @click="refresh" />
    </div>

    <div v-if="unreachable" class="os-body text-negative" :class="{ 'q-mb-md': catalog }" role="alert" data-catalog-unreachable>{{ unreachable }}</div>

    <div v-if="catalog && !catalog.checked" class="os-body os-text-muted" data-catalog-not-checked>
      {{ notCheckedWords(catalog) }}
    </div>

    <div v-else-if="catalog && catalog.packages.length === 0" class="os-body os-text-muted" data-catalog-empty>
      The package catalog lists no packages.
    </div>

    <template v-else-if="catalog">
      <FilterText
        v-model="filterText"
        class="get-started-filter q-mb-md"
        placeholder="Filter: name, what it does or what it needs"
        aria-label="Filter the catalog"
        data-catalog-filter
      />

      <div v-if="shownPackages.length === 0" class="os-body os-text-muted" data-catalog-none-match>{{ noneMatchWords(filterText) }}</div>

      <div v-else class="os-tiles get-started-tiles">
        <div
          v-for="pkg in shownPackages"
          :key="pkg.id"
          class="os-tile get-started-tile"
          :class="`os-tile-kind-${pkg.kind}`"
          :data-catalog-package="pkg.id"
        >
          <div class="os-tile-head">
            <q-icon :name="pkg.kind === 'solution' ? 'apps' : 'extension'" size="18px" class="os-tile-kind-icon" aria-hidden="true" data-package-kind-icon />
            <div class="row items-baseline no-wrap q-gutter-x-sm get-started-title">
              <div class="text-subtitle1 text-weight-medium ellipsis get-started-name" :title="pkg.name" data-package-name>{{ pkg.name }}</div>
              <div class="text-caption os-text-muted get-started-version" data-package-version>{{ pkg.version }}</div>
            </div>
          </div>
          <div class="os-tile-line os-text-muted" data-package-kind>{{ kindWords(pkg.kind) }}</div>
          <ul v-if="contentsLines(pkg).length" class="os-tile-line get-started-text get-started-contents" data-package-contents>
            <li v-for="(line, i) in contentsLines(pkg)" :key="i" data-package-content>{{ line }}</li>
          </ul>
          <div v-if="includedInWords(pkg)" class="os-tile-line get-started-text" data-package-included-in>{{ includedInWords(pkg) }}</div>
          <div class="os-tile-line get-started-text" data-package-summary>{{ pkg.summary }}</div>
          <template v-if="pkg.kind !== 'plugin' || !contentsLines(pkg).length">
            <div v-if="plainNeeds(pkg.catalogNeeds).length" class="os-tile-line get-started-text" data-package-needs>
              <div v-for="(need, i) in plainNeeds(pkg.catalogNeeds)" :key="i" data-package-need>{{ need }}</div>
            </div>
            <div v-else class="os-tile-line os-text-muted" data-package-needs>It needs nothing more.</div>
          </template>
          <div v-if="pkg.needs.length" class="os-tile-line get-started-text">
            <q-btn
              flat
              dense
              no-caps
              size="sm"
              class="get-started-details"
              :icon="details[pkg.id] ? 'keyboard_arrow_up' : 'keyboard_arrow_down'"
              label="Details"
              :aria-expanded="details[pkg.id] ? 'true' : 'false'"
              data-package-details-toggle
              @click="details[pkg.id] = !details[pkg.id]"
            />
            <ul v-if="details[pkg.id]" class="q-my-none q-pl-md" data-package-details>
              <li v-for="(need, i) in pkg.needs" :key="i">{{ need }}</li>
            </ul>
          </div>
          <div class="os-tile-line" :class="pkg.updateAvailable ? 'text-warning' : pkg.installed ? 'text-positive' : 'os-text-muted'" data-package-installed>
            {{ installedWords(pkg) }}
          </div>
          <div v-if="refused?.id === pkg.id" class="os-tile-line text-negative get-started-text" role="alert" data-package-refused>{{ refused.text }}</div>

          <div class="get-started-actions">
            <a
              v-if="pkg.page"
              class="get-started-docs os-body"
              :href="pkg.page"
              target="_blank"
              rel="noopener"
              :aria-label="`Documentation for ${pkg.name} (opens in a new tab)`"
              data-package-docs
            >Documentation</a>
            <q-btn
              v-if="getLabel(pkg)"
              unelevated
              dense
              no-caps
              color="primary"
              :icon="getLabel(pkg) === 'Update' ? 'upgrade' : 'download'"
              :label="getLabel(pkg)!"
              :aria-label="`${getLabel(pkg)} ${pkg.name}`"
              :loading="getting === pkg.id"
              :disable="getting !== '' && getting !== pkg.id"
              data-package-get
              @click="get(pkg)"
            />
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.get-started-tiles {
  --os-tile-min: min(18rem, 100%);
}

.get-started-filter {
  max-width: 28rem;
}

.get-started-tile {
  min-width: 0;
  overflow: hidden;
}

.get-started-contents {
  margin: 4px 0;
  padding-left: 1.1rem;
}

.get-started-title,
.get-started-name {
  min-width: 0;
  flex: 1 1 auto;
}

.get-started-version {
  flex: 0 0 auto;
}

.get-started-text {
  overflow-wrap: anywhere;
}

.get-started-details {
  margin-left: -6px;
}

.get-started-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 12px;
}

/* Ink, not the accent: link text holds 4.5:1 in every theme, the underline says it is a link. */
.get-started-docs {
  margin-right: auto;
  color: var(--os-ink);
  text-decoration: underline;
}
</style>
