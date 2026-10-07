<script setup lang="ts">
import { ref, watch } from 'vue';
import HostPathPicker from './HostPathPicker.vue';

/**
 * INSTALL FROM A FOLDER: the one dialog Solutions and Admin > Plugins both open. A folder inside
 * the instance - typed, or chosen with Browse - and the screen's own options; the install itself is
 * the caller's (`install`), which is how each screen keeps what it does with the folder: Solutions
 * opens the wizard, Plugins installs a plugin and shows the Host's verdict in the default slot.
 *
 * Browse opens the picker in its `packages` mode: the teams' Documents, where a package is
 * uploaded, with nothing above it and no dot-entries, and Choose disabled until a folder below
 * Documents is open.
 *
 * `offerReplace` shows Replace, for a screen whose Host route takes it (a plugin version already
 * installed); a screen without one is not shown a box that would do nothing.
 *
 * THE UPLOAD PLACE (`data-upload-package`, slot `upload`) is where uploading a package (.zip) from
 * this computer goes. Until then it says how a package gets into Documents.
 */
const props = withDefaults(defineProps<{
  title?: string;
  caption?: string;
  pickerTitle?: string;
  offerReplace?: boolean;
  installing?: boolean;
}>(), {
  title: 'Install from a folder',
  caption: 'A package folder inside this instance.',
  pickerTitle: 'Choose the package folder',
  offerReplace: false,
  installing: false,
});

const open = defineModel<boolean>({ required: true });

const emit = defineEmits<{ install: [folder: string, replace: boolean] }>();

const folder = ref('');
const replace = ref(false);
const pickerOpen = ref(false);

// Every open starts empty: the last folder is the last install's, not this one's.
watch(open, (showing) => {
  if (!showing) return;
  folder.value = '';
  replace.value = false;
});

function install() {
  const path = folder.value.trim();
  if (!path || props.installing) return;
  emit('install', path, props.offerReplace && replace.value);
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-md" data-install-dialog>
      <q-card-section>
        <div class="os-dialog-title">{{ title }}</div>
        <div class="text-caption os-text-muted">{{ caption }}</div>
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-input
          v-model="folder"
          outlined
          dense
          label="Folder"
          spellcheck="false"
          autocomplete="off"
          @keyup.enter="install"
        >
          <template #after>
            <q-btn flat dense no-caps icon="folder_open" label="Browse…" @click="pickerOpen = true" />
          </template>
        </q-input>

        <!-- THE UPLOAD PLACE: uploading a package (.zip) from this computer belongs here. -->
        <div class="text-caption os-text-muted" data-upload-package>
          <slot name="upload">
            Not in this instance yet? Upload the package folder, or its .zip, into Documents first;
            Browse opens there.
          </slot>
        </div>

        <template v-if="offerReplace">
          <q-checkbox v-model="replace" dense label="Replace" />
          <div class="text-caption os-text-muted">
            Tick to install over a version that is already installed. Without it, the Host refuses.
          </div>
        </template>

        <slot />
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Close" :disable="installing" />
        <q-btn
          color="primary"
          no-caps
          label="Install"
          :loading="installing"
          :disable="installing || folder.trim() === ''"
          @click="install"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <HostPathPicker
    v-model="pickerOpen"
    packages
    :title="pickerTitle"
    @chose="folder = $event"
  />
</template>
