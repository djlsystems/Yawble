<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { getMarketplace, refreshMarketplace } from '../api/client';
import type { MarketplaceCatalog, MarketplacePackage } from '../api/types';
import { installedWords, kindWords, noneMatchWords, notCheckedWords, packageMatches, plainNeeds, unreachableWords } from '../lib/marketplace';
import { fetchIntoDocuments } from '../lib/marketplaceFetch';
import { whenWords } from '../lib/solutionPanel';
import FilterText from './FilterText.vue';

/**
 * MARKETPLACE > BROWSE: the published package catalog as the Host read it, one tile per package -
 * its name, summary, kind, version, what it needs in a few short plain lines (the full lines under a
 * Details expander, closed until asked), and whether this instance has it - and Refresh, which asks
 * the Host to read the catalog again.
 *
 * THE FILTER narrows the cards as you type, case-insensitively, by name, summary, description and
 * the words for what a package needs (`packageMatches`); it reads nothing again, and a filter that
 * keeps no card says so in a sentence. Each read is handed to the caller (`read`), which is how
 * Installed knows which teams the catalog lists a newer version for.
 *
 * GET FETCHES, IT NEVER INSTALLS: the Host downloads and checks the package and unpacks it into the
 * documents; `get` hands the folder to the caller, which opens the install wizard (a solution) or the
 * plugin install dialog (a plugin) on it, so the person reviews and installs exactly as from a folder.
 *
 * A catalog the Host has not read says so and why, never as an empty list; a catalog the console
 * cannot reach and a Get the Host refused each read as a sentence. EVERYTHING FROM THE CATALOG IS
 * TEXT: names, summaries and needs are interpolated, never bound as HTML.
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

onMounted(() => read(getMarketplace));

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

    <div v-if="unreachable" class="os-body text-negative" role="alert" data-catalog-unreachable>{{ unreachable }}</div>

    <div v-else-if="catalog && !catalog.checked" class="os-body os-text-muted" data-catalog-not-checked>
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
        <div v-for="pkg in shownPackages" :key="pkg.id" class="os-tile get-started-tile" :data-catalog-package="pkg.id">
          <div class="os-tile-head">
            <q-icon :name="pkg.kind === 'solution' ? 'apps' : 'extension'" size="18px" class="get-started-icon" aria-hidden="true" />
            <div class="row items-baseline no-wrap q-gutter-x-sm get-started-title">
              <div class="text-subtitle1 text-weight-medium ellipsis get-started-name" :title="pkg.name" data-package-name>{{ pkg.name }}</div>
              <div class="text-caption os-text-muted get-started-version" data-package-version>{{ pkg.version }}</div>
            </div>
          </div>
          <div class="os-tile-line os-text-muted" data-package-kind>{{ kindWords(pkg.kind) }}</div>
          <div class="os-tile-line get-started-text" data-package-summary>{{ pkg.summary }}</div>
          <div v-if="plainNeeds(pkg.catalogNeeds).length" class="os-tile-line get-started-text" data-package-needs>
            <div v-for="(need, i) in plainNeeds(pkg.catalogNeeds)" :key="i" data-package-need>{{ need }}</div>
          </div>
          <div v-else class="os-tile-line os-text-muted" data-package-needs>It needs nothing more.</div>
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
            <q-btn
              unelevated
              dense
              no-caps
              color="primary"
              icon="download"
              label="Get"
              :aria-label="`Get ${pkg.name}`"
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

.get-started-icon {
  color: var(--os-ink-muted);
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
  justify-content: flex-end;
}
</style>
