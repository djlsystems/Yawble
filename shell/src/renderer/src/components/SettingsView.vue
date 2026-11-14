<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { YawbleApi, ShellSettings, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import AgentsDialog from './AgentsDialog.vue';
import SkillsDialog from './SkillsDialog.vue';
import GlobalPolicyDialog from './GlobalPolicyDialog.vue';
import LinkDialog from './LinkDialog.vue';
import SystemPanel from './SystemPanel.vue';
import IntegrationsPanel from './IntegrationsPanel.vue';
import HealthPanel from './HealthPanel.vue';
import UpdatesSection from './UpdatesSection.vue';
import BackupsSection from './BackupsSection.vue';
import CompanionPanel from './CompanionPanel.vue';
import type { MenuItem } from './Sidebar.vue';
import LoadingNote from './LoadingNote.vue';

/**
 * Settings, one page with tabs: General (how the app runs on this computer), Agents, Skills, Guardrails (the global
 * policy), Cloud Link, Health, Companion (yawble-companion; this computer only), System. Each tab shows its screen in place.
 */
export type SettingsTab = 'general' | 'health' | 'companion' | MenuItem;

const props = defineProps<{ api?: YawbleApi; tab: SettingsTab; teams: TeamSummary[]; pollMs?: number; integrationsTab?: 'cli' | 'api' | 'mcp' | 'connections' | null }>();
const emit = defineEmits<{ tab: [tab: SettingsTab]; connections: []; changed: []; link: [status: unknown] }>();
const api = resolveApi(props.api);

const tabs = computed(() => [
  { id: 'general' as const, label: 'General', test: 'open-general' },
  { id: 'agents' as const, label: 'Agents', test: 'open-agents' },
  { id: 'skills' as const, label: 'Skills', test: 'open-skills' },
  { id: 'globalPolicy' as const, label: 'Guardrails', test: 'open-global-policy' },
  { id: 'integrations' as const, label: 'Integrations', test: 'open-integrations' },
  ...(api.remote ? [] : [{ id: 'link' as const, label: 'Cloud Link', test: 'open-link' }]),
  ...(api.remote ? [] : [{ id: 'health' as const, label: 'Health', test: 'open-health' }]),
  ...(api.remote ? [] : [{ id: 'companion' as const, label: 'Companion', test: 'open-companion' }]),
  { id: 'system' as const, label: 'System', test: 'open-system' },
]);
/** Skills fills the page's height (its list and text scroll on their own); every other tab scrolls as a page. */
const fill = computed(() => props.tab === 'skills');

// General: the app's own settings, kept by the desktop app (not the core), so a cloud viewer has none to change.
const shell = ref<ShellSettings | null>(null);
const shellError = ref<string | null>(null);
const desktop = computed(() => typeof api.shellSettings === 'function');

onMounted(async () => {
  if (!desktop.value) return;
  try { shell.value = await api.shellSettings!(); } catch (e) { shellError.value = errorText(e); }
});

async function set(patch: Partial<ShellSettings>) {
  shellError.value = null;
  try { shell.value = await api.setShellSettings!(patch); } catch (e) { shellError.value = errorText(e); }
}
</script>

<template>
  <div class="settings-place" :class="{ fill }" data-test="settings">
    <h2 class="page-title">Settings</h2>
    <nav class="tabs" role="tablist">
      <button v-for="t in tabs" :key="t.id" role="tab" :aria-selected="tab === t.id" :class="{ on: tab === t.id }" :data-test="t.test"
        @click="emit('tab', t.id)">{{ t.label }}</button>
    </nav>
    <div class="pane" :class="{ fill }" data-test="settings-pane">
      <section v-if="tab === 'general'" class="general" data-test="settings-general">
        <h3>Running on this computer</h3>
        <template v-if="desktop">
          <p v-if="shellError" class="error">{{ shellError }}</p>
          <LoadingNote v-if="!shell && !shellError" text="Loading settings…" />
          <label v-if="shell" class="check">
            <input type="checkbox" :checked="shell.runInBackground" data-test="run-in-background" @change="set({ runInBackground: ($event.target as HTMLInputElement).checked })" />
            <span>
              <b>Keep running when the window is closed</b>
              <span class="muted">Your teams, schedules and the Cloud Link carry on; Yawble stays in the tray (the system's notification area).
                Open it or quit it from there.</span>
            </span>
          </label>
          <label v-if="shell" class="check">
            <input type="checkbox" :checked="shell.startAtLogin" data-test="start-at-login" @change="set({ startAtLogin: ($event.target as HTMLInputElement).checked })" />
            <span>
              <b>Start Yawble when I sign in to this computer</b>
              <span class="muted">It starts in the tray, without a window, so scheduled work runs even before you open it.</span>
              <span v-if="shell.startAtLogin && shell.startAtLoginRegistered === false" class="warn" data-test="login-not-registered">
                The system does not have Yawble in its startup list (it may have been turned off in the system's own settings).
                Turn this off and on again to add it back.
              </span>
            </span>
          </label>
        </template>
        <p v-else class="muted">These are set on the computer itself.</p>
        <UpdatesSection v-if="!api.remote" :api="api" />
        <BackupsSection v-if="!api.remote" :api="api" />
      </section>
      <HealthPanel v-else-if="tab === 'health' && !api.remote" :api="api" @open="emit('connections')" @changed="emit('changed')" />
      <CompanionPanel v-else-if="tab === 'companion' && !api.remote" :api="api" />
      <AgentsDialog v-else-if="tab === 'agents'" inline :api="api" />
      <SkillsDialog v-else-if="tab === 'skills'" inline fill :api="api" :teams="teams" />
      <GlobalPolicyDialog v-else-if="tab === 'globalPolicy'" inline :api="api" :poll-ms="pollMs" @changed="emit('changed')" />
      <IntegrationsPanel v-else-if="tab === 'integrations'" :key="integrationsTab ?? 'cli'" :api="api" :teams="teams" :initial-tab="integrationsTab ?? null" />
      <LinkDialog v-else-if="tab === 'link'" inline :api="api" @changed="emit('link', $event)" />
      <SystemPanel v-else-if="tab === 'system'" :api="api" />
    </div>
  </div>
</template>

<style scoped>
.settings-place { height: 100%; overflow: auto; padding: 4px 24px 24px; }
/* No CSS containment (container-type, contain, transform) on the page or the tab pane: tabs open Modals, whose
   position: fixed backdrop must be relative to the window, not to this scrolling box. */
/* Skills fills the page: the page is a column and the tab's pane takes the height left below the tabs, down to the
   bottom padding; its panes scroll on their own (min-height: 0 down the chain). Other tabs scroll as a page. */
.settings-place.fill { display: flex; flex-direction: column; }
.settings-place.fill > * { flex: none; }
.settings-place.fill > .pane { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; }
@media (max-width: 720px) { .settings-place.fill > .pane { display: block; flex: none; } }
.tabs { display: flex; gap: 4px; flex-wrap: wrap; border-bottom: 1px solid var(--line); margin-bottom: 16px; max-width: 980px; }
.tabs button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 8px 12px; color: var(--ink-soft); font-weight: 600; }
.tabs button.on { color: var(--accent); border-bottom-color: var(--accent); }
.pane { max-width: 980px; }
.general h3 { margin: 0 0 12px; font-size: 15px; }
.check { display: flex; gap: 10px; align-items: flex-start; padding: 10px 0; border-bottom: 1px solid var(--line-soft); cursor: pointer; }
.check input { margin-top: 3px; }
.check > span { display: flex; flex-direction: column; gap: 3px; font-size: 13.5px; }
.muted { color: var(--muted); font-weight: 400; }
.warn { color: var(--warn); font-size: 12.5px; }
.error { color: var(--bad); }
.page-title { margin: 0 0 8px; font-size: 22px; }
</style>
