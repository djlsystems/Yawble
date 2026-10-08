<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import {
  SiteDeletionConfirmationRequired,
  deleteSite,
  getSite,
  listSiteDocuments,
  listSites,
  rollbackSite,
  unpublishSite,
  type Site,
  type SiteDetail,
  type SiteDocument,
} from '../api/sites';
import { useConsoleStore } from '../stores/console';
import { documentFields } from '../lib/siteDocumentFields';

/**
 * ADMIN > SITES: every site across teams, from `GET /api/sites` - its team, live version, who
 * published it and when, and how much data it holds. Open shows it in a new tab; Versions lists
 * the kept versions and rolls back to one, or, for a site not published, publishes its newest again; Unpublish takes it down and keeps its files and data;
 * Data reads its collections, read-only. Delete asks first, with the Host's own sentence saying
 * what would be lost. Every refusal is shown as the Host worded it.
 *
 * `team` is the Active Team group's way in: the same list, filtered to that team, with a way back
 * to all of them.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{ team?: string | null }>();

const board = useConsoleStore();

const sites = ref<Site[]>([]);
const loading = ref(false);
const error = ref('');

/** The team the list is narrowed to. Taken from `team` on each open, cleared by All teams. */
const only = ref<string | null>(null);

/** The last refusal from a row action, in the Host's words. Cleared on each open. */
const problem = ref('');
/** `team/name` of the row whose action is in flight. */
const busy = ref('');

const shown = computed(() => (only.value ? sites.value.filter((site) => site.team === only.value) : sites.value));

