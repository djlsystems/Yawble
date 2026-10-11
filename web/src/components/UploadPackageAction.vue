<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { listDocumentsRoot, uploadDocumentZip } from '../api/documents';
import type { DocumentsClash, DocumentsFolder, DocumentsFolderKey, OnClash } from '../api/types';
import { agentPath, clashQueue, type ClashAnswer } from '../lib/documentsExplorer';
import { useConsoleStore } from '../stores/console';
import DocumentsClashDialog from './documents/DocumentsClashDialog.vue';

/**
 * UPLOAD A PACKAGE (.zip), from this computer, inside the install dialog. The zip goes to the top
 * of a team's folder in Documents - the active team's unless the person picks another, and the
 * line under the picker says exactly where - through the Documents zip route, which unpacks it
 * into a folder of its name and refuses an unsafe zip whole. The folder it made is handed back as
 * the absolute path the install routes take (`<documents root>/<folder>/<path>`).
 *
 * A clash is the Documents one: the same question, the same choices. Skip keeps the folder already
 * there and hands that back, so the person can still install it.
 */
const emit = defineEmits<{ uploaded: [folder: string] }>();

const consoleStore = useConsoleStore();

const folders = ref<DocumentsFolder[]>([]);
const root = ref('');
const into = ref<DocumentsFolderKey | null>(null);
const busy = ref(false);
const error = ref('');
const landed = ref('');
const input = ref<HTMLInputElement | null>(null);
const clashAsked = ref<{ clash: DocumentsClash; answer: (answer: ClashAnswer | null) => void } | null>(null);

/** Only a live team's folder takes an upload. */
const options = computed(() =>
  folders.value
    .filter((folder) => folder.exists && !folder.retired)
    .map((folder) => ({ value: folder.folder, label: folder.label })),
);
const intoLabel = computed(() => options.value.find((option) => option.value === into.value)?.label ?? '');

onMounted(async () => {
  try {
    const answer = await listDocumentsRoot();
    folders.value = answer.folders;
    root.value = answer.root;
    const live = options.value;
    into.value = (live.find((option) => option.value === consoleStore.activeTeamId) ?? live[0])?.value ?? null;
  } catch (failure) {
    error.value = (failure as Error).message;
  }
});

function ask(clash: DocumentsClash): Promise<ClashAnswer | null> {
  return new Promise((answer) => {
    clashAsked.value = { clash, answer };
  });
}

function clashAnswered(answer: ClashAnswer | null) {
  const asked = clashAsked.value;
  clashAsked.value = null;
  asked?.answer(answer);
}

async function onChosen(event: Event) {
  const chosen = event.target as HTMLInputElement;
  const file = chosen.files?.[0];
  chosen.value = '';

  const folder = into.value;
  if (!file || !folder) return;

  busy.value = true;
  error.value = '';
  landed.value = '';

  try {
    let onClash: 'ask' | OnClash = 'ask';

    for (;;) {
      const answer = await uploadDocumentZip(folder, file, '', onClash);

      if (answer.kind === 'clash') {
        const clash = answer.clashes[0] ?? { from: file.name, to: file.name, isFolder: true };
        const choice = (await clashQueue([clash], ask))?.get(clash.from);
        if (!choice) return;
        onClash = choice;
        continue;
      }

      const path = answer.kind === 'saved' ? answer.saved.path : answer.path;
      landed.value = `${answer.kind === 'saved' ? 'Unpacked into' : 'Kept the folder already there:'} Documents › ${intoLabel.value} › ${path.replaceAll('/', ' › ')}`;
      emit('uploaded', agentPath(root.value, folder, path));
      return;
    }
  } catch (failure) {
    error.value = (failure as Error).message;
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="column q-gutter-xs" data-upload-package-action>
    <div class="row items-center q-gutter-sm no-wrap">
      <q-btn
        outline
        dense
        no-caps
        icon="folder_zip"
        label="Upload a package (.zip)"
        :loading="busy"
        :disable="busy || !into"
        data-upload-package-button
        @click="input?.click()"
      />
      <q-select
        v-model="into"
        dense
        outlined
        emit-value
        map-options
        options-dense
        class="col"
        label="Into Documents of"
        :options="options"
        :disable="busy"
        data-upload-package-into
      />
    </div>
    <div v-if="landed" class="text-positive" data-upload-package-landed>{{ landed }}</div>
    <div v-else-if="into">
      The .zip is unpacked into a folder of its name at the top of Documents › {{ intoLabel }}.
    </div>
    <div v-else>Not in this instance yet? Upload the package into a team's Documents first; Browse opens there.</div>
    <div v-if="error" class="text-negative" role="alert" data-upload-package-error>{{ error }}</div>
    <input ref="input" type="file" accept=".zip,application/zip" class="hidden" data-upload-package-input @change="onChosen" />

    <DocumentsClashDialog
      :clash="clashAsked?.clash ?? null"
      :remaining="0"
      :folder="`Documents › ${intoLabel}`"
      @answer="clashAnswered"
    />
  </div>
</template>
