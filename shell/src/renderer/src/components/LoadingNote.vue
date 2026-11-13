<script setup lang="ts">
// The one loading state for Settings tabs and the dialogs that fetch on open: a small CSS-only spinner and
// "Loading <thing>…" in muted text, placed where the content will appear. It does not spin when the person asks for
// reduced motion. Theme colours only (CSS variables).
withDefaults(defineProps<{ text?: string }>(), { text: 'Loading…' });
</script>

<template>
  <p class="loading-note" role="status" aria-live="polite" data-test="loading-note">
    <span class="spinner" aria-hidden="true"></span>
    <span>{{ text }}</span>
  </p>
</template>

<style scoped>
.loading-note { display: flex; align-items: center; gap: 8px; margin: 6px 0; color: var(--muted); font-size: 13px; font-weight: 400; }
.spinner {
  flex: none; width: 12px; height: 12px; box-sizing: border-box; border-radius: 50%;
  border: 2px solid var(--line); border-top-color: var(--accent);
  animation: loading-note-spin 0.8s linear infinite;
}
@keyframes loading-note-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) {
  .spinner { animation: none; border-top-color: var(--muted); }
}
</style>
