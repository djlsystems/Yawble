<script setup lang="ts">
// The window's confirm dialog (card 355), shown over everything for what confirm.ts asks; modelled on the cloud web
// app's AppDialog.vue. Enter = OK, Escape, a click on the backdrop or Cancel = cancel. OK has the focus; Tab stays
// inside; the focus goes back to where it was when the dialog closes. The keys are taken by a capture listener on the
// document while it is open (added on open, removed on close), so Escape never also closes the dialog under it.
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { confirmState } from '../confirm';
import { dimTitleBar } from '../titleBar';

const d = confirmState;
const box = ref<HTMLElement | null>(null);
const okBtn = ref<HTMLButtonElement | null>(null);
let before: Element | null = null;

function onKey(e: KeyboardEvent): void {
  if (!d.value || !box.value || e.isComposing) return;
  if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancel(); return; }
  if (e.key === 'Enter') {
    // On Cancel, Enter presses Cancel (the button's own click); anywhere else (OK, the text, the page) it is OK.
    if (document.activeElement instanceof HTMLButtonElement && box.value.contains(document.activeElement)) return;
    e.preventDefault(); e.stopPropagation(); ok(); return;
  }
  if (e.key === 'Tab') {
    const f = [...box.value.querySelectorAll<HTMLElement>('button:not(:disabled)')];
    if (!f.length) return;
    const i = f.indexOf(document.activeElement as HTMLElement);
    const next = e.shiftKey ? (i <= 0 ? f.length - 1 : i - 1) : (i === f.length - 1 ? 0 : i + 1);
    e.preventDefault(); e.stopPropagation();
    f[next]!.focus();
  }
}

let shown = false;
function opened(): void {
  if (shown) return;
  shown = true;
  before = document.activeElement;
  document.addEventListener('keydown', onKey, true);
  dimTitleBar(true);
}
function closed(): void {
  if (!shown) return;
  shown = false;
  document.removeEventListener('keydown', onKey, true);
  dimTitleBar(false);
  const b = before as HTMLElement | null;
  before = null;
  if (b && typeof b.focus === 'function' && document.contains(b)) b.focus();
}

watch(d, (now) => { if (now) opened(); else closed(); }, { flush: 'sync', immediate: true });
watch(d, (now) => { if (now) okBtn.value?.focus(); }, { flush: 'post' });
onMounted(() => { if (d.value) okBtn.value?.focus(); });

function ok(): void { d.value?.resolve(true); }
function cancel(): void { d.value?.resolve(false); }

onBeforeUnmount(() => { d.value?.resolve(false); closed(); });
</script>

<template>
  <div v-if="d" class="confirm-scrim" data-test="confirm-backdrop" @mousedown.self="cancel">
    <div ref="box" class="confirm" tabindex="-1" role="alertdialog" aria-modal="true" aria-describedby="confirm-msg" data-test="confirm-dialog">
      <p id="confirm-msg" class="msg" data-test="confirm-message">{{ d.message }}</p>
      <div class="buttons">
        <button type="button" data-test="confirm-cancel" @click="cancel">Cancel</button>
        <button ref="okBtn" type="button" :class="d.danger ? 'ok-danger' : 'primary'" data-test="confirm-ok" @click="ok">{{ d.okLabel }}</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* Above every Modal (z-index 10) and the drawer/menus. */
.confirm-scrim {
  position: fixed; inset: 0; z-index: 1000; display: flex; align-items: flex-start; justify-content: center; background: var(--backdrop);
  padding: var(--dialog-top) calc(16px + env(safe-area-inset-right, 0px)) calc(16px + env(safe-area-inset-bottom, 0px)) calc(16px + env(safe-area-inset-left, 0px));
}
.confirm {
  width: min(440px, 100%); max-height: 100%; overflow: auto; background: var(--surface); color: var(--ink);
  border: 1px solid var(--line); border-radius: 10px; box-shadow: 0 10px 40px var(--shadow);
  padding: 18px; display: flex; flex-direction: column; gap: 14px;
}
.confirm:focus { outline: none; } /* tabindex -1 only keeps a click on the text inside the dialog */
.msg { margin: 0; font-size: 14px; line-height: 1.45; overflow-wrap: anywhere; white-space: pre-wrap; }
.buttons { display: flex; justify-content: flex-end; gap: 8px; }
.buttons button { min-height: 36px; min-width: 88px; }
.buttons button.ok-danger { background: var(--bad); color: var(--on-accent); border-color: var(--bad); }
/* A phone: near the top (clear of a notch), the buttons share the width at a tap size. */
@media (max-width: 760px) {
  .confirm-scrim { padding-top: calc(12vh + env(safe-area-inset-top, 0px)); }
  .buttons button { flex: 1; min-width: 0; min-height: 44px; }
}
</style>
