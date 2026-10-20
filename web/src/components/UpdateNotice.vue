<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { readUpdateStatus } from '../api/client';
import type { UpdateStatus } from '../lib/releaseUpdates';
import UpdatesDialog from './UpdatesDialog.vue';

/**
 * "Update available" beside the version in the top bar, shown only when the Host's release check
 * answered and listed a newer release; it opens the Updates dialog (what changed, the command that
 * updates). It reads the Host's kept answer when the page loads and once an hour after - never the
 * release list itself, which the Host reads about twice a day. A read that fails shows nothing:
 * not knowing is never an alert.
 */
const RereadEvery = 60 * 60 * 1000;

/**
 * `word` false is the header row's first give-way step (`lib/headerFit.ts`): the icon alone on the
 * row, the words its tooltip and aria-label, and it still opens the Updates dialog.
 */
withDefaults(defineProps<{ word?: boolean }>(), { word: true });

/** Whether the notice is on the row, so the phone row can make room for it (`lib/headerFit.ts`). */
const emit = defineEmits<{ pending: [pending: boolean] }>();

/** UNTIL THE HOST HAS AN ANSWER, asked again this often. A page opened right after the Host
 *  starts reads "not checked yet" (its first read of the release list takes a few seconds), and
 *  waiting an hour after that would hide a newer release for an hour. */
const RereadUntilCheckedEvery = 3 * 60 * 1000;

const status = ref<UpdateStatus | null>(null);
const open = ref(false);
let timer: ReturnType<typeof setInterval> | undefined;
let soon: ReturnType<typeof setTimeout> | undefined;

async function read() {
  clearTimeout(soon);
  try {
    status.value = await readUpdateStatus();
  } catch {
    // Not known is not an alert; the dialog says why when it is opened from a later answer.
  }
  if (status.value?.enabled !== false && status.value?.checked !== true) {
    soon = setTimeout(() => void read(), RereadUntilCheckedEvery);
  }
}

watch(() => status.value?.updateAvailable === true, (pending) => emit('pending', pending));

onMounted(() => {
  void read();
  timer = setInterval(() => void read(), RereadEvery);
});

onBeforeUnmount(() => {
  clearInterval(timer);
  clearTimeout(soon);
});
</script>

<template>
  <span class="update-notice">
    <button
      v-if="status?.updateAvailable"
      type="button"
      class="update-link"
      :class="{ 'update-link--icon': !word }"
      :aria-label="`Update available: v${status.latest}. Open what changed and how to update.`"
      data-update-notice
      @click="open = true"
    >
      <q-icon name="upgrade" size="16px" aria-hidden="true" />
      <span v-if="word" class="update-word" data-test="update-word">Update available</span>
      <q-tooltip v-else>Update available: v{{ status.latest }}</q-tooltip>
    </button>
    <UpdatesDialog v-model="open" :status="status" @checked="(answer) => (status = answer)" />
  </span>
</template>

<style scoped>
.update-notice {
  display: inline-flex;
  align-items: center;
  flex: 0 0 auto;
}

/* A small accent pill, so it is seen without shouting; a real button, so a keyboard reaches it. */
.update-link {
  margin-left: 8px;
  padding: 1px 8px;
  border: 1px solid var(--q-primary);
  border-radius: 999px;
  background: transparent;
  color: var(--q-primary);
  font: inherit;
  font-size: 0.72rem;
  line-height: 1.4;
  white-space: nowrap;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.update-link--icon {
  padding: 1px 4px;
}

.update-link:hover,
.update-link:focus-visible {
  background: color-mix(in srgb, var(--q-primary) 12%, transparent);
}
</style>
