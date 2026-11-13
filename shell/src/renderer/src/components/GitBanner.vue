<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { GitInstallResult, GitStatus, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import BusyLabel from './BusyLabel.vue';

/**
 * The line at the top of the window when git is missing, too old or not working: teams need git (any repository host
 * works, or none). On this computer: Install Git / Update Git (git.install, winget on Windows), Download Git, Check
 * again. A cloud viewer sees the line only: installing happens on the computer itself.
 */
const props = defineProps<{ api: YawbleApi }>();

const status = ref<GitStatus | null>(null);
const installing = ref(false);
const checking = ref(false);
const message = ref<string | null>(null);
const error = ref<string | null>(null);

const show = computed(() => status.value !== null && status.value.state !== 'ok');

const text = computed(() => {
  const s = status.value;
  if (!s) return '';
  switch (s.state) {
    case 'old': return `Git ${s.version} is too old: teams need git ${s.minimum} or later to merge and sync their branches.`;
    case 'broken': return `Git at ${s.path} does not work${s.detail ? ` (${s.detail})` : ''}: teams need a working git ${s.minimum} or later.`;
    default: return `Git is not installed on this computer: teams need git ${s.minimum} or later to run. Any repository host works, or none.`;
  }
});

const installLabel = computed(() => (status.value?.state === 'missing' ? 'Install Git' : 'Update Git'));

async function check() {
  checking.value = true;
  error.value = null;
  try { status.value = await props.api.call<GitStatus>('git.check'); }
  catch (e) { error.value = errorText(e); }
  finally { checking.value = false; }
}

async function install() {
  if (installing.value) return;
  installing.value = true;
  message.value = null;
  error.value = null;
  try {
    const r = await props.api.call<GitInstallResult>('git.install');
    status.value = r.status;
    if (r.done) message.value = r.message;
    else error.value = r.message;
  } catch (e) { error.value = errorText(e); }
  finally { installing.value = false; }
}

onMounted(check);
defineExpose({ check });
</script>

<template>
  <div v-if="show" class="git-banner" role="alert" data-test="git-banner" :data-state="status!.state">
    <span class="text">{{ text }}</span>
    <span class="actions">
      <button v-if="status!.canInstall && !api.remote" type="button" :disabled="installing" data-test="git-install" @click="install">
        <BusyLabel :busy="installing" text="Installing Git…">{{ installLabel }}</BusyLabel>
      </button>
      <a :href="status!.downloadUrl" target="_blank" rel="noopener" data-test="git-download">Download Git</a>
      <button type="button" class="mini" :disabled="checking || installing" data-test="git-recheck" @click="check">Check again</button>
    </span>
    <span v-if="api.remote" class="muted small" data-test="git-remote-note">Install it on the computer itself.</span>
    <span v-if="error" class="error" data-test="git-error">{{ error }}</span>
  </div>
  <p v-else-if="message" class="ok-line" data-test="git-done">{{ message }} <button class="mini" type="button" @click="message = null">Dismiss</button></p>
</template>

<style scoped>
.git-banner { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; padding: 6px 18px; margin: 0; border: 1px solid #c0392b; background: #fdecea; color: #1d2330; }
.git-banner .text { flex: 1 1 320px; }
.git-banner .actions { display: inline-flex; gap: 8px; align-items: center; }
.git-banner .error { flex-basis: 100%; color: #a01818; }
.ok-line { padding: 6px 18px; margin: 0; color: var(--ok); }
</style>
