<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { AvailableBackend, CoreInfo, YawbleApi } from '../../../shared/api';
import AboutSection from './AboutSection.vue';
import LoadingNote from './LoadingNote.vue';

const props = defineProps<{ api?: YawbleApi }>();
const panelApi = props.api ?? window.yawble;
const info = ref<CoreInfo | null>(null);
const backends = ref<AvailableBackend[] | null>(null);
const error = ref<string | null>(null);

onMounted(async () => {
  const api = props.api ?? window.yawble;
  try {
    [info.value, backends.value] = await Promise.all([api.getInfo(), api.getAvailableBackends()]);
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  }
});
</script>

<template>
  <div class="system">
    <AboutSection :api="panelApi" />

    <p class="phase">The core's identity and the sandbox's backend probe.</p>

    <p v-if="error" class="error" data-test="error">Core unavailable: {{ error }}</p>
    <LoadingNote v-else-if="!info" text="Loading the core's details…" />

    <template v-if="info">
      <section data-test="core">
        <h2>Core</h2>
        <dl>
          <dt>Version</dt><dd data-test="core-version">{{ info.version }}</dd>
          <dt>Platform</dt><dd>{{ info.platform }} · {{ info.os }} · .NET {{ info.dotnet }}</dd>
          <dt>Sandbox SDK</dt><dd data-test="sandbox-sdk">{{ info.sandbox.sdkVersion }} (native {{ info.sandbox.nativeVersion }})</dd>
          <dt>Request contract</dt>
          <dd data-test="sandbox-contract">{{ info.sandbox.requestContract }} · telemetry {{ info.sandbox.telemetryEnabled ? 'ON' : 'off' }}</dd>
          <dt>Schema</dt><dd>{{ info.schema.join(', ') || 'none' }}</dd>
        </dl>
      </section>

      <section data-test="backends">
        <h2>getAvailableBackends()</h2>
        <p v-if="backends && backends.length === 0" data-test="no-backends">
          No containment backend is available on this computer.
        </p>
        <table v-else-if="backends" class="backends">
          <thead><tr><th>Backend</th><th>Tier</th><th>Capabilities</th><th>Warnings</th></tr></thead>
          <tbody>
            <tr v-for="b in backends" :key="b.backend" data-test="backend-row">
              <td data-label="Backend">{{ b.backend }}</td>
              <td data-label="Tier">{{ b.tier ?? '—' }}</td>
              <td data-label="Capabilities">{{ b.capabilities.join(', ') || '—' }}</td>
              <td data-label="Warnings">{{ b.warnings.join('; ') || '—' }}</td>
            </tr>
          </tbody>
        </table>
        <pre data-test="backends-json">{{ JSON.stringify(backends) }}</pre>
      </section>

    </template>
  </div>
</template>

<style scoped>
.phase { color: var(--muted); font-size: 13px; }
h2 { font-size: 15px; margin: 18px 0 6px; }
section { background: var(--surface); border: 1px solid var(--line); border-radius: 6px; padding: 8px 14px 12px; margin-top: 12px; }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0; }
dt { color: var(--muted); }
dd { margin: 0; font-family: ui-monospace, monospace; }
table { border-collapse: collapse; width: 100%; font-size: 13px; }
th, td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--line-soft); }
pre { background: var(--surface-sunken); padding: 6px 8px; font-size: 12px; white-space: pre-wrap; word-break: break-all; }
.error { color: var(--bad); }
.ok { color: var(--ok); margin: 0 8px; }
.missing { color: var(--warn-ink); margin: 0 8px; }
code { font-size: 12px; color: var(--muted); }
/* A phone (B054): each backend is a stacked block of labelled lines (no table wider than the screen, no sideways
   scroller), so Capabilities and Warnings are in view. Its selector outweighs Settings' phone rule for tables. */
@media (max-width: 759.98px) {
  section table.backends, section table.backends tbody, section table.backends tr, section table.backends td { display: block; width: auto; overflow-x: visible; }
  section table.backends thead { display: none; }
  section table.backends tr { padding: 6px 0; border-bottom: 1px solid var(--line-soft); }
  section table.backends td { border: 0; padding: 2px 0; overflow-wrap: anywhere; }
  section table.backends td::before { content: attr(data-label) ": "; color: var(--muted); }
}
</style>
