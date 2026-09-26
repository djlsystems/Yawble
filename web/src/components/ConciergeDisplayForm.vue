<script setup lang="ts">
import { useTerminalDisplayStore } from '../stores/terminalDisplay';

/**
 * THIS BROWSER'S CONCIERGE DISPLAY - font family and size, saved locally and applied live, with no
 * Save. It sits on Admin > Settings > Concierge beside the instance's Agent and Prompt, and says it
 * is this browser's own because everything else on that dialog is shared.
 */
const display = useTerminalDisplayStore();

const fontFamilyOptions = [
  '"Cascadia Mono", Consolas, monospace',
  '"IBM Plex Mono", Consolas, monospace',
  'Consolas, monospace',
  'ui-monospace, monospace',
];
</script>

<template>
  <div class="q-gutter-md concierge-display-form">
    <div class="text-subtitle2">Terminal display</div>
    <div class="text-caption os-text-muted">
      Saved in this browser only, as you change it. Everyone else keeps their own.
    </div>

    <!-- The popup class lifts the menu above the Concierge panel when Settings is opened from it. -->
    <q-select
      :model-value="display.fontFamily"
      :options="fontFamilyOptions"
      outlined
      dense
      label="Font family"
      popup-content-class="concierge-settings"
      @update:model-value="(val) => val && display.set({ fontFamily: val })"
    >
      <template #prepend>
        <q-icon name="format_size" />
      </template>
    </q-select>

    <div>
      <div class="text-caption os-text-muted q-mb-xs">Font size</div>
      <q-slider
        :model-value="display.fontSize"
        :min="10"
        :max="22"
        :step="1"
        label
        label-always
        @update:model-value="(val) => typeof val === 'number' && display.set({ fontSize: val })"
      />
    </div>
  </div>
</template>
