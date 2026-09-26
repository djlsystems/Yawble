<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useConsoleStore } from '../stores/console';
import * as api from '../api/documents';
import type { DocumentsFolder, DocumentsFolderKey } from '../api/types';
import { documentsBrowser } from '../lib/documentsSource';
import FileBrowser from './FileBrowser.vue';

/**
 * A small file manager over the TENANT documents root - one folder per team, and a folder outlives
 * the team that wrote it.
 *
 * THE FOLDER IS NAMED, NOT IMPLIED. There are many folders, the active team is not necessarily one
 * of them, and the most important folder on the list may belong to a team that no longer exists.
 * So the team is a CONTROL at the top of this dialog rather than a caption at the
 * bottom of it: it is what you came here to choose.
 *
 * One folder at a time with a breadcrumb, rather than a tree. A tree needs expansion state that
 * has to stay in step with what is on disk, and this is a place people visit to find one file.
 *
 * ---
 * **THE LISTING IS `FileBrowser`, AND THE FOLDER DIMENSION IS THIS FILE'S.**
 *
 * One component over two data sources: the LOOK and the NAVIGATION are shared with the Host picker
 * and the BOUNDARIES are not. Documents is still bounded by `TeamDocuments.Resolve` and reaches it
 * through `api/documents.ts` alone - see `lib/documentsSource.ts`.
 *
 * WHAT DELIBERATELY STAYS OUT OF THE SHARED COMPONENT: the team select and the banner below
 * it. `exists` and `retired` are TWO FACTS WITH DIFFERENT FIXES - a team was deleted, versus a
 * later team took its identifier and this folder was moved aside - and each carries its own
 * sentence. A shared browser that could render one "unavailable" state would erase that
 * distinction, so it renders none of them: below this chrome it is only ever looking at one
 * folder, and which folder that is, and why it might be a dead team's, is this dialog's business.
 * ---
 */
const open = defineModel<boolean>({ required: true });

const board = useConsoleStore();

/** Every documents folder that EXISTS, which is not the same list as the teams. */
const folders = ref<DocumentsFolder[]>([]);

/**
 * THE FOLDER KEY, NOT A TEAM ID - see `DocumentsFolder.folder`. Every route below is addressed
 * with this, and the two are equal for every folder anybody would open first, which is exactly why
 * it is typed apart from `TeamId`.
 */
const selected = ref<DocumentsFolderKey | null>(null);

const busy = ref(false);
const error = ref('');

const browser = ref<InstanceType<typeof FileBrowser> | null>(null);

/** The folder being shown, or null while there is none to show. Null is an ordinary state: an
 *  instance where nobody has written a document yet has no folders at all. */
const current = computed(() => folders.value.find((entry) => entry.folder === selected.value) ?? null);

/** WHAT EVERY ROUTE IS ADDRESSED WITH. Null rather than '' when there is no selection, so every
 *  call below has to narrow past it - which is the whole point of the key being its own type. */
const key = computed(() => current.value?.folder ?? null);

/**
 * THE CASE THIS DIALOG EXISTS FOR. A team is deleted the moment its work merges, which is exactly
 * when its reports become the only record of how that work was checked - so the folder is still
 * here and the team is not.
 */
const teamIsGone = computed(() => current.value !== null && !current.value.exists);

/**
 * WRITING INTO A DEAD TEAM'S FOLDER IS REFUSED HERE, and this is a choice rather than a
 * consequence. What is left behind is the RECORD of work nobody can do
 * again: there is no team to ask about it and nothing that would notice an edit. Reading it is the
 * whole point; adding to it is a way to make a record wrong by accident. DELETING it is not refused
 * - a person may clear a record out, and this flag is only about adding to it.
 *
 * Stated on the button as words, not left to a server refusal after the file has been picked.
 */
const readOnly = computed(() => key.value === null || teamIsGone.value);

/**
 * THE SOURCE, REMADE WHENEVER THE FOLDER CHANGES - which is how `FileBrowser` is told to start
 * over. A different folder is a different root, so the path within the old one means nothing.
 *
 * `null` while there is no folder is a REAL state and not a loading one: the browser fetches
 * nothing and renders this dialog's own empty words instead of a second list renderer.
 */
const browsing = computed(() =>
  key.value === null ? null : documentsBrowser(key.value, readOnly.value, ask));

/**
 * DELETE IS PASSED, OR IT IS NOT PASSED - there is no disabled state for it, which is why this is
 * `undefined` rather than a flag. The Host picker renders the same component and never passes one.
 * A dead team's folder IS given it: its documents can be deleted, not added to.
 */
const deletion = computed(() => browsing.value?.deletion);

