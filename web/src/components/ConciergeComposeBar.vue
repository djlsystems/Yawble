<script setup lang="ts">
import { nextTick, ref } from 'vue';
import type { QInput } from 'quasar';
import { composedLine } from '../lib/keys';
import { dragCarriesFiles, droppedImages, NotAnImage, pastedImage } from '../lib/conciergeAttachment';

const emit = defineEmits<{ send: [sequence: string]; images: [files: File[]]; refused: [sentence: string] }>();

/**
 * A real text field, which is the entire point.
 *
 * Typing into the terminal sends each keystroke onward the instant it happens, so there is nothing
 * left for the phone to revise — and iOS dictation works by revising. It rewrites its transcript as
 * you speak ("a" → "ask" → "ask the manager"), and against a PTY every revision lands as more text
 * rather than replacing the last, which is how one sentence became "aaskask the managerask the...".
 * Autocorrect and moving the caret to fix a word fail the same way and for the same reason.
 *
 * Nothing reaches the agent until Send. Until then this is an ordinary input, so dictation, spelling
 * correction, selection and paste all behave the way they do everywhere else on the phone.
 */
const text = ref('');

/*
 * The autocorrect/autocapitalize/enterkeyhint attributes below sit directly on QInput rather than in
 * an `input-attrs` object: QInput forwards anything that is not one of its own props straight to the
 * native input, and there is no such prop. Passing them as one would have compiled, rendered, and
 * quietly done nothing — leaving a field that looks right and helps with nothing.
 */

const field = ref<QInput>();

/**
 * AN IMAGE PASTED OR DROPPED HERE IS HANDED TO THE PANEL, which uploads it and calls `insert` with
 * the path. A text paste is left to the field, so it behaves as it does everywhere else.
 */
function onPaste(event: ClipboardEvent) {
  const image = pastedImage(event.clipboardData);
  if (!image) return;

  event.preventDefault();
  emit('images', [image]);
}

function onDragOver(event: DragEvent) {
  if (dragCarriesFiles(event.dataTransfer)) event.preventDefault();
}

function onDrop(event: DragEvent) {
  if (!dragCarriesFiles(event.dataTransfer)) return;

  // Cancelled even when nothing in it is an image: a file the browser is left to handle is one it
  // navigates to, taking the panel with it.
  event.preventDefault();
  event.stopPropagation();

  // A drop with no image in it is told so in the same sentence the shell uses, not ignored.
  const images = droppedImages(event.dataTransfer);
  if (images.length > 0) emit('images', images);
  else emit('refused', NotAnImage);
}

/**
 * Puts text at the caret, replacing any selection, as a paste would, and leaves the caret after it.
 * At the end when the field has never been focused.
 */
async function insert(inserted: string) {
  const input = field.value?.getNativeElement() as HTMLInputElement | undefined;
  const start = input?.selectionStart ?? text.value.length;
  const end = input?.selectionEnd ?? start;

  text.value = text.value.slice(0, start) + inserted + text.value.slice(end);

  await nextTick();
  const caret = start + inserted.length;
  input?.setSelectionRange(caret, caret);
}

defineExpose({ insert });

function send() {
  const sequence = composedLine(text.value);

  // Cleared even when there was nothing to send, so a field holding only spaces does not look like
  // it still has something in it.
  text.value = '';

  if (sequence !== null) emit('send', sequence);
}
</script>

<template>
  <!-- The paste and drop listeners sit on a wrapper because the events bubble here from the native
       input, and QInput claims a paste listener of its own for its mask. -->
  <div class="concierge-compose-wrap" @paste="onPaste" @dragover="onDragOver" @drop="onDrop">
    <q-input
      ref="field"
      v-model="text"
      dense
      outlined
      dark
      class="concierge-compose bg-grey-10 q-px-xs q-py-xs"
      placeholder="Type or dictate, then send"
      aria-label="Compose a line for the console"
      enterkeyhint="send"
      autocorrect="on"
      autocapitalize="sentences"
      spellcheck="true"
      @keyup.enter="send"
    >
      <template #append>
        <q-btn dense flat round icon="send" aria-label="Send" :disable="!text.trim()" @click="send" />
      </template>
    </q-input>
  </div>
</template>

<style scoped>
.concierge-compose-wrap {
  flex: 0 0 auto;
}

.concierge-compose {
  /* A touch target, not a mouse target — the same floor the key bar holds. */
  min-height: 44px;
}
</style>
