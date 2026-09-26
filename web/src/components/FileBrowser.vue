<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import type { FileBrowserDeletion, FileBrowserEntry, FileBrowserListing, FileBrowserSource }
  from '../lib/fileBrowser';

/**
 * THE ONE FILE BROWSER. Listing, breadcrumb, new folder, upload, and delete as an OPTIONAL
 * capability, over whatever a `FileBrowserSource` hands back.
 *
 * Its two users answer different questions: Documents is a view of a PRODUCT CONCEPT - a team's
 * shared folder, which outlives the team - and the Host picker is a view of the MACHINE. One
 * component, two data sources: **the look and the navigation are shared, the BOUNDARIES are not.** Documents is still
 * bounded by `TeamDocuments.Resolve`, the Host picker still by `FileBrowserPolicy` and its
 * `FileBrowser:Roots` allowlist, and sharing a renderer moves neither.
 *
 * SO NOTHING HERE TAKES A PATH APART. `path` is an opaque string: Documents paths are `/`-joined
 * and relative to a folder, Host paths are absolute and may use either separator, and a renderer
 * that split them would be a THIRD thing resolving paths - which is the boundary this design
 * refuses to move. The source hands back crumbs already made and this renders them.
 *
 * WHAT IS NOT HERE, DELIBERATELY: the `exists`/`retired` distinction. Those are two facts with
 * different fixes and they carry two different sentences; they belong
 * to the Documents dialog's own chrome, ABOVE this component, because this one is only ever looking
 * at one folder. A shared browser able to render a single "unavailable" state would take that
 * away.
 */

const props = defineProps<{
  /** `null` is a REAL state, not a loading one: an instance where nobody has written a document has
   *  no folder to browse. Nothing is fetched and the `empty` slot renders. */
  source: FileBrowserSource | null;

  /**
   * DELETE IS A CAPABILITY, NOT A FLAG IN THE TEMPLATE. A caller that does not pass this has no
   * delete control at all - absent because it was never passed, rather than present and disabled.
   * The Host picker does not pass it and cannot: `/api/fs` has four routes and no delete among
   * them, and adding a fifth is deliberately not done.
   *
   * `| undefined` IS SPELLED OUT because this project runs `exactOptionalPropertyTypes`, under
   * which a bare `?` means "may be omitted" and NOT "may be passed as undefined". Withholding the
   * capability by binding an expression that evaluates to `undefined` - which is exactly how the
   * Documents dialog withholds it from a dead team's folder - is a type error without this.
   */
  deletion?: FileBrowserDeletion | undefined;
}>();

const emit = defineEmits<{ navigated: [string] }>();

const $q = useQuasar();

const listing = ref<FileBrowserListing | null>(null);

/** A NAVIGATION failure. It clears the listing and takes over the panel, because the folder that
 *  was being looked at no longer answers. */
const browseError = ref<string | null>(null);

/** Separate from `browseError` on purpose: New folder, Upload and Delete fail WITHOUT invalidating
 *  the folder a person is already looking at, so the listing stays on screen and this renders
 *  inline beneath it. */
const actionError = ref<string | null>(null);

const busy = ref(false);
const uploadBusy = ref(false);
const creatingBusy = ref(false);

const creating = ref(false);
const newFolderName = ref('');
const uploader = ref<HTMLInputElement | null>(null);

/** The source's own preview of the server's name rule, when it has one. The server re-checks
 *  everything regardless of what this says. */
const folderNameError = computed(() => {
  const trimmed = newFolderName.value.trim();
  if (trimmed === '' || !props.source?.checkName) return null;

  return props.source.checkName(trimmed);
});

const entries = computed(() => listing.value?.entries ?? []);
const crumbs = computed(() => listing.value?.crumbs ?? []);
const writes = computed(() => listing.value?.writes ?? 'absent');
const path = computed(() => listing.value?.path ?? null);

/**
 * Bumped on every call and compared after the fetch resolves, so a RESPONSE that arrives out of
 * order is discarded rather than believed. Two quick crumb clicks (or a row click racing a crumb
 * click) issue two overlapping requests, and without this the later-*arriving* one wins regardless
 * of which was asked for LAST - the listing, and everything derived from it, would silently
 * describe the wrong folder.
 *
 * A generation counter rather than an early `if (busy.value) return`: the early return drops a
 * click the person actually made, which is its own small wrongness - this lets every request fire
 * and simply ignores whichever ones are no longer the latest.
 */
let generation = 0;

async function browseTo(target: string) {
  const source = props.source;
  if (!source) return;

  const mine = ++generation;

  busy.value = true;
  browseError.value = null;
  actionError.value = null;

  try {
    const result = await source.list(target);
    if (mine !== generation) return; // superseded by a later navigation

    listing.value = result;
    emit('navigated', result.path);

    // Retargeting the New Folder form happens at COMPLETION, not at the call that started this
    // navigation: a form already open BEFORE the navigation began stays open through the whole
    // round trip, and clearing at call time would do nothing to protect that case. The generation
    // guard above is what stops a SUPERSEDED response closing a form opened after this call.
    creating.value = false;
    newFolderName.value = '';
  } catch (cause) {
    if (mine !== generation) return;

    listing.value = null;
    browseError.value = cause instanceof Error ? cause.message : String(cause);
    creating.value = false;
    newFolderName.value = '';
  } finally {
    // Only the LATEST request may clear the busy flag - an abandoned one finishing after a newer
    // navigation started must not flip it false out from under the request that superseded it.
    if (mine === generation) busy.value = false;
  }
}

