<script setup lang="ts">
import { computed, ref } from 'vue';
import { concierge as readConcierge, listCatalog, setConcierge } from '../api/client';
import { agentsForMode, type Agent, type ConciergeSettings } from '../api/types';
import { useConsoleStore } from '../stores/console';

/**
 * Which Agent the tenant-wide CONCIERGE launches. What it is told is the built-in Concierge prompt,
 * chosen by role and never here. The Concierge tab of Tenant
 * Settings; the Concierge dialog keeps only this browser's own display settings. The parent calls `load()` on open and `save()` from its one Save button.
 *
 * IT NAMES NO TEAM, AND THAT IS THE WHOLE OF IT. A session is keyed on the USER alone, so there is
 * one Concierge per person, serving every team they reach. It reads `GET /api/concierge`, so it
 * works on an instance with no teams at all - exactly the instance most likely to need it.
 *
 * INTERACTIVE presets only, and that is not cosmetic. A headless preset has no command that can be
 * typed at - `claude -p` prints one answer and exits - so choosing one gives a terminal that opens,
 * spawns something, and dies. The server refuses the same thing again on the way in.
 */
const board = useConsoleStore();

/** What the instance is set to RIGHT NOW, read on load. `dirty` compares against it. */
const current = ref<ConciergeSettings | null>(null);

const presets = ref<Agent[]>([]);

/** NULL is NOBODY HAS CHOSEN, as on the wire: the server runs its default. Clearing the select
 *  puts it back there, and Save sends a blank `agent`, which the server stores as NULL. */
const agent = ref<string | null>(null);

/** What the server resolves a null choice to - named only to SAY so. NULL when it could not say. */
const defaultAgent = computed(() =>
  current.value?.effective?.agentSource === 'default' ? current.value.effective.agent : null);

/** Shown once the setting has answered, so "no Agent chosen" is never said about a failed read. */
const noAgentChosen = computed(() => !loading.value && !loadError.value && current.value !== null && agent.value === null);

const loading = ref(false);
const loadError = ref('');

const names = computed(() => presets.value.map((a) => a.name));

/** The current setting is offered even when the catalog no longer has it: a blank select reads as
 *  "not set", which is a different and more alarming thing than "set to something now gone". */
const options = computed(() =>
  agent.value && !names.value.includes(agent.value) ? [agent.value, ...names.value] : names.value,
);

const noneAvailable = computed(() => !loading.value && !loadError.value && names.value.length === 0);

async function load() {
  agent.value = null;
  loading.value = true;
  loadError.value = '';

  try {
    // TOGETHER: a catalog without the setting offers every option and shows none as chosen.
    const [catalog, settings] = await Promise.all([listCatalog(), readConcierge()]);

    presets.value = agentsForMode(catalog.agents, 'Interactive');
    current.value = settings;

    agent.value = settings.agent;
  } catch (cause) {
    presets.value = [];
    current.value = null;
    loadError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

/** Something here differs from what is stored. */
const agentMoved = computed(() => current.value !== null && agent.value !== current.value.agent);

const dirty = computed(() => agentMoved.value);

/** A Save now would be refused: nothing to launch. A NULL agent is not this - it is the default. */
const invalid = computed(() => dirty.value && noneAvailable.value);

/** Writes the Concierge. Throws the server's words verbatim, for the parent to show. */
async function save() {
  if (!dirty.value) return;

  // `agent` cleared goes as "" - the server's word for "back to the default".
  await setConcierge({ agent: agent.value ?? '' });

  // Every team summary carries the Concierge, so they are all now stale on screen.
  await board.refresh();
  await load();
}

defineExpose({ load, save, dirty, invalid });
</script>

<template>
  <div class="q-gutter-md">
    <q-banner v-if="loadError" dense class="os-bg-tint-error text-negative">
      <template #avatar><q-icon name="error" /></template>
      Could not read the Agent catalog: {{ loadError }}
    </q-banner>

    <!-- Not an empty dropdown: this says what is missing and where to fix it. -->
    <q-banner v-else-if="noneAvailable" dense class="os-bg-tint-warn text-warning">
      <template #avatar><q-icon name="warning" /></template>
      No Agent that can run the Concierge is configured. Add one in <strong>Agents</strong>
      with its mode set to Concierge; a headless preset has no command that can be typed
      at, so it cannot run the Concierge terminal.
    </q-banner>

    <q-select
      v-else
      v-model="agent"
      :options="options"
      :loading="loading"
      outlined
      dense
      emit-value
      map-options
      label="Agent"
      clearable
      popup-content-class="concierge-settings"
      hint="Presets that can run the Concierge only. Takes effect the next time a Concierge is opened; a session already running keeps what it started with."
    />

    <!-- A warning line rather than an error: the Concierge still opens, on the default. -->
    <div v-if="noAgentChosen && !noneAvailable" class="concierge-default-agent text-warning row no-wrap items-start q-gutter-x-xs" role="status">
      <q-icon name="warning" size="16px" class="q-mt-xs" aria-hidden="true" />
      <span v-if="defaultAgent" class="concierge-default-agent-text">
        No Agent chosen. The Concierge will use the default, <strong>{{ defaultAgent }}</strong>.
      </span>
      <span v-else class="concierge-default-agent-text">
        No Agent chosen. The Concierge will use the default.
      </span>
    </div>

    <div v-if="!loadError" class="text-caption os-text-muted">
      The Concierge is told the built-in Concierge prompt, whichever Agent runs it.
    </div>
  </div>
</template>
