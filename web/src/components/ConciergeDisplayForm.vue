<script setup lang="ts">
import { TerminalTextColours, type TerminalTextColour } from '../lib/terminalDisplay';
import { useTerminalDisplayStore } from '../stores/terminalDisplay';
import FieldHelp from './FieldHelp.vue';

/**
 * THIS BROWSER'S CONCIERGE DISPLAY - font family, size and text colour, saved locally and applied live, with no
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

const textColourOptions = (Object.keys(TerminalTextColours) as TerminalTextColour[]).map((value) => ({
  value,
  label: TerminalTextColours[value].label,
}));
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

    <q-select
      :model-value="display.textColour"
      :options="textColourOptions"
      emit-value
      map-options
      outlined
      dense
      label="Text colour"
      data-test="terminal-text-colour"
      popup-content-class="concierge-settings"
      @update:model-value="(val) => val && display.set({ textColour: val })"
    >
      <template #prepend>
        <q-icon name="contrast" />
      </template>
      <template #append>
        <FieldHelp
          text="The terminal's default text and background. Colours the agent sets itself (links, hints, diffs) are drawn as it asks."
        />
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
