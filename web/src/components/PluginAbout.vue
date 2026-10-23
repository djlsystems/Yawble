<script setup lang="ts">
import { computed } from 'vue';
import { firstParagraph } from '../lib/pluginSettings';

/**
 * WHAT A PLUGIN IS, ABOVE ITS SETTINGS: the first paragraph of its description and a
 * "Documentation" link to its page on the marketplace, opened in a new tab, where the rest is. A
 * plugin the catalog does not list (`url` null) has no page to point at, so its whole description
 * is shown and there is no link. Package text is text, never HTML.
 */
const props = defineProps<{
  description: string;
  /** The plugin's marketplace page, from the Host; null when the catalog does not list it. */
  url: string | null | undefined;
}>();

const text = computed(() => (props.url ? firstParagraph(props.description) : props.description.trim()));
</script>

<template>
  <div v-if="text || url" class="plugin-about text-caption os-text-muted" data-plugin-about>
    <span class="plugin-about-text" data-plugin-description>{{ text }}</span>
    <template v-if="url">
      {{ ' ' }}<a :href="url" target="_blank" rel="noopener noreferrer" data-plugin-documentation>Documentation</a>
    </template>
  </div>
</template>

<style scoped>
.plugin-about-text {
  white-space: pre-line;
}
</style>
