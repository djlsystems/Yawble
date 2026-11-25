<script setup lang="ts">
// Shown in place of a page whose file could not be loaded, most often because the app was rebuilt or updated while
// this window stayed open (the old file is gone). Reloading the window loads the current one.
import { resolveApi } from '../api';

// The desktop window reloads through its own Reload command (what Ctrl+R does); a page in a browser reloads itself.
const api = resolveApi(undefined);
function reload() {
  if (typeof api?.windowCommand === 'function') api.windowCommand('reload');
  else location.reload();
}
</script>

<template>
  <div class="failed" role="alert" data-test="page-load-failed">
    <p><b>This page could not be loaded.</b></p>
    <p class="muted">The app was probably updated while this window was open. Reload the window to load the current version.</p>
    <button class="primary" data-test="page-load-reload" @click="reload">Reload</button>
  </div>
</template>

<style scoped>
.failed { padding: 24px; display: flex; flex-direction: column; gap: 8px; align-items: flex-start; }
.failed p { margin: 0; }
.muted { color: var(--muted); }
</style>