/**
 * ONE QUESTION AT A TIME, asked here and answered by the person. `documentsBrowser` asks it before
 * a folder with things in it goes; "Delete all of these documents" asks it before a gone team's
 * whole folder goes. The promise is what lets the source wait for the answer without owning a
 * dialog.
 */
const question = ref<{ text: string; answer: (yes: boolean) => void } | null>(null);

function ask(text: string): Promise<boolean> {
  return new Promise((answer) => {
    question.value = { text, answer };
  });
}

function reply(yes: boolean) {
  const asked = question.value;
  question.value = null;
  asked?.answer(yes);
}

const asking = computed({
  get: () => question.value !== null,
  set: (showing: boolean) => {
    if (!showing) reply(false);
  },
});

/**
 * A GONE TEAM'S WHOLE FOLDER, deleted after the person is told how many files go. The
 * server removes it only when its marker says the platform made it, and never a live team's.
 * Afterwards the folder is gone from the picker, so the dialog moves to the one after it - the
 * next one down the list a person is working through, or the last if this was the last.
 */
async function deleteAll() {
  const gone = current.value;
  if (!gone || gone.exists) return;

  error.value = '';

  try {
    const files = (await api.listDocuments(gone.folder, '', true)).length;
    const yes = await ask(
      `Delete all ${files} documents of ${gone.label}? This cannot be undone.`,
    );
    if (!yes) return;

    const at = folders.value.findIndex((entry) => entry.folder === gone.folder);

    await api.deleteDocument(gone.folder, '', true);
    await loadFolders();

    const remaining = folders.value.filter((entry) => entry.folder !== gone.folder);
    selected.value = remaining[Math.min(at, remaining.length - 1)]?.folder ?? null;
  } catch (failure) {
    error.value = (failure as Error).message;
  }
}

/**
 * Which folder to open on: the active team's if it has one, else whatever exists.
 *
 * THE RIBBON DOES NOT NAME ONE. It is a plain button, so this is the whole of the decision rather
 * than a fallback behind one.
 *
 * MATCHED ON `folder`, NEVER ON `team`, AND THAT IS THE WHOLE OF THE CARE HERE. Both fields hold
 * the team's identifier for a live folder, so the two spellings agree everywhere except the one
 * place it matters. A RETIRED folder keeps `team` and has its `folder` moved aside - that is what
 * retirement IS - so `entry.team === activeTeamId` matches the PREDECESSOR of the team that is
 * active right now, and matches it first if it sorts first. The dialog would open the active team
 * onto a dead team's files: a 200, somebody else's documents, and a banner saying the team is gone
 * while the ribbon says it is active.
 *
 * An `exists` clause here would be redundant rather than defensive - a folder keyed by a live
 * team's id IS that team's live folder, because creating the team is what retires any older one -
 * and a guard that cannot fail is a question every later reader has to re-answer.
 *
 * Checked against the FOLDER list rather than assumed, because a team can be active and have
 * written nothing.
 */
function chooseFolder(): DocumentsFolderKey | null {
  const live = folders.value.find((entry) => entry.folder === board.activeTeamId);

  return live?.folder ?? folders.value[0]?.folder ?? null;
}

async function loadFolders() {
  busy.value = true;
  error.value = '';

  try {
    folders.value = await api.listDocumentFolders();
  } catch (failure) {
    error.value = (failure as Error).message;
    folders.value = [];
  } finally {
    busy.value = false;
  }
}

/** The whole dialog, from nothing: which folders exist, then which one we are in. */
async function reload() {
  await loadFolders();

  // Re-chosen rather than kept, because the list it was chosen from has just been re-read - a team
  // deleted while this dialog sat open leaves its FOLDER, so the selection usually survives, but a
  // folder that has genuinely gone must not leave the dialog pointing at nothing.
  if (!selected.value || !folders.value.some((entry) => entry.folder === selected.value)) {
    selected.value = chooseFolder();
  }

  // The SAME folder re-read. A CHANGED one remakes the source above, which re-lists on its own -
  // this is the other case, and the only one the browser cannot see for itself.
  browser.value?.reload();
}

watch(open, (showing) => {
  if (!showing) return;

  selected.value = null;
  void reload();
});

/** A different folder is a different root. Setting the key is the whole of it: the source is
 *  derived from it, and replacing the source is what resets the browser. */
