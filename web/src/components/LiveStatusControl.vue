<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue';
import { storeToRefs } from 'pinia';
import { useConsoleStore } from '../stores/console';
import { serviceStatus } from '../api/client';
import type { ServiceStatus } from '../api/types';
import { localStamp, wireInstant } from '../lib/localTime';
import {
  LiveIcons,
  ReadEveryMs,
  checkWords,
  connectionWords,
  liveWord,
  upWords,
  workerWords,
} from '../lib/serviceStatus';
import FieldHelp from './FieldHelp.vue';

/**
 * THE LIVE CHIP ON THE APP BAR, as a status control: green with a radiating dot while this window's
 * hub connection is up, grey and struck through when it is not, the word beside it while the row has
 * room (`word`, from `lib/headerFit.ts`) and in its tooltip otherwise. A click opens what the service
 * says of itself (`GET /api/service-status`): how long the instance has been up, whether it can serve,
 * which build, which workers - beside this window's own connection, which is a different fact.
 *
 * The chip's colour is the CONNECTION alone. A healthy instance this window has lost still reads
 * offline, because what is on this screen may be stale and that is what the person needs to know.
 * The service is read when the dropdown opens and every 30 s while it is open; never on a poll in
 * the background, since the hub's own connection already says what matters at a glance.
 */
const props = withDefaults(defineProps<{ word?: boolean }>(), { word: true });

const board = useConsoleStore();
const { connected, connectionEstablished, connectionChangedAt } = storeToRefs(board);

const word = computed(() => liveWord(connected.value));
const icon = computed(() => (connected.value ? LiveIcons.live : LiveIcons.offline));

const status = ref<ServiceStatus | null>(null);
const error = ref('');
const readAt = ref<number | null>(null);
const reading = ref(false);
let timer: ReturnType<typeof setInterval> | 0 = 0;

async function read() {
  if (reading.value) return;
  reading.value = true;
  try {
    status.value = await serviceStatus();
    error.value = '';
    readAt.value = Date.now();
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    reading.value = false;
  }
}

function opened() {
  void read();
  if (!timer) timer = setInterval(() => void read(), ReadEveryMs);
}

function closed() {
  if (timer) clearInterval(timer);
  timer = 0;
}

onUnmounted(closed);

const stamp = (iso: string | null) => {
  if (!iso) return '';
  const at = wireInstant(iso);
  return Number.isNaN(at) ? '' : localStamp(at);
};

const connectionLine = computed(() =>
  connectionWords(
    connected.value,
    connectionEstablished.value,
    connectionChangedAt.value === null ? null : localStamp(connectionChangedAt.value),
  ));

const up = computed(() => (status.value ? upWords(status.value.startedAt, status.value.now) : ''));

const HelpText =
  'Live means this window is receiving updates from the instance now; offline means it is not, and what is ' +
  'on screen may be out of date. Below it is the instance\'s own state: how long it has been up, the three ' +
  'checks its health probe makes, which build it is and which workers it has, read when this opens.';
</script>

