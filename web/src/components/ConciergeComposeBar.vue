<script setup lang="ts">
import { ref } from 'vue';
import { composedLine } from '../lib/keys';

const emit = defineEmits<{ send: [sequence: string] }>();

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

function send() {
  const sequence = composedLine(text.value);

  // Cleared even when there was nothing to send, so a field holding only spaces does not look like
  // it still has something in it.
  text.value = '';

  if (sequence !== null) emit('send', sequence);
}
</script>

<template>
  <q-input
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
</template>

<style scoped>
.concierge-compose {
  /* A touch target, not a mouse target — the same floor the key bar holds. */
  min-height: 44px;
  flex: 0 0 auto;
}
</style>
