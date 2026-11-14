<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { TeamHost, YawbleApi } from '../../../shared/api';
import { errorText, removeTeamHost, resolveApi, teamHosts, when } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

/**
 * The team's network allow list (policy.teamHosts): every member's run gets these hosts, members hired later too.
 * "Allow for this team" on a network request adds to it. Each host says who added it, how and when, and has Remove
 * (policy.removeTeamHost; the core audits it). A failed call shows its own error and the list is read again.
 */
const props = defineProps<{ teamId: number; api?: YawbleApi }>();
const emit = defineEmits<{ changed: [] }>();
const api = resolveApi(props.api);

const hosts = ref<TeamHost[]>([]);
const ready = ref(false);
const error = ref<string | null>(null);
const work = useBusy();

async function load() {
  try { hosts.value = (await teamHosts(api, props.teamId)).hosts; }
  catch (e) { error.value = errorText(e); }
  finally { ready.value = true; }
}

const remove = (host: string) => work.run('remove', async () => {
  error.value = null;
  try {
    hosts.value = (await removeTeamHost(api, props.teamId, host)).hosts;
    emit('changed');
  } catch (e) {
    error.value = errorText(e);
    await load();
  }
}, host);

const how = (h: TeamHost) => [h.addedBy, h.addedVia ? `via ${h.addedVia}` : null, when(h.addedAt)].filter(Boolean).join(' · ');

onMounted(load);
defineExpose({ load });
</script>

<template>
  <div class="team-hosts" data-test="team-hosts">
    <h4>The team's network allows</h4>
    <p class="muted small">Every member of the team reaches these hosts from its next run, members hired later too. "Allow for this team" on a network request adds one.</p>
    <p v-if="error" class="error small" data-test="team-hosts-error">{{ error }}</p>
    <LoadingNote v-if="!ready" text="Loading the team's hosts…" />
    <p v-else-if="!hosts.length" class="muted small" data-test="team-hosts-empty">No hosts are allowed for the whole team.</p>
    <div v-for="h in hosts" :key="h.host" class="row" :data-test="`team-host-${h.host}`">
      <span class="mono host">{{ h.host }}</span>
      <span class="grow muted small" data-test="team-host-added">{{ how(h) }}</span>
      <button class="link" :data-test="`team-host-remove-${h.host}`" :disabled="work.any()" @click="remove(h.host)">
        <BusyLabel :busy="work.is('remove', h.host)" text="Removing…">Remove</BusyLabel>
      </button>
    </div>
  </div>
</template>

<style scoped>
.team-hosts h4 { margin: 0 0 4px; font-size: 14px; }
.team-hosts p { margin: 0 0 8px; }
.row { display: flex; gap: 8px; align-items: baseline; padding: 3px 0; border-top: 1px solid var(--line-soft); }
.host { overflow-wrap: anywhere; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.grow { flex: 1; }
.link { border: 0; background: transparent; color: var(--accent); }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
