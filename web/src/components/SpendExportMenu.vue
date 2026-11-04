<script setup lang="ts">
import { spendExportUrl } from '../api/spend';
import { ExportFiles, ExportTitle, periodLabel } from '../lib/spend';

/**
 * THE SPEND PAGE'S EXPORT: one CSV file each - billing accounts, expenses, the period summary, and Estimated
 * Execution Cost by team and outcome - for the period the page shows. Each is a plain link to its route, so
 * the browser downloads it with the person's session; the server builds every figure, the same as the page's.
 */
const props = defineProps<{ period: { from: string; to: string } }>();
</script>

<template>
  <q-btn-dropdown flat dense no-caps icon="download" :label="ExportTitle" class="q-ml-md" data-spend-export>
    <q-list dense>
      <q-item-label header>CSV for {{ periodLabel(props.period) }} (UTC)</q-item-label>
      <q-item
        v-for="entry in ExportFiles"
        :key="entry.file"
        v-close-popup
        clickable
        tag="a"
        :href="spendExportUrl(entry.file, props.period)"
        download
        :data-spend-export-file="entry.file"
      >
        <q-item-section>
          <q-item-label>{{ entry.label }}</q-item-label>
          <q-item-label caption>{{ entry.caption }}</q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
  </q-btn-dropdown>
</template>
