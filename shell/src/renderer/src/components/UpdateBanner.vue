<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { Overview, UpdateStatus, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import BusyLabel from './BusyLabel.vue';

/**
 * The line at the top of the window about an update to the app itself (updates.status): downloading it, ready to
 * install (Restart to update), or available to download. Restart to update asks first while agents are working, since
 * the app quits to install. "Later" hides it until a newer version. A cloud viewer is told; installing is on the
 * computer. A development run of the source shows nothing.
 */
const props = defineProps<{ api: YawbleApi; pollMs?: number }>();

const status = ref<UpdateStatus | null>(null);
const packaged = ref<boolean | null>(null);
const later = ref<string | null>(readLater());
const busy = ref<'download' | 'install' | null>(null);
const working = ref<number | null>(null);
const error = ref<string | null>(null);
/** "You have the latest version" after a check the person asked for (Help > Check for updates). */
const told = ref<string | null>(null);

function readLater(): string | null {
  try { return localStorage.getItem('yawble.update.later'); } catch { return null; }
}
function hide() {
  later.value = status.value?.latest ?? null;
  try { if (later.value) localStorage.setItem('yawble.update.later', later.value); } catch { /* per-window convenience only */ }
}

const show = computed(() => {
  const s = status.value;
  if (!s || !s.available || !s.latest || later.value === s.latest) return false;
  if (props.api.remote) return true;
  return packaged.value !== false && ['available', 'downloading', 'ready', 'error'].includes(s.state);
});
const percent = computed(() => (status.value && status.value.size > 0 ? Math.min(100, Math.round((status.value.downloaded / status.value.size) * 100)) : null));

async function refresh() {
  try { status.value = await props.api.call<UpdateStatus>('updates.status'); }
  catch { /* an older core: no updates */ }
}

async function download() {
  busy.value = 'download';
  error.value = null;
  try { status.value = await props.api.call<UpdateStatus>('updates.download'); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = null; }
}

/** Restart to update: while agents run, say so and wait for a second yes. */
async function install(confirmed = false) {
  error.value = null;
  if (!confirmed) {
    try {
      const running = (await props.api.call<Overview>('app.overview')).slots.running;
      if (running > 0) { working.value = running; return; }
    } catch { /* the overview could not be read: install anyway, as quitting does */ }
  }
  working.value = null;
  busy.value = 'install';
  try { await props.api.installUpdate?.(); }
  catch (e) { error.value = errorText(e); busy.value = null; }
}

/** Help > Check for updates: checks now, and says so when there is nothing newer. */
async function checkNow() {
  told.value = null;
  later.value = null;
  try { status.value = await props.api.call<UpdateStatus>('updates.check'); }
  catch (e) { told.value = errorText(e); return; }
  const s = status.value;
  if (s.state === 'upToDate') told.value = `Yawble ${s.current} is the latest version.`;
  else if (s.state === 'error') told.value = s.error;
}

let timer: ReturnType<typeof setInterval> | undefined;
onMounted(async () => {
  try { packaged.value = (await props.api.shellSettings?.())?.packaged ?? (props.api.remote ? null : false); } catch { packaged.value = false; }
  await refresh();
  timer = setInterval(() => void refresh(), props.pollMs ?? 30_000);
});
onBeforeUnmount(() => clearInterval(timer));
defineExpose({ checkNow, refresh });
</script>

<template>
  <div v-if="show" class="update-banner" role="status" data-test="update-banner" :data-state="status!.state">
    <template v-if="api.remote">
      <span class="text">Yawble {{ status!.latest }} is available. It is installed on the computer itself.</span>
    </template>
    <template v-else-if="status!.state === 'ready'">
      <span class="text">Yawble {{ status!.latest }} is ready to install. The app restarts to update.</span>
      <button type="button" class="primary" :disabled="busy === 'install'" data-test="update-install" @click="install()">
        <BusyLabel :busy="busy === 'install'" text="Restarting…">Restart to update</BusyLabel>
      </button>
    </template>
    <template v-else-if="status!.state === 'downloading'">
      <span class="text" data-test="update-downloading">Downloading Yawble {{ status!.latest }}<template v-if="percent !== null">… {{ percent }}%</template></span>
    </template>
    <template v-else>
      <span class="text">Yawble {{ status!.latest }} is available.</span>
      <button v-if="status!.canInstall" type="button" :disabled="busy === 'download'" data-test="update-download" @click="download">
        <BusyLabel :busy="busy === 'download'" text="Downloading…">Download</BusyLabel>
      </button>
    </template>
    <a :href="status!.notesUrl" target="_blank" rel="noopener" data-test="update-notes">What's new</a>
    <button v-if="!api.remote" type="button" class="mini" data-test="update-later" @click="hide">Later</button>
    <span v-if="working" class="confirm" data-test="update-confirm">
      {{ working }} agent{{ working === 1 ? ' is' : 's are' }} working; updating stops {{ working === 1 ? 'it' : 'them' }} now.
      <button type="button" class="danger mini" data-test="update-anyway" @click="install(true)">Update now</button>
      <button type="button" class="mini" data-test="update-wait" @click="working = null">Wait</button>
    </span>
    <span v-if="error || (status!.state === 'error' && status!.error)" class="error" data-test="update-error">{{ error ?? status!.error }}</span>
  </div>
  <p v-else-if="told" class="told" data-test="update-told">{{ told }} <button class="mini" type="button" @click="told = null">Dismiss</button></p>
</template>

<style scoped>
.update-banner { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; padding: 6px 18px; margin: 0; border-bottom: 1px solid var(--line); background: #eef4ff; color: #1d2330; }
.update-banner .text { flex: 1 1 280px; }
.update-banner .confirm, .update-banner .error { flex-basis: 100%; }
.update-banner .error { color: #a01818; }
.told { padding: 6px 18px; margin: 0; }
</style>
