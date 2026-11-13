<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { DoctorCheck, DoctorFix, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

/** Settings > Health: the doctor's checks (ops.doctor), Run again, and one button per fix (ops.doctorFix). */
const props = defineProps<{ api: YawbleApi }>();
const emit = defineEmits<{ open: [fix: DoctorFix]; changed: [] }>();

const checks = ref<DoctorCheck[] | null>(null);
const error = ref<string | null>(null);
const running = ref(false);
const message = ref<string | null>(null);
/** The fix being applied (its button shows the spinner; every fix button and Yes are held meanwhile). */
const fixing = ref<string | null>(null);
// A removal waiting for the person's yes: the doctor's question and the exact targets it would act on (credential
// key names or the team, never a value). The yes is sent with these targets, so the doctor refuses if they moved.
const asking = ref<{ fix: DoctorFix; question: string; targets: string[]; changed: boolean } | null>(null);
type FixAnswer = { done: boolean; needsConfirm: boolean; message: string; targets?: string[]; changed?: boolean };

// One ops.doctor at a time; a reload asked for while one runs (Run again, then a Fix) runs once more after it.
let again = false;
async function load() {
  if (running.value) { again = true; return; }
  running.value = true;
  error.value = null;
  try {
    do {
      again = false;
      try { checks.value = (await props.api.call<{ checks: DoctorCheck[] }>('ops.doctor', {})).checks; error.value = null; emit('changed'); }
      catch (e) { error.value = errorText(e); }
    } while (again);
  } finally { running.value = false; }
}

async function fix(f: DoctorFix) {
  if (f.kind === 'open') { message.value = null; error.value = null; emit('open', f); return; }
  if (fixing.value) return;
  message.value = null;
  error.value = null;
  asking.value = null;
  fixing.value = f.id;
  try {
    if (f.confirm) {
      // Ask first: without confirm the doctor only says what it would do, and to what.
      const r = await props.api.call<FixAnswer>('ops.doctorFix', { fix: f.id });
      if (r.needsConfirm) { asking.value = { fix: f, question: r.message, targets: r.targets ?? [], changed: false }; return; }
      message.value = r.message;
      await load();
      return;
    }
    const r = await props.api.call<FixAnswer>('ops.doctorFix', { fix: f.id, confirm: undefined });
    message.value = r.message;
    await load();
  } catch (e) { error.value = errorText(e); } finally { fixing.value = null; }
}

async function yes() {
  const a = asking.value;
  if (!a || fixing.value) return;
  message.value = null;
  error.value = null;
  fixing.value = 'confirm';
  try {
    const r = await props.api.call<FixAnswer>('ops.doctorFix', { fix: a.fix.id, confirm: true, expect: a.targets });
    // Cancel stays usable while the yes runs: then the answer is shown as a message, the confirmation stays closed.
    const stillAsking = asking.value === a;
    if (r.changed || r.needsConfirm) {
      // The yes was for the list shown; it moved, so nothing was done. Show the new list to say yes to again.
      if (stillAsking) asking.value = { fix: a.fix, question: r.message, targets: r.targets ?? [], changed: !!r.changed };
      else message.value = r.message;
      return;
    }
    if (stillAsking) asking.value = null;
    message.value = r.message;
    await load();
  } catch (e) { if (asking.value === a) asking.value = null; error.value = errorText(e); } finally { fixing.value = null; }
}

onMounted(load);
</script>

<template>
  <section class="health" data-test="settings-health">
    <div class="head">
      <h3>Health</h3>
      <button :disabled="running" data-test="health-run" :aria-busy="running" @click="load"><BusyLabel :busy="running" text="Checking…">Run again</BusyLabel></button>
    </div>
    <p v-if="error" class="error" data-test="health-error">{{ error }}</p>
    <p v-if="message" class="muted" data-test="health-message">{{ message }}</p>
    <LoadingNote v-if="!checks && !error" text="Loading health checks…" />
    <ul v-if="checks" class="checks">
      <li v-for="c in checks" :key="c.id" :class="c.status" data-test="health-check" :data-status="c.status">
        <span class="dot" :aria-label="c.status">{{ c.status === 'ok' ? '✓' : c.status === 'warn' ? '!' : '✕' }}</span>
        <span class="body">
          <b>{{ c.title }}</b>
          <span class="muted">{{ c.detail }}</span>
          <span v-if="(c.fixes ?? []).length" class="fixes">
            <button v-for="f in c.fixes" :key="f.id" data-test="health-fix" :disabled="!!fixing && f.kind !== 'open'" :aria-busy="fixing === f.id" @click="fix(f)"><BusyLabel :busy="fixing === f.id" text="Fixing…">{{ f.label }}</BusyLabel></button>
          </span>
        </span>
      </li>
    </ul>
    <div v-if="asking" class="confirm" role="alertdialog" data-test="health-confirm">
      <p v-if="asking.changed" class="error" data-test="health-changed">The list changed since you looked at it, so nothing was done. Here is the list now.</p>
      <p><b>{{ asking.fix.label }}?</b> {{ asking.question }}</p>
      <ul v-if="asking.targets.length" class="targets" data-test="health-confirm-targets">
        <li v-for="t in asking.targets" :key="t" data-test="health-confirm-target"><code>{{ t }}</code></li>
      </ul>
      <button class="danger" data-test="health-confirm-yes" :disabled="!!fixing" :aria-busy="fixing === 'confirm'" @click="yes"><BusyLabel :busy="fixing === 'confirm'" text="Working…">Yes, do it</BusyLabel></button>
      <button data-test="health-confirm-no" @click="asking = null">Cancel</button>
    </div>
  </section>
</template>

<style scoped>
.head { display: flex; align-items: center; justify-content: space-between; }
.head h3 { margin: 0 0 12px; font-size: 15px; }
.checks { list-style: none; padding: 0; margin: 0; }
.checks li { display: flex; gap: 10px; padding: 10px 0; border-bottom: 1px solid var(--line-soft); font-size: 13.5px; }
.body { display: flex; flex-direction: column; gap: 4px; }
.dot { width: 20px; height: 20px; border-radius: 50%; display: inline-flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 700; color: #fff; flex: none; }
.ok .dot { background: var(--ok, #2a8a4a); }
.warn .dot { background: var(--warn); }
.fail .dot { background: var(--bad); }
.fixes { display: flex; gap: 6px; flex-wrap: wrap; }
.muted { color: var(--muted); font-weight: 400; }
.error { color: var(--bad); }
.confirm { margin-top: 12px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 6px; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.confirm p { margin: 0; flex: 1 1 100%; }
.targets { margin: 0; padding-left: 20px; flex: 1 1 100%; max-height: 160px; overflow-y: auto; font-size: 12.5px; }
</style>