/**
 * REPLACING THE SOURCE IS HOW A CALLER CHANGES ROOT, and that is the whole of the reset protocol:
 * a different folder is a different root, so the path within the old one means nothing here.
 * `immediate` because a source present at first render must be listed - this component is not a
 * dialog and has no open/close edge of its own to hang that on.
 */
watch(
  () => props.source,
  (source) => {
    listing.value = null;
    browseError.value = null;
    actionError.value = null;
    creating.value = false;
    newFolderName.value = '';

    // Abandons any navigation still in flight against the PREVIOUS source, whose response would
    // otherwise land as this one's listing.
    generation += 1;

    if (!source) {
      busy.value = false;
      return;
    }

    void browseTo(source.start);
  },
  { immediate: true },
);

function openEntry(entry: FileBrowserEntry) {
  if (entry.disabled) return;
  if (entry.isFolder) {
    void browseTo(entry.path);
    return;
  }

  // A NEW TAB, and `noopener` so the document's page gets no handle back on this one. The
  // server sandboxes what it shows; this keeps the opener out of its reach as well.
  if (entry.viewUrl) {
    window.open(entry.viewUrl, '_blank', 'noopener');
    return;
  }

  if (entry.downloadUrl) download(entry.downloadUrl);
}

/** The same thing the download icon does - `content` answers an attachment - without a tab. */
function download(url: string) {
  const link = document.createElement('a');
  link.href = url;
  link.download = '';
  link.rel = 'noopener';
  link.click();
}

/** What a click on a FILE row does, said on the row. Null for a row a click does not act on. */
function rowAction(entry: FileBrowserEntry): string | null {
  if (entry.isFolder || entry.disabled) return null;
  if (entry.viewUrl) return 'Open';
  return entry.downloadUrl ? 'Download' : null;
}

function goUp() {
  const up = listing.value?.up;
  if (up) void browseTo(up);
}

async function submitNewFolder() {
  const name = newFolderName.value.trim();
  const here = listing.value;
  if (!name || !here || !props.source || folderNameError.value) return;

  creatingBusy.value = true;
  actionError.value = null;

  try {
    await props.source.createFolder(here.path, name);
    creating.value = false;
    newFolderName.value = '';
    await browseTo(here.path);
  } catch (cause) {
    actionError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    creatingBusy.value = false;
  }
}

function cancelNewFolder() {
  creating.value = false;
  newFolderName.value = '';
}

async function upload(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];

  // Cleared straight away so choosing the SAME file twice still fires a change event - otherwise a
  // failed upload cannot be retried without picking something else first.
  input.value = '';

  const here = listing.value;
  if (!file || !here || !props.source) return;

  uploadBusy.value = true;
  actionError.value = null;

  try {
    await props.source.upload(here.path, file);
    $q.notify({ type: 'positive', message: `${file.name} uploaded.` });
    await browseTo(here.path);
  } catch (cause) {
    actionError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    uploadBusy.value = false;
  }
}

async function remove(entry: FileBrowserEntry) {
  const here = listing.value;
  if (!props.deletion || !here) return;

  actionError.value = null;

  try {
    await props.deletion.remove(entry);
    await browseTo(here.path);
  } catch (cause) {
    // A folder with something in it is refused by the server, and the message says so. Shown rather
    // than pre-empted, because the count the UI holds is a moment old and the disk is not.
    actionError.value = cause instanceof Error ? cause.message : String(cause);
  }
}

/** Hoisted out of the template because `deletion.hint` is optional on an optional prop, and a
 *  template cannot narrow the same expression twice - `v-if` and the interpolation are separate. */
function deleteHint(entry: FileBrowserEntry): string | null {
  return props.deletion?.hint?.(entry) ?? null;
}

/** Re-read the SAME location, for a caller's own Refresh button. Replacing the source reloads on
 *  its own; this is the other case. */
function reload() {
  if (listing.value) void browseTo(listing.value.path);
  else if (props.source) void browseTo(props.source.start);
}

defineExpose({ reload });
</script>

