<script setup lang="ts">
import { CodeBrowser } from '@yawble/code-browser';
import type { YawbleApi } from '../../../shared/api';
import { resolveApi } from '../api';
import { renderMarkdown } from '../markdown';
import { closeCode, codeTarget } from '../codeDialog';
import { displayName } from '../faces/faces';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// The Code dialog: the read-only browser of a member's clone, from the code-browser package. Mounted
// once; codeTarget says whose code it shows, and a new target replaces the dialog (its key), so only one is ever
// open. Escape and the backdrop close it (Modal). Markdown renders with the app's markdown-it (html disabled).
const props = defineProps<{ api?: YawbleApi }>();
const api = resolveApi(props.api);
</script>

<template>
  <Modal v-if="codeTarget" :key="`${codeTarget.team}/${codeTarget.member}`" :title="`${displayName(codeTarget.member)} · code`" wide @close="closeCode">
    <div data-test="code-dialog" :data-member="codeTarget.member">
      <CodeBrowser :api="api" :team="codeTarget.team" :member="codeTarget.member" :render-markdown="renderMarkdown" :loading-note="LoadingNote" :busy-label="BusyLabel" />
    </div>
  </Modal>
</template>