async function load() {
  loading.value = true;
  error.value = '';

  try {
    sites.value = await listSites();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

watch(open, (showing) => {
  if (!showing) return;

  only.value = props.team || null;
  problem.value = '';
  void load();
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

function documents(count: number) {
  return count === 1 ? '1 document' : `${count} documents`;
}

const key = (site: Site) => `${site.team}/${site.name}`;

// --- Row actions ---------------------------------------------------------------------------------

/** Same origin: the Host issues the site's capability itself when the tab asks for it. */
function openSite(site: Site) {
  window.open(site.url, '_blank', 'noopener');
}

async function unpublish(site: Site) {
  busy.value = key(site);
  problem.value = '';

  try {
    await unpublishSite(site.team, site.name);
    await load();
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = '';
  }
}

// --- Delete, which asks first, in the Host's words -----------------------------------------------

/** The site and the Host's sentence saying what deleting it would lose. */
const confirming = ref<{ site: Site; sentence: string } | null>(null);
const deleting = ref(false);
const deleteProblem = ref('');

/**
 * The first DELETE carries no `confirm`, so the Host refuses it with the sentence the person is
 * then asked with. Nothing is deleted until they press Delete in that question.
 */
async function askDelete(site: Site) {
  busy.value = key(site);
  problem.value = '';
  deleteProblem.value = '';

  try {
    await deleteSite(site.team, site.name);
    // The Host deleted it without asking. Not what it does, but the list must say so if it did.
    await load();
  } catch (cause) {
    if (cause instanceof SiteDeletionConfirmationRequired) confirming.value = { site, sentence: cause.message };
    else problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = '';
  }
}

async function confirmDelete() {
  const site = confirming.value?.site;
  if (!site || deleting.value) return;

  deleting.value = true;
  deleteProblem.value = '';

  try {
    await deleteSite(site.team, site.name, true);
    confirming.value = null;
    await load();
  } catch (cause) {
    deleteProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    deleting.value = false;
  }
}

// --- Versions and data, one site at a time -------------------------------------------------------

const detailOpen = ref(false);
const detailView = ref<'versions' | 'data'>('versions');
const detailSite = ref<Site | null>(null);
const detail = ref<SiteDetail | null>(null);
const detailProblem = ref('');

const collection = ref('');
const docs = ref<SiteDocument[]>([]);
const docsLoading = ref(false);

async function showDetail(site: Site, view: 'versions' | 'data') {
  detailSite.value = site;
  detailView.value = view;
  detail.value = null;
  detailProblem.value = '';
  collection.value = '';
  docs.value = [];
  detailOpen.value = true;

  try {
    detail.value = await getSite(site.team, site.name);
  } catch (cause) {
    detailProblem.value = cause instanceof Error ? cause.message : String(cause);
  }
}

const rollingBack = ref<number | null>(null);

async function rollback(version: number) {
  const site = detailSite.value;
  if (!site || rollingBack.value !== null) return;

  rollingBack.value = version;
  detailProblem.value = '';

  try {
    await rollbackSite(site.team, site.name, version);
    detail.value = await getSite(site.team, site.name);
    await load();
  } catch (cause) {
    detailProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    rollingBack.value = null;
  }
}

async function pickCollection(name: string) {
  const site = detailSite.value;
  if (!site) return;

  collection.value = name;
  docs.value = [];
  docsLoading.value = true;
  detailProblem.value = '';

  try {
    docs.value = await listSiteDocuments(site.team, site.name, name);
  } catch (cause) {
    detailProblem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    docsLoading.value = false;
  }
}

/** A document as TEXT. It is whatever the site stored, so it is never rendered as markup. */
function pretty(doc: unknown) {
  return JSON.stringify(doc, null, 2);
}

/**
 * EACH DOCUMENT AS FIELDS AND VALUES, its JSON behind the Show JSON switch, as Admin > Log keeps its
 * details. One switch for every document shown. A document with no fields to name (not an object)
 * shows its JSON either way.
 */
const showJson = ref(false);

function shownFields(doc: unknown) {
  return showJson.value ? null : documentFields(doc);
}

/**
 * The newest kept version, when the site is not published: its action puts the site back live as
 * it was, so it says Publish rather than Roll back. Versions arrive newest first.
 */
const republishable = computed(() =>
  detail.value && detail.value.site.liveVersion === null ? (detail.value.versions[0]?.version ?? null) : null,
);

function versionAction(version: number) {
  return version === republishable.value
    ? { icon: 'publish', label: `Publish v${version}` }
    : { icon: 'undo', label: `Roll back to v${version}` };
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-xl" data-sites-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Sites<template v-if="only"> · {{ teamName(only) }}</template></div>
        <q-space />
        <q-btn v-if="only" flat dense no-caps icon="groups" label="All teams" @click="only = null" />
        <q-btn flat dense no-caps icon="refresh" label="Refresh" :loading="loading" @click="load" />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section>
        <div class="os-body os-text-muted q-mb-sm">
          A team publishes a site from its members' work. Unpublishing takes it down and keeps its files
          and data; deleting removes all of it.
        </div>

        <div v-if="problem" class="os-body text-negative q-mb-sm" data-sites-problem>{{ problem }}</div>

        <div v-if="error" class="os-body text-negative">Could not list the sites: {{ error }}</div>
        <div v-else-if="!loading && shown.length === 0" class="os-body os-text-muted">
          No sites yet.
        </div>

        <!-- ONE TILE PER SITE, as Plugins and Agents lay out theirs: whether it is live on the
             head, its team, publisher and data in the body, the actions at the foot. -->
        <div v-else class="os-tiles site-tiles">
          <div v-for="site in shown" :key="key(site)" class="os-tile site-tile" :data-site="key(site)">
            <div class="os-tile-head">
              <q-icon name="language" size="18px" class="site-icon" aria-hidden="true" />
              <span class="text-weight-medium mono">{{ site.name }}</span>
              <q-space />
              <q-badge
                data-site-state
                :color="site.liveVersion !== null ? 'positive' : 'grey-6'"
                :label="site.liveVersion !== null ? `Live v${site.liveVersion}` : 'Not published'"
              />
            </div>

            <!-- THE TEAM NARROWS THE LIST TO IT, the way Active Team opens this dialog. Plain text
                 once the list is already that team's. -->
            <div class="os-tile-line">
              <a
                v-if="!only"
                href="#"
                class="site-team-link"
                data-site-team
                @click.prevent="only = site.team"
              >{{ teamName(site.team) }}</a>
              <span v-else data-site-team>{{ teamName(site.team) }}</span>
            </div>

            <div v-if="site.liveVersion !== null" class="os-tile-line os-text-muted" data-site-published>
              Published {{ when(site.publishedAt) }}<template v-if="site.publishedBy"> by {{ site.publishedBy }}</template>
            </div>

            <div class="os-tile-line os-text-muted" data-site-data>
              {{ documents(site.documents) }} · {{ size(site.dataBytes) }}
            </div>

            <!-- ACTIONS, icons with their words in a tooltip and an aria-label. The tooltip lives on
                 a wrapper because a disabled q-btn swallows pointer events, and the disabled ones
                 are exactly the ones whose reason a person wants to read. -->
            <div class="site-tile-actions">
              <span class="row-btn-wrap">
                <q-btn
                  flat
                  dense
                  round
                  icon="open_in_new"
                  :aria-label="`Open ${site.name}`"
                  :disable="site.liveVersion === null"
                  @click="openSite(site)"
                />
                <q-tooltip>{{ site.liveVersion === null ? 'Not published, so there is nothing to open' : 'Open the live site in a new tab' }}</q-tooltip>
              </span>
              <span class="row-btn-wrap">
                <q-btn
                  flat
                  dense
                  round
                  icon="history"
                  :aria-label="`Versions of ${site.name}`"
                  @click="showDetail(site, 'versions')"
                />
                <q-tooltip>The kept versions, and rolling back to one</q-tooltip>
              </span>
              <span class="row-btn-wrap">
                <q-btn
                  flat
                  dense
                  round
                  icon="dataset"
                  :aria-label="`Data of ${site.name}`"
                  @click="showDetail(site, 'data')"
                />
                <q-tooltip>The site's collections, read-only</q-tooltip>
              </span>
              <span class="row-btn-wrap">
                <q-btn
                  flat
                  dense
                  round
                  icon="unpublished"
                  :aria-label="`Unpublish ${site.name}`"
                  :disable="site.liveVersion === null || busy !== ''"
                  :loading="busy === key(site)"
                  @click="unpublish(site)"
                />
                <q-tooltip>{{ site.liveVersion === null ? 'Not published' : 'Take the site down. Its files and data are kept' }}</q-tooltip>
              </span>
              <span class="row-btn-wrap">
                <q-btn
                  flat
                  dense
                  round
                  color="negative"
                  icon="delete"
                  :aria-label="`Delete ${site.name}`"
                  :disable="busy !== ''"
                  @click="askDelete(site)"
                />
                <q-tooltip>Delete the site, its versions and its data</q-tooltip>
              </span>
            </div>
          </div>
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>

  <!-- DELETE: the Host's own sentence, from the first DELETE it refused, is the question. -->
  <q-dialog :model-value="confirming !== null" @update:model-value="(showing) => showing || (confirming = null)">
    <q-card v-if="confirming" class="os-dialog-sm" data-site-delete>
      <q-card-section>
        <div class="os-dialog-title">Delete {{ confirming.site.name }}?</div>
        <div class="os-body q-mt-sm" data-site-delete-sentence>{{ confirming.sentence }}</div>
        <div v-if="deleteProblem" class="os-body text-negative q-mt-sm">{{ deleteProblem }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" @click="confirming = null" />
        <q-btn
          unelevated
          no-caps
          color="negative"
          :label="`Delete ${confirming.site.name}`"
          :loading="deleting"
          @click="confirmDelete"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- VERSIONS AND DATA: one site's kept versions, or its collections read-only. -->
  <q-dialog v-model="detailOpen">
    <q-card v-if="detailSite" class="os-dialog-lg" data-site-detail>
      <q-card-section class="row items-center q-pb-none">
        <div>
          <div class="os-dialog-title">{{ detailView === 'versions' ? 'Versions' : 'Data' }}</div>
          <div class="text-caption os-text-muted mono">{{ key(detailSite) }}</div>
        </div>
        <q-space />
        <q-toggle v-if="detailView === 'data'" v-model="showJson" dense label="Show JSON" class="q-mr-sm" data-show-json />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section class="site-detail-body">
        <div v-if="detailProblem" class="os-body text-negative q-mb-sm" data-site-detail-problem>{{ detailProblem }}</div>

        <template v-if="detail && detailView === 'versions'">
          <div v-if="detail.versions.length === 0" class="os-body os-text-muted">No versions kept.</div>
          <!-- LAID OUT BY THE BROWSER EVERY TIME, not resizable: a pinned width pushed the actions past
               the dialog's edge, cut "Roll back to v1" short, and a click there scrolled the table
               sideways and the Version column out of sight. Long text wraps instead; the action
               column takes the width of its words. -->
          <q-markup-table v-else class="site-versions" flat bordered dense separator="horizontal" data-site-versions>
            <thead>
              <tr>
                <th class="text-left">Version</th>
                <th class="text-left">Published</th>
                <th class="text-left">Source</th>
                <th class="text-right">Files</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <tr v-for="version in detail.versions" :key="version.version" :data-site-version="version.version">
                <td class="text-left">
                  v{{ version.version }}
                  <q-badge v-if="version.live" color="positive" label="Live" class="q-ml-xs" data-site-version-live />
                </td>
                <td class="text-left">
                  {{ when(version.publishedAt) }}
                  <div class="text-caption os-text-muted">by {{ version.publishedBy }}</div>
                </td>
                <td class="text-left mono">{{ version.source }}</td>
                <td class="text-right">
                  {{ version.files }}
                  <div class="text-caption os-text-muted">{{ size(version.bytes) }}</div>
                </td>
                <td class="text-right site-version-action">
                  <q-btn
                    v-if="!version.live"
                    flat
                    dense
                    no-caps
                    :icon="versionAction(version.version).icon"
                    :label="versionAction(version.version).label"
                    :loading="rollingBack === version.version"
                    :disable="rollingBack !== null"
                    @click="rollback(version.version)"
                  />
                </td>
              </tr>
            </tbody>
          </q-markup-table>
        </template>

        <template v-else-if="detail">
          <div v-if="detail.collections.length === 0" class="os-body os-text-muted">No collections.</div>
          <div v-else class="row q-gutter-xs q-mb-sm">
            <q-btn
              v-for="name in detail.collections"
              :key="name"
              dense
              no-caps
              :flat="collection !== name"
              :unelevated="collection === name"
              :color="collection === name ? 'primary' : undefined"
              :label="name"
              @click="pickCollection(name)"
            />
          </div>

          <div v-if="collection && !docsLoading && docs.length === 0" class="os-body os-text-muted">
            No documents in {{ collection }}.
          </div>
          <div v-for="doc in docs" :key="doc.id" class="q-mb-md" :data-site-doc="doc.id">
            <div class="text-caption">
              <span class="mono">{{ doc.id }}</span>
              <span class="os-text-muted"> · {{ when(doc.updatedAt) }} by {{ doc.updatedBy }}</span>
            </div>
            <dl v-if="shownFields(doc.doc)" class="site-doc-fields">
              <div v-for="entry in shownFields(doc.doc)" :key="entry.field" class="site-doc-field" data-site-doc-field>
                <dt class="os-text-muted mono">{{ entry.field }}</dt>
                <dd>{{ entry.value }}</dd>
              </div>
            </dl>
            <pre v-else class="site-doc mono">{{ pretty(doc.doc) }}</pre>
          </div>
        </template>
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* The grid itself is `os-tiles` (css/tiles.scss); this only says how narrow a column may get. */
.site-tiles {
  --os-tile-min: 20rem;
}

.site-icon {
  color: var(--q-primary);
}

.site-team-link {
  color: inherit;
}

.site-tile-actions {
  margin-top: auto;
  padding-top: 6px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 2px;
}

/* The tooltip lives on this WRAPPER because a disabled q-btn swallows pointer events. */
.row-btn-wrap {
  display: inline-flex;
}

.site-detail-body {
  max-height: 70vh;
  overflow-y: auto;
}

/* The versions table fits the dialog and never scrolls sideways: long text wraps anywhere, the
   action column is as wide as its words and no wider. */
.site-versions {
  overflow-x: hidden;
}

.site-versions td {
  overflow-wrap: anywhere;
}

.site-version-action {
  width: 1%;
  white-space: nowrap;
}

.site-doc-fields {
  margin: 0;
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  column-gap: 12px;
  row-gap: 2px;
}

/* Each field's pair sits in the parent grid, so every name lines up in one column. */
.site-doc-field {
  display: contents;
}

.site-doc-fields dt,
.site-doc-fields dd {
  margin: 0;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}

.site-doc {
  margin: 0;
  max-height: 30vh;
  overflow: auto;
  font-size: 12px;
  white-space: pre;
}
</style>
