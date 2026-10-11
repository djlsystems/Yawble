<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { concierge as readConcierge, endConciergeOf } from '../api/client';
import type { ConciergeSessionView } from '../api/types';

/**
 * THE RUNNING CONCIERGE SESSIONS, any person's: on which worker, whether someone has it open, what it
 * last did, when the idle window would end it, and what it costs its worker. Read when the tab
 * opens. A session is ended only when nobody has it open AND it has done nothing for the window, so
 * "would end at" is blank while someone has it open and moves while it works.
 *
 * End is the same `DELETE /api/concierge?user=` any person may already make.
 */
const sessions = ref<ConciergeSessionView[]>([]);
const loading = ref(false);
const error = ref('');
const ending = ref<string | null>(null);

async function load() {
  loading.value = true;
  error.value = '';

  try {
    sessions.value = (await readConcierge()).sessions ?? [];
  } catch (cause) {
    sessions.value = [];
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

async function end(user: string) {
  ending.value = user;
  error.value = '';

  try {
    await endConciergeOf(user);
    await load();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    ending.value = null;
  }
}

/** Local time: the reader is deciding whether this was them a moment ago. */
function when(value: string) {
  return new Date(value).toLocaleString();
}

function megabytes(bytes: number) {
  return `${Math.round(bytes / (1024 * 1024))} MB`;
}

onMounted(load);

defineExpose({ load });
</script>

<template>
  <div class="concierge-sessions">
    <div class="text-subtitle2 q-mb-xs">Running sessions</div>

    <q-banner v-if="error" dense class="os-bg-tint-error text-negative">
      <template #avatar><q-icon name="error" /></template>
      {{ error }}
    </q-banner>

    <div v-else-if="!loading && sessions.length === 0" class="concierge-sessions-none text-caption os-text-muted">
      No Concierge session is running.
    </div>

    <q-markup-table v-else flat dense class="concierge-sessions-table">
      <thead>
        <tr>
          <th class="text-left">Person</th>
          <th class="text-left">Worker</th>
          <th class="text-left">Viewer</th>
          <th class="text-left">Last activity</th>
          <th class="text-left">Would end at</th>
          <th class="text-left">Memory</th>
          <th />
        </tr>
      </thead>
      <tbody>
        <tr v-for="session in sessions" :key="session.user" class="concierge-session">
          <td>{{ session.email ?? session.user }}</td>
          <td>{{ session.worker ?? '—' }}</td>
          <td class="concierge-session-viewer">
            <template v-if="session.viewer">open now</template>
            <template v-else-if="session.lastViewerAt">left {{ when(session.lastViewerAt) }}</template>
          </td>
          <td class="concierge-session-activity">
            {{ session.lastActivity }}, {{ when(session.lastActivityAt) }}
            <template v-if="session.callsInFlight > 0"> ({{ session.callsInFlight }} call(s) in flight)</template>
          </td>
          <td class="concierge-session-ends">
            <template v-if="session.wouldEndAt">{{ when(session.wouldEndAt) }}</template>
            <template v-else>not while open</template>
          </td>
          <td class="concierge-session-memory">
            {{ session.memory ? megabytes(session.memory.residentBytes) : 'not measured' }}
          </td>
          <td class="text-right">
            <q-btn
              flat
              dense
              no-caps
              color="negative"
              label="End"
              class="concierge-session-end"
              :loading="ending === session.user"
              @click="end(session.user)"
            />
          </td>
        </tr>
      </tbody>
    </q-markup-table>

    <div class="text-caption os-text-muted q-mt-xs">
      A session is ended only when nobody has had it open and it has done nothing - no output above
      its floor, no keystrokes, no platform call - for the idle window below.
    </div>
  </div>
</template>
