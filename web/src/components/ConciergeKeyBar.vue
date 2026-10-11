<script setup lang="ts">
import { ref } from 'vue';
import { ControlKeys, ctrlSequence } from '../lib/keys';

const emit = defineEmits<{ key: [sequence: string] }>();

/**
 * Ctrl is a STICKY modifier, not a held one.
 *
 * A phone cannot hold two keys at once, so tapping Ctrl arms the next keystroke instead of sending
 * anything. Armed state is visible, or the person has no way to tell whether the next letter is
 * about to interrupt the agent.
 */
const ctrlArmed = ref(false);

function send(sequence: string) {
  emit('key', sequence);
  ctrlArmed.value = false;
}

/** Applies a pending Ctrl to a plain character coming from the terminal's own input. */
function sendCharacter(character: string) {
  send(ctrlArmed.value ? ctrlSequence(character) : character);
}

defineExpose({ ctrlArmed, sendCharacter });
</script>

<template>
  <q-toolbar class="concierge-keys bg-grey-10 text-white q-px-xs q-gutter-xs">
    <q-btn dense flat no-caps label="Esc" @click="send(ControlKeys.Esc)" />
    <q-btn dense flat no-caps label="Tab" @click="send(ControlKeys.Tab)" />

    <q-btn
      dense
      flat
      no-caps
      label="Ctrl"
      :color="ctrlArmed ? 'primary' : undefined"
      :class="ctrlArmed ? 'bg-white' : ''"
      @click="ctrlArmed = !ctrlArmed"
    />

    <!-- Its own button rather than Ctrl-then-C. The interrupt is what a person reaches for when
         something is going wrong, and making them arm a modifier first is a tax at exactly the
         wrong moment. -->
    <q-btn dense flat no-caps label="^C" @click="send(ctrlSequence('c'))" />

    <q-space />

    <q-btn dense flat icon="keyboard_arrow_left" aria-label="Left" @click="send(ControlKeys.Left)" />
    <q-btn dense flat icon="keyboard_arrow_down" aria-label="Down" @click="send(ControlKeys.Down)" />
    <q-btn dense flat icon="keyboard_arrow_up" aria-label="Up" @click="send(ControlKeys.Up)" />
    <q-btn dense flat icon="keyboard_arrow_right" aria-label="Right" @click="send(ControlKeys.Right)" />
  </q-toolbar>
</template>

<style scoped>
.concierge-keys {
  min-height: 44px; /* a touch target, not a mouse target */
}
</style>
