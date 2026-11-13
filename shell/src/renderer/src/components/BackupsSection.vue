<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { BackupItem, YawbleApi, RestoreStatus } from '../../../shared/api';
import { errorText } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/** Settings > General: backups of the app's database. Restore is the person's alone, from this window. */
const props = defineProps<{ api: YawbleApi }>();

const keepDaily = ref(7);
const backups = ref<BackupItem[]>([]);
const error = ref<string | null>(null);
const message = ref<string | null>(null);
const busy = ref(false);
/** Which action runs (Back up now, Restore): that button shows the spinner. */
const doing = ref<'backup' | 'restore' | null>(null);
const restoring = ref<BackupItem | null>(null);
const restoreStatus = ref<RestoreStatus | null>(null);

const size = (n: number) => (n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
const when = (t: string) => new Date(t).toLocaleString();

const loaded = ref(false); // the first list arrived (or failed)
async function load() {
  try {
    backups.value = (await props.api.call<BackupItem[]>('ops.backups', {})) ?? [];
    keepDaily.value = (await props.api.call<{ keepDaily: number }>('ops.keepDaily', {})).keepDaily;
  } catch (e) { error.value = errorText(e); }
  loaded.value = true;
  // How the last restore went at the core's start (an older core has no such method: then nothing is shown).
  try { restoreStatus.value = await props.api.call<RestoreStatus>('ops.restoreStatus', {}); } catch { restoreStatus.value = null; }
}

/** The line about the last restore: "Restored" only when the core really swapped it in at start. */
function statusText(s: RestoreStatus): string | null {
  const l = s.last;
  if (l?.state === 'restored') return `Restored ${l.backup} (${when(l.at)}).`;
  if (l?.state === 'failed') return `The restore of ${l.backup} failed: ${l.reason ?? 'no reason given'}.`;
  if (l?.state === 'pending' || s.pending) {
    const name = l?.backup ?? s.pending;
    return `The restore of ${name} has not been applied yet${l?.reason ? `: ${l.reason}` : ''}. Yawble tries again when its core next starts.`;
  }
  return null;
}

async function setKeep(value: number) {
  error.value = null;
  if (!Number.isInteger(value) || value < 1) { error.value = 'Keep at least 1 daily backup.'; return; }
  try { keepDaily.value = (await props.api.call<{ keepDaily: number }>('ops.setKeepDaily', { count: value })).keepDaily; await load(); }
  catch (e) { error.value = errorText(e); }
}

async function backupNow() {
  if (busy.value) return;
  busy.value = true; doing.value = 'backup'; error.value = null; message.value = null;
  try { const b = await props.api.call<BackupItem>('ops.backupNow', {}); message.value = `Backed up: ${b.name}`; await load(); }
  catch (e) { error.value = errorText(e); }
  finally { busy.value = false; doing.value = null; }
}

// The confirmation stays up (its Restore shows Restoring…) until the core answers.
async function restore(b: BackupItem) {
  if (busy.value) return;
  try { await doRestore(b); } finally { restoring.value = null; }
}
async function doRestore(b: BackupItem) {
  busy.value = true; doing.value = 'restore'; error.value = null;
  try {
    await props.api.call('ops.restore', { backup: b.name, confirm: true });
    message.value = 'The backup is ready to restore. Yawble is restarting its core to put it in place; this window reloads in a moment.';
  } catch (e) { error.value = errorText(e); }
  finally { busy.value = false; doing.value = null; }
}

onMounted(load);
</script>

<template>
  <section class="backups" data-test="settings-backups">
    <h3>Backups</h3>
    <p class="muted">Yawble backs up its database every day while it runs, and before an update changes it. Passwords and sign-ins are not copied.</p>
    <p v-if="error" class="error" data-test="backups-error">{{ error }}</p>
    <p v-if="message" class="muted" data-test="backups-message">{{ message }}</p>
    <p v-if="restoreStatus && statusText(restoreStatus)" :class="restoreStatus.last?.state === 'restored' ? 'muted' : 'error'"
       data-test="restore-status" :data-state="restoreStatus.last?.state ?? 'pending'">{{ statusText(restoreStatus) }}</p>
    <label class="keep">
      <span>Daily backups to keep</span>
      <input type="number" min="1" :value="keepDaily" data-test="keep-daily" @change="setKeep(Number(($event.target as HTMLInputElement).value))" />
    </label>
    <button :disabled="busy" data-test="backup-now" :aria-busy="doing === 'backup'" @click="backupNow"><BusyLabel :busy="doing === 'backup'" text="Backing up…">Back up now</BusyLabel></button>
    <LoadingNote v-if="!loaded" text="Loading backups…" />
    <table v-else-if="backups.length" class="list">
      <thead><tr><th>When</th><th>Why</th><th>Size</th><th></th></tr></thead>
      <tbody>
        <tr v-for="b in backups" :key="b.name" data-test="backup-row">
          <td>{{ when(b.time) }}</td><td>{{ b.reason }}</td><td>{{ size(b.size) }}</td>
          <td><button :disabled="busy" data-test="backup-restore" @click="restoring = b">Restore…</button></td>
        </tr>
      </tbody>
    </table>
    <p v-else class="muted" data-test="no-backups">No backups yet.</p>
    <div v-if="restoring" class="confirm" role="alertdialog" data-test="restore-confirm">
      <p><b>Restore {{ when(restoring.time) }}?</b> Everything since then is lost: teams, cards and messages return to how they were.
        The current database is backed up first, and Yawble restarts its core.</p>
      <button class="danger" data-test="restore-yes" :disabled="busy" :aria-busy="doing === 'restore'" @click="restore(restoring)"><BusyLabel :busy="doing === 'restore'" text="Restoring…">Restore</BusyLabel></button>
      <button data-test="restore-no" @click="restoring = null">Cancel</button>
    </div>
  </section>
</template>

<style scoped>
.backups { margin-top: 24px; }
.backups h3 { margin: 0 0 8px; font-size: 15px; }
.keep { display: flex; gap: 10px; align-items: center; margin: 10px 0; font-size: 13.5px; }
.keep input { width: 64px; }
.list { border-collapse: collapse; width: 100%; margin-top: 12px; font-size: 13px; }
.list th, .list td { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--line-soft); }
.muted { color: var(--muted); font-weight: 400; }
.error { color: var(--bad); }
.confirm { margin-top: 12px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 6px; display: flex; gap: 8px; flex-wrap: wrap; }
.confirm p { margin: 0; flex: 1 1 100%; }
</style>