<template>
  <q-card-section v-if="crumbs.length > 0" class="q-py-sm">
    <div class="row items-center q-gutter-xs text-caption">
      <template v-for="(crumb, index) in crumbs" :key="crumb.path">
        <q-icon v-if="index > 0" name="chevron_right" size="14px" class="os-text-muted" />
        <q-btn flat dense no-caps size="sm" :label="crumb.label" @click="browseTo(crumb.path)" />
      </template>
    </div>
  </q-card-section>

  <q-separator />

  <q-card-section class="file-browser-body q-pa-none">
    <q-inner-loading :showing="busy" />

    <div v-if="browseError" class="text-negative q-pa-md">{{ browseError }}</div>

    <!-- THE ONE ROW TEMPLATE. Everything that told the two browsers apart is DATA on the entry -
         a caption, a download link, a disabled row - rather than a mode this branches on. -->
    <q-list v-else-if="entries.length > 0" separator>
      <q-item v-if="listing?.up" clickable @click="goUp">
        <q-item-section avatar>
          <q-icon name="arrow_upward" />
        </q-item-section>
        <q-item-section>..</q-item-section>
      </q-item>

      <q-item v-for="entry in entries" :key="entry.path" :disable="entry.disabled === true">
        <q-item-section avatar>
          <q-icon :name="entry.isFolder ? 'folder' : 'description'" />
        </q-item-section>

        <q-item-section>
          <q-item-label
            :class="(entry.isFolder && !entry.disabled) || rowAction(entry) ? 'cursor-pointer' : ''"
            :data-row-action="rowAction(entry) ?? undefined"
            @click="openEntry(entry)"
          >
            {{ entry.name }}
            <q-tooltip v-if="rowAction(entry)">{{ rowAction(entry) }}</q-tooltip>
          </q-item-label>
          <q-item-label v-if="entry.caption" caption>{{ entry.caption }}</q-item-label>
        </q-item-section>

        <q-item-section side>
          <div class="row items-center no-wrap">
            <!-- READING IS NEVER WITHHELD, including from a dead team's folder: that is what the
                 folder outliving the team is FOR. A source with no download route supplies no URL
                 and this simply does not render. -->
            <q-btn
              v-if="entry.downloadUrl"
              flat
              dense
              round
              icon="download"
              :href="entry.downloadUrl"
              target="_blank"
              aria-label="Download"
            />
            <!-- DELETE IS ABSENT WHEN THE CAPABILITY WAS NOT PASSED. Not disabled: a row-level
                 control that is present and dead reads as a bug on every row at once. -->
            <q-btn
              v-if="deletion"
              flat
              dense
              round
              icon="delete"
              :aria-label="deletion.label(entry)"
              @click="remove(entry)"
            >
              <q-tooltip v-if="deleteHint(entry)">{{ deleteHint(entry) }}</q-tooltip>
            </q-btn>
          </div>
        </q-item-section>
      </q-item>

      <!-- SAID OUT LOUD. A list that just stops at a cap reads as "this folder is small". -->
      <q-item v-if="listing?.note">
        <q-item-section class="os-text-muted">{{ listing.note }}</q-item-section>
      </q-item>
    </q-list>

    <!-- THE ONE EMPTY STATE. `source` being null lands here too, which is what lets a caller with
         nothing to browse say so in its own words instead of keeping a second renderer. -->
    <div v-else-if="!busy" class="os-text-muted q-pa-lg text-center">
      <slot name="empty">Nothing here.</slot>
    </div>
  </q-card-section>

  <q-separator />

  <q-card-section v-if="actionError" class="q-py-sm text-negative">{{ actionError }}</q-card-section>

  <q-card-section v-if="creating" class="q-py-sm row items-center q-gutter-sm">
    <q-input
      v-model="newFolderName"
      dense
      outlined
      autofocus
      hide-bottom-space
      label="Folder name"
      class="col"
      :error="folderNameError !== null"
      :error-message="folderNameError ?? undefined"
      @keyup.enter="submitNewFolder"
    />
    <q-btn flat no-caps label="Cancel" @click="cancelNewFolder" />
    <q-btn
      unelevated
      no-caps
      color="primary"
      label="Create"
      :loading="creatingBusy"
      :disable="newFolderName.trim() === '' || folderNameError !== null"
      @click="submitNewFolder"
    />
  </q-card-section>

  <q-card-actions align="right">
    <div class="file-browser-note col"><slot name="note" /></div>

    <!-- `busy` on both: the inner loading overlay only covers the body above, not this row, so
         without it a click landing between a navigation firing and its response arriving acts on
         the STALE listing - Upload would write into the folder just navigated away from. -->
    <template v-if="writes !== 'absent'">
      <q-btn
        flat
        no-caps
        icon="create_new_folder"
        label="New folder"
        :disable="writes === 'refused' || creating || busy"
        @click="creating = true"
      />
      <q-btn
        flat
        no-caps
        icon="upload_file"
        label="Upload"
        :disable="writes === 'refused' || uploadBusy || busy || !listing"
        :loading="uploadBusy"
        @click="uploader?.click()"
      />
    </template>

    <slot name="actions" :path="path" :busy="busy" />
  </q-card-actions>

  <!-- A real file input, hidden and driven by the button above: the browser will not open a picker
       for a click it did not consider a user gesture, and this keeps that gesture. -->
  <input ref="uploader" type="file" class="hidden" @change="upload" />
</template>

<style scoped>
.file-browser-body {
  position: relative;
  min-height: 12rem;
  max-height: 45vh;
  overflow-y: auto;
}

.file-browser-note {
  font-size: 11px;
  color: var(--os-ink-faint);
  padding-left: 8px;
}
</style>
