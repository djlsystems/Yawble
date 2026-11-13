<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { UpdateStatus, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import BusyLabel from './BusyLabel.vue';

/** Settings > General, Updates: this version, the newest release, automatic checks, Check now. */
const props = defineProps<{ api: YawbleApi }>();

const status = ref<UpdateStatus | null>(null);
const checking = ref(false);
const error = ref<string | null>(null);

async function load() {
  try { status.value = await props.api.call<UpdateStatus>('updates.status'); }
  catch (e) { error.value = errorText(e); }
}

async function check() {
  checking.value = true;
  error.value = null;
  try { status.value = await props.api.call<UpdateStatus>('updates.check'); }
  catch (e) { error.value = errorText(e); }
  finally { checking.value = false; }
}

async function setAuto(on: boolean) {
  error.value = null;
  try { status.value = await props.api.call<UpdateStatus>('updates.setAuto', { on }); }
  catch (e) { error.value = errorText(e); }
}

function line(s: UpdateStatus): string {
  switch (s.state) {
    case 'upToDate': return 'This is the latest version.';
    case 'available': return `Yawble ${s.latest} is available.`;
    case 'downloading': return `Downloading Yawble ${s.latest}…`;
    case 'ready': return `Yawble ${s.latest} is downloaded and ready: Restart to update, at the top of the window.`;
    case 'error': return s.error ?? 'The last check did not work.';
    case 'checking': return 'Checking…';
    default: return s.checkedAt ? '' : 'Not checked yet.';
  }
}

onMounted(load);
</script>

<template>
  <section class="updates" data-test="updates-section">
    <h3>Updates</h3>
    <p v-if="status?.managed" data-test="updates-managed">Yawble {{ status.current }}. Updates to this app are managed by your organization.</p>
    <p v-else-if="status" data-test="updates-line">
      Yawble {{ status.current }}. <span :class="{ error: status.state === 'error' }">{{ line(status) }}</span>
      <a v-if="status.available" :href="status.notesUrl" target="_blank" rel="noopener">What's new</a>
    </p>
    <label v-if="status && !status.managed" class="check">
      <input type="checkbox" :checked="status.auto" data-test="updates-auto" @change="setAuto(($event.target as HTMLInputElement).checked)" />
      <span>
        <b>Check for updates automatically</b>
        <span class="muted">On start and every 6 hours, from GitHub Releases. A newer version is downloaded and checked
          (its SHA-256 and the publisher's signature); it is installed only when you choose Restart to update.</span>
      </span>
    </label>
    <button v-if="!status?.managed" type="button" :disabled="checking" data-test="updates-check" @click="check"><BusyLabel :busy="checking" text="Checking…">Check now</BusyLabel></button>
    <p v-if="error" class="error" data-test="updates-error">{{ error }}</p>
  </section>
</template>

<style scoped>
.updates { margin-top: 18px; }
.updates p { margin: 6px 0; }
</style>