function selectFolder(id: DocumentsFolderKey) {
  selected.value = id;
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="documents-card os-dialog-md">
      <q-card-section class="q-pb-none row items-center">
        <div class="col">
          <div class="os-dialog-title">Documents</div>
          <!-- WHICH TEAM'S, AS A CONTROL RATHER THAN A CAPTION. This was the active team's name and
               nothing else, which said what you were looking at but not that there was anything
               else to look at. -->
          <q-select
            v-if="folders.length > 0"
            dense
            options-dense
            borderless
            emit-value
            map-options
            :model-value="selected"
            :options="folders"
            option-value="folder"
            option-label="label"
            class="documents-team"
            aria-label="Whose documents"
            @update:model-value="selectFolder"
          >
            <!-- VALUED BY `folder` AND LABELLED BY `label` - render the name, address with the id,
                 which here is a third string again: a retired folder's key is neither its team's
                 identifier nor its label. -->
            <template #option="scope">
              <q-item v-bind="scope.itemProps">
                <q-item-section avatar>
                  <q-icon :name="scope.opt.exists ? 'folder_shared' : 'folder_off'" />
                </q-item-section>
                <q-item-section>
                  <q-item-label>{{ scope.opt.label }}</q-item-label>
                  <!-- TWO DIFFERENT SENTENCES, because they are two different facts and the fix
                       for each is different. A team that was deleted leaves its documents; a
                       RETIRED folder was moved aside because a later team took the identifier, so
                       a team of that name is on screen right now and is not this one. -->
                  <q-item-label v-if="scope.opt.retired" caption>
                    Superseded by a later team of this name
                  </q-item-label>
                  <q-item-label v-else-if="!scope.opt.exists" caption>
                    Team deleted, documents kept
                  </q-item-label>
                </q-item-section>
              </q-item>
            </template>
          </q-select>
          <div v-else class="os-body os-text-muted">No team documents yet</div>
        </div>
        <q-space />
        <q-btn flat dense round icon="refresh" aria-label="Refresh" :disable="busy" @click="reload" />
      </q-card-section>

      <!-- SAID WHERE IT BITES. The team is gone and these files are not, which is the whole point
           of keeping them - but a reader who does not know that reads a folder named for a team they
           cannot find and assumes the screen is broken.

           KEPT HERE RATHER THAN PUSHED INTO `FileBrowser`: the shared browser can render exactly one kind of nothing, and these
           are two facts with two fixes. -->
      <q-card-section v-if="teamIsGone" class="q-py-sm">
        <q-banner dense class="documents-gone">
          <template #avatar>
            <q-icon name="folder_off" />
          </template>
          <span v-if="current?.retired">
            A later team took this name. These documents belong to the earlier one and were kept.
            You can read or delete them here.
          </span>
          <span v-else>This team no longer exists. Its documents were kept. You can read or delete them here.</span>
          <template #action>
            <q-btn flat dense no-caps color="negative" icon="delete" label="Delete all of these documents"
              @click="deleteAll" />
          </template>
        </q-banner>
      </q-card-section>

      <q-card-section v-if="error" class="q-py-sm text-negative">{{ error }}</q-card-section>

      <!-- THE ONE FILE BROWSER. `deletion` is passed or it is not; there is no delete flag. -->
      <FileBrowser ref="browser" :source="browsing?.source ?? null" :deletion="deletion">
        <template #empty>
          <template v-if="folders.length === 0">
            Nothing has been written yet. A team's documents appear here once it writes one.
          </template>
          <template v-else-if="readOnly">Nothing here.</template>
          <template v-else>Nothing here yet. Upload a document, or create a folder.</template>
        </template>

        <!-- THE COST OF THE DECISION, SAID RATHER THAN BURIED. A team placed on a particular volume
             by `teams.root` keeps its work there; its DOCUMENTS are the exception, and this is the
             screen where somebody goes looking for them. -->
        <template #note>Documents live in one place for the whole tenant.</template>

        <template #actions>
          <q-btn flat no-caps label="Close" @click="open = false" />
        </template>
      </FileBrowser>
    </q-card>

    <!-- The one question the delete paths ask; see `ask`. -->
    <q-dialog v-model="asking">
      <q-card class="os-dialog-sm">
        <q-card-section>{{ question?.text }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="reply(false)" />
          <q-btn flat no-caps color="negative" label="Delete" @click="reply(true)" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-dialog>
</template>

<style scoped>
/* Sized and coloured as a caption, so the control does not read as a form having appeared. It is still the answer to "whose documents"; it is
   only also the way to change it. */
.documents-team {
  font-size: 12px;
  margin-left: -4px;
}

/* The banner is a statement of fact rather than a warning: nothing has gone wrong, and painting it
   as an error would teach people that a kept document is a problem. */
.documents-gone {
  background: var(--os-chrome);
  color: var(--os-ink-muted);
  border-radius: 3px;
}
</style>