<template>
  <q-btn
    flat
    dense
    no-caps
    class="live-chip"
    :class="{ 'live-chip-on': connected, 'live-chip-dot': !props.word }"
    :icon="icon"
    :label="props.word ? word : undefined"
    :aria-label="word"
    data-test="live-chip"
    :data-state="word"
  >
    <q-tooltip v-if="!props.word">{{ word }}</q-tooltip>

    <q-menu anchor="bottom right" self="top right" class="above-concierge" @before-show="opened" @hide="closed">
      <div class="live-status-panel q-pa-sm" data-test="live-status-panel">
        <div class="row items-center no-wrap q-mb-xs">
          <div class="text-subtitle2">Status</div>
          <FieldHelp class="q-ml-xs" :text="HelpText" />
        </div>

        <div class="row items-center no-wrap q-mb-sm" data-test="live-status-connection">
          <q-icon :name="icon" size="18px" class="q-mr-xs" :class="connected ? 'live-ink' : 'os-text-muted'" />
          <div class="text-body2">{{ connectionLine }}</div>
        </div>

        <div v-if="!status && !error" class="text-caption os-text-muted" data-test="live-status-reading">
          Reading the instance's state…
        </div>
        <div v-if="error" class="text-caption os-text-muted q-mb-xs" data-test="live-status-error">
          Not read: {{ error }}
        </div>

        <template v-if="status">
          <section class="q-mb-sm" data-test="live-status-instance">
            <div class="live-status-heading text-overline">Instance</div>
            <div class="text-body2">
              Up {{ up }}<span v-if="status.startedAt" class="os-text-muted"> · since {{ stamp(status.startedAt) }}</span>
            </div>
            <div class="text-caption os-text-muted">{{ status.build.version }}</div>
          </section>

          <section class="q-mb-sm" data-test="live-status-checks">
            <div class="live-status-heading text-overline">
              Health · {{ status.status }}
            </div>
            <div
              v-for="check in status.checks"
              :key="check.name"
              class="row items-center no-wrap text-body2"
              data-test="live-status-check"
              :data-ok="check.ok"
            >
              <q-icon
                :name="check.ok ? 'check_circle' : 'cancel'"
                size="16px"
                class="q-mr-xs"
                :class="check.ok ? 'live-ink' : 'live-ink-failed'"
              />
              <span>{{ checkWords(check) }}</span>
            </div>
            <div v-if="status.pumpLastPass" class="text-caption os-text-muted">
              Last delivery pass {{ stamp(status.pumpLastPass) }}
            </div>
          </section>

          <section class="q-mb-xs" data-test="live-status-workers">
            <div class="live-status-heading text-overline">Workers</div>
            <div v-if="status.workers.length === 0" class="text-body2 os-text-muted">
              No worker is connected: runs wait for one.
            </div>
            <div
              v-for="worker in status.workers"
              :key="worker.id"
              class="row items-center no-wrap text-body2"
              data-test="live-status-worker"
              :data-state="worker.state"
            >
              <q-icon
                :name="worker.state === 'connected' ? 'check_circle' : 'cancel'"
                size="16px"
                class="q-mr-xs"
                :class="worker.state === 'connected' ? 'live-ink' : 'live-ink-failed'"
              />
              <span>{{ workerWords(worker, stamp(worker.connectedSince), stamp(worker.droppedAt)) }}</span>
            </div>
          </section>
        </template>

        <div class="row items-center no-wrap q-mt-xs">
          <div class="text-caption os-text-muted" data-test="live-status-read-at">
            {{ readAt === null ? 'Not read yet' : `Read ${localStamp(readAt)}` }}
          </div>
          <q-space />
          <q-btn flat dense no-caps size="sm" label="Read again" :loading="reading" data-test="live-status-refresh" @click="read" />
        </div>
      </div>
    </q-menu>
  </q-btn>
</template>

<style scoped>
/* GREEN WHILE LIVE, GREY WHEN NOT: the running state's token, which every theme holds at 3:1 on the
   chrome, and the muted ink. The word, when shown, takes the same colour as the icon. On the button's
   CONTENT, not its root: the ribbon's own rule gives every button in it `color: inherit` at a higher
   specificity than a class here, and the content is below that rule's reach. */
.live-chip {
  gap: 4px;
}

.live-chip :deep(.q-btn__content) {
  color: var(--os-ink-muted);
}

.live-chip-on :deep(.q-btn__content) {
  color: var(--os-state-running);
}

/* Short of room: the icon alone, the word in its tooltip. */
.live-chip-dot {
  padding: 0 4px;
  min-width: 0;
}

.live-ink {
  color: var(--os-state-running);
}

.live-ink-failed {
  color: var(--os-state-failed);
}

.live-status-panel {
  width: 24rem;
  max-width: 92vw;
}

.live-status-heading {
  color: var(--os-ink-muted);
  line-height: 1.6;
}
</style>
