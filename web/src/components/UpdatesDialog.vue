<script setup lang="ts">
import { computed, ref } from 'vue';
import { copyToClipboard, useQuasar } from 'quasar';
import { checkForUpdates } from '../api/client';
import { useWipStore } from '../stores/wip';
import {
  checkedLine,
  releaseDay,
  runningWarning,
  updateCommand,
  updateHeadline,
  type UpdateStatus,
} from '../lib/releaseUpdates';

/**
 * UPDATES: whether a newer release is out, what changed in each one, and the one command that
 * updates. Opened from the version in the top bar. The release notes are the release's own text,
 * shown as text (never `v-html`). The update is the operator CLI's, run on the machine: nothing here
 * updates anything, and "not checked" is said as such, never as up to date.
 */
const props = defineProps<{ status: UpdateStatus | null }>();
const emit = defineEmits<{ checked: [status: UpdateStatus] }>();
const open = defineModel<boolean>({ required: true });

const $q = useQuasar();
const wip = useWipStore();

const checking = ref(false);
const checkError = ref('');

const status = computed(() => props.status);
const headline = computed(() => (status.value ? updateHeadline(status.value) : 'Not checked yet.'));
const command = computed(() => (status.value ? updateCommand(status.value) : ''));
const running = computed(() => runningWarning(wip.runningCount));

async function checkNow() {
  checking.value = true;
  checkError.value = '';
  try {
    emit('checked', await checkForUpdates());
  } catch (cause) {
    checkError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    checking.value = false;
  }
}

async function copyCommand() {
  await copyToClipboard(command.value);
  $q.notify({ type: 'positive', message: 'Command copied.', timeout: 1500 });
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-md" data-updates-dialog>
      <q-card-section>
        <div class="os-dialog-title">{{ status?.updateAvailable ? 'Update available' : 'Updates' }}</div>
        <div class="os-body q-mt-xs" data-updates-headline>{{ headline }}</div>
        <div v-if="status && checkedLine(status)" class="text-caption os-text-muted q-mt-xs" data-updates-checked>
          {{ checkedLine(status) }}
        </div>
      </q-card-section>

      <template v-if="status?.updateAvailable">
        <q-card-section class="q-pt-none">
          <div class="text-subtitle2">How to update</div>
          <div class="os-body q-mt-xs">On the computer that runs it, open a terminal and run:</div>
          <div class="row items-center no-wrap q-mt-sm update-command">
            <code class="col mono" data-updates-command>{{ command }}</code>
            <q-btn flat dense no-caps icon="content_copy" label="Copy" data-updates-copy @click="copyCommand" />
          </div>
          <div class="text-caption os-text-muted q-mt-sm">
            It updates the command-line tool and the instance together and keeps your data; this page
            reconnects when the instance is back.
          </div>
          <div v-if="running" class="os-body text-warning q-mt-sm" data-updates-running>{{ running }}</div>
        </q-card-section>

        <q-card-section class="q-pt-none">
          <div class="text-subtitle2">What's new</div>
          <div class="release-notes q-mt-xs">
            <section v-for="release in status.newer" :key="release.version" class="q-mb-md" data-updates-release>
              <div class="row items-baseline q-gutter-x-sm">
                <span class="text-weight-medium mono">v{{ release.version }}</span>
                <span v-if="release.prerelease" class="text-caption os-text-muted">pre-release</span>
                <span class="text-caption os-text-muted">{{ releaseDay(release.publishedAt) }}</span>
                <a v-if="release.url" :href="release.url" target="_blank" rel="noopener noreferrer" class="text-caption">Release page</a>
              </div>
              <div v-if="release.notes.trim()" class="notes os-body q-mt-xs" data-updates-notes>{{ release.notes }}</div>
              <div v-else class="text-caption os-text-muted q-mt-xs">No notes were published with this release.</div>
            </section>
          </div>
        </q-card-section>
      </template>

      <q-card-section v-if="checkError" class="q-pt-none">
        <div class="os-body text-negative">Could not check: {{ checkError }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn
          v-if="status?.enabled !== false"
          flat
          no-caps
          label="Check now"
          :loading="checking"
          data-updates-check
          @click="checkNow"
        />
        <q-btn v-close-popup flat label="Close" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.update-command {
  border: 1px solid var(--os-rule);
  border-radius: 6px;
  padding: 4px 4px 4px 12px;
}

/* The release's own text, kept as written: its line breaks and lists read as the author laid them
   out, and a long history scrolls inside the dialog rather than stretching it. */
.release-notes {
  max-height: 18rem;
  overflow-y: auto;
}

.notes {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
