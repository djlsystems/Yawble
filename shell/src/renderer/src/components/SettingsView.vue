<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { usePhone } from '../phone';
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
import ConciergeSettings from './ConciergeSettings.vue';
import TeamsSettings from './TeamsSettings.vue';
import ScriptsSettings from './ScriptsSettings.vue';
import type { MenuItem } from './Sidebar.vue';
import LoadingNote from './LoadingNote.vue';

/**
 * Settings, one page with tabs: General (how the app runs on this computer), Agents, Concierge (its default agent), Teams
 * (New Team defaults and the member-name list), Skills, Guardrails (the global
 * policy), Integrations, Scripts (what applies to every script member), Cloud Link, Health, Companion (yawble-companion; this computer only), System. Each tab shows its screen in place.
 */
export type SettingsTab = 'general' | 'health' | 'companion' | 'concierge' | 'teams' | 'scripts' | MenuItem;

const props = defineProps<{
  api?: YawbleApi; tab: SettingsTab; teams: TeamSummary[]; pollMs?: number; integrationsTab?: 'cli' | 'api' | 'mcp' | 'connections' | null;
  /** On a phone: start at the list of sections (Settings opened as a place) rather than in `tab` (a link to one section). */
  landing?: boolean;
}>();
const emit = defineEmits<{ tab: [tab: SettingsTab]; connections: []; changed: []; link: [status: unknown] }>();
const api = resolveApi(props.api);

// A phone: one section per screen. Settings opens at the list of its sections; a tap opens that section with a
// Back to the list above it. A link to a section (Settings → Agents, Needs You's reconnect) opens it at once.
const phone = usePhone();
const sectionOpen = ref(!(props.landing ?? true));
watch(() => props.tab, () => { sectionOpen.value = true; });
function choose(id: SettingsTab) {
  sectionOpen.value = true;
  emit('tab', id);
}
const showList = computed(() => !phone.value || !sectionOpen.value);
const showPane = computed(() => !phone.value || sectionOpen.value);
const tabLabel = computed(() => tabs.value.find((t) => t.id === props.tab)?.label ?? 'Settings');

const tabs = computed(() => [
  { id: 'general' as const, label: 'General', test: 'open-general' },
  { id: 'agents' as const, label: 'Agents', test: 'open-agents' },
  { id: 'concierge' as const, label: 'Concierge', test: 'open-concierge' },
  { id: 'teams' as const, label: 'Teams', test: 'open-teams' },
  { id: 'skills' as const, label: 'Skills', test: 'open-skills' },
  { id: 'globalPolicy' as const, label: 'Guardrails', test: 'open-global-policy' },
  { id: 'integrations' as const, label: 'Integrations', test: 'open-integrations' },
  { id: 'scripts' as const, label: 'Scripts', test: 'open-scripts' },
  ...(api.remote ? [] : [{ id: 'link' as const, label: 'Cloud Link', test: 'open-link' }]),
  ...(api.remote ? [] : [{ id: 'health' as const, label: 'Health', test: 'open-health' }]),
  ...(api.remote ? [] : [{ id: 'companion' as const, label: 'Companion', test: 'open-companion' }]),
  { id: 'system' as const, label: 'System', test: 'open-system' },
]);
/** Skills fills the page's height (its list and text scroll on their own); every other tab scrolls as a page. On a
 *  phone Skills scrolls as a page too (one scroll level): the list, or the picked skill, in normal flow. */
const fill = computed(() => props.tab === 'skills' && !phone.value);

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
  <div class="settings-place" :class="{ fill: fill && showPane, phone }" data-test="settings" :data-phone-screen="phone ? (sectionOpen ? 'section' : 'list') : undefined">
    <div v-if="phone && sectionOpen" class="section-head">
      <button class="back" data-test="settings-back" aria-label="Back to Settings" @click="sectionOpen = false"><span class="back-icon" aria-hidden="true">‹</span>Settings</button>
      <h2 class="page-title" data-test="settings-section-title">{{ tabLabel }}</h2>
    </div>
    <h2 v-else class="page-title">Settings</h2>
    <nav v-if="showList" class="tabs" :class="{ list: phone }" role="tablist" :aria-orientation="phone ? 'vertical' : undefined">
      <button v-for="t in tabs" :key="t.id" role="tab" :aria-selected="tab === t.id" :class="{ on: tab === t.id && !phone }" :data-test="t.test"
        @click="choose(t.id)">{{ t.label }}<span v-if="phone" class="go-icon" aria-hidden="true">›</span></button>
    </nav>
    <div v-if="showPane" class="pane" :class="{ fill }" data-test="settings-pane">
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
      <ConciergeSettings v-else-if="tab === 'concierge'" :api="api" />
      <TeamsSettings v-else-if="tab === 'teams'" :api="api" />
      <ScriptsSettings v-else-if="tab === 'scripts'" :api="api" />
      <SkillsDialog v-else-if="tab === 'skills'" inline :fill="fill" :api="api" :teams="teams" />
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
/* ---- a phone: the list of sections (full-width rows), or one section under a Back to the list. Its panels' wide parts
   (two-column facts, tables, long inputs) fold to one column or scroll on their own. */
.settings-place.phone { padding: 4px calc(12px + env(safe-area-inset-right, 0px)) calc(16px + env(safe-area-inset-bottom, 0px)) calc(12px + env(safe-area-inset-left, 0px)); }
.phone .tabs.list { flex-direction: column; flex-wrap: nowrap; gap: 0; border-bottom: 0; border-top: 1px solid var(--line-soft); }
.phone .tabs.list button { display: flex; align-items: center; justify-content: space-between; width: 100%; min-height: 52px; padding: 0 6px 0 4px;
  border-bottom: 1px solid var(--line-soft); text-align: left; font-weight: 500; color: var(--ink); font-size: 15px; }
/* (no transform here: nothing on this page may be a containing block for a Modal's fixed backdrop) */
.go-icon { color: var(--muted); font-size: 20px; line-height: 1; }
.section-head { display: flex; flex-direction: column; align-items: flex-start; gap: 2px; margin-bottom: 10px; }
.back { display: inline-flex; align-items: center; gap: 2px; border: 0; background: transparent; color: var(--accent); padding: 0 8px 0 0; font-size: 15px; }
.back-icon { font-size: 22px; line-height: 1; margin-right: 2px; }
/* On a phone the section's header is one slim row that sticks to the top of the page's scroller (the only one),
   so "‹ Settings" stays in reach at the bottom of a long section (Guardrails). Its background hides what scrolls under. */
.phone .section-head { position: sticky; top: 0; z-index: 2; flex-direction: row; align-items: center; gap: 6px; min-height: 44px;
  margin: 0 0 8px; background: var(--surface); border-bottom: 1px solid var(--line-soft); }
.phone .section-head .back { flex: none; }
.phone .section-head .page-title { margin: 0; font-size: 17px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.phone .pane { max-width: none; }
/* One scroll level on a phone: no panel inside a section scrolls on its own (Health's targets). */
.phone :deep(.targets) { max-height: none; overflow: visible; }
.phone .check { padding: 12px 0; }
.phone :deep(table) { display: block; max-width: 100%; overflow-x: auto; }
.phone :deep(dl), .phone :deep(.facts) { grid-template-columns: minmax(0, 1fr); }
.phone :deep(dd) { margin-left: 0; overflow-wrap: anywhere; }
.phone :deep(pre), .phone :deep(code) { overflow-wrap: anywhere; }
.phone :deep(.add-cli input) { min-width: 0; width: 100%; }
.phone :deep(.prow) { grid-template-columns: minmax(0, 1fr); }
</style>
