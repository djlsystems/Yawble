<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { LinkStatus, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// The Cloud Link (optional): attach this computer to yawble.ai (or another cloud site the person chooses) so they can
// see and drive it from a browser elsewhere. The app works the same without it. Pairing: the app gets a code from the
// site and opens the site's pair page in the person's browser with the code filled in; there they sign in (or create
// an account), choose a workgroup and attach. Nothing is pasted into the app.
const props = defineProps<{ api?: YawbleApi; inline?: boolean }>();
const emit = defineEmits<{ close: []; changed: [status: LinkStatus] }>();
const api = resolveApi(props.api);

const status = ref<LinkStatus | null>(null);
const url = ref('');
const error = ref<string | null>(null);
const busy = ref(false);
/** The method running (link.pair, link.cancel, link.forget, ...): its button shows the spinner. */
const doing = ref<string | null>(null);
// The pairing view stays up from Pair until the person closes it with Done or Cancel, through connecting and connected.
const pairing = ref(false);
let off: (() => void) | null = null;

const attached = computed(() => !!status.value?.instanceId);
const showPairing = computed(() => pairing.value || status.value?.state === 'pairing');
const host = computed(() => {
  try { return new URL(status.value?.url || url.value).host; } catch { return 'the cloud'; }
});

function set(s: LinkStatus) {
  status.value = s;
  if (!url.value) url.value = s.url ?? s.defaultUrl ?? '';
  emit('changed', s);
}

async function run(method: string, params?: Record<string, unknown>): Promise<LinkStatus | null> {
  if (busy.value) return null;
  error.value = null;
  busy.value = true;
  doing.value = method;
  try {
    const s = await api.call<LinkStatus>(method, params);
    set(s);
    return s;
  } catch (e) {
    error.value = errorText(e);
    return null;
  } finally {
    busy.value = false;
    doing.value = null;
  }
}

function openSite() {
  // The main process opens web addresses in the person's browser, never in the app.
  if (status.value?.pairUrl) window.open(status.value.pairUrl, '_blank');
}

async function pair() {
  if (busy.value) return;
  pairing.value = true;
  const s = await run('link.pair', { url: url.value });
  if (s?.pairUrl) openSite();
}

async function cancel() {
  await run('link.cancel');
  pairing.value = false;
}

onMounted(async () => {
  off = api.onEvent((e) => { if (e.event === 'link.state') set(e.data as LinkStatus); });
  await run('link.status');
});
onBeforeUnmount(() => off?.());

const stateText = computed(() => {
  switch (status.value?.state) {
    case 'connected': return 'Connected';
    case 'connecting': return 'Connecting…';
    case 'offline': return 'Not connected (retrying)';
    case 'pairing': return 'Pairing';
    default: return 'Not attached';
  }
});

// Where the pairing has got to, in the person's words.
const phase = computed((): { key: string; text: string; detail: string } => {
  const s = status.value;
  if (error.value) return { key: 'failed', text: 'Could not start pairing', detail: error.value };
  if (busy.value && !s?.code) return { key: 'pairing', text: 'Asking the cloud for a code…', detail: '' };
  switch (s?.state) {
    case 'pairing':
      return s.error
        ? { key: 'offline', text: 'Cannot reach the cloud, still trying', detail: s.error }
        : { key: 'pairing', text: 'Waiting for the cloud connection',
            detail: `A browser window opened at ${host.value}. Sign in or create an account, choose a workgroup and attach this computer. The code works for 10 minutes.` };
    case 'connecting': return { key: 'connecting', text: 'Attached. Connecting to the cloud…', detail: '' };
    case 'offline': return { key: 'offline', text: 'Attached, not connected yet (retrying)', detail: s.error ?? '' };
    case 'connected':
      return { key: 'connected', text: 'Connected',
        detail: `This computer is ${s.instanceName ?? 'attached'}${s.workgroup ? ` in ${s.org ? `${s.org} › ` : ''}${s.workgroup}` : ''}. You can close this.` };
    default: return { key: 'failed', text: 'Pairing stopped', detail: s?.error ?? 'Pair again for a new code.' };
  }
});
</script>

<template>
  <Modal :inline="inline" :title="showPairing ? `Pair with ${host}` : 'Cloud Link'" @close="emit('close')">
    <!-- Pairing: the code, where it has got to, and the way back to the browser. -->
    <div v-if="showPairing && status" class="pairing" data-test="link-pairing">
      <template v-if="!attached">
        <p class="muted small">This computer's pairing code</p>
        <div class="code" data-test="link-code">{{ status.code ?? '····-····' }}</div>
      </template>
      <div class="row phase">
        <span class="dot" :class="phase.key" />
        <strong data-test="link-phase" :data-phase="phase.key">{{ phase.text }}</strong>
      </div>
      <p v-if="phase.detail" class="muted small detail" data-test="link-phase-detail">{{ phase.detail }}</p>
      <div class="row buttons">
        <template v-if="status.state === 'pairing'">
          <button data-test="link-open-site" :disabled="!status.pairUrl" @click="openSite">Open the browser again</button>
          <button data-test="link-cancel" :disabled="busy" @click="cancel"><BusyLabel :busy="doing === 'link.cancel'" text="Cancelling…">Cancel</BusyLabel></button>
        </template>
        <button v-else-if="attached" class="primary" data-test="link-done" @click="pairing = false">Done</button>
        <template v-else>
          <button class="primary" data-test="link-pair-again" :disabled="busy" @click="pair"><BusyLabel :busy="doing === 'link.pair'" text="Pairing…">Pair again</BusyLabel></button>
          <button data-test="link-back" @click="pairing = false; error = null">Back</button>
        </template>
      </div>
    </div>

    <template v-else>
      <p class="muted intro">
        Attach this computer to yawble.ai so you (and your team) can see and control it from a browser.
      </p>
      <p v-if="error" class="error" data-test="link-error">{{ error }}</p>
      <LoadingNote v-if="!status && !error" text="Loading the Cloud Link…" />

      <template v-if="status">
        <div class="row state">
          <span class="dot" :class="status.state" />
          <strong data-test="link-state">{{ stateText }}</strong>
          <span v-if="status.error" class="muted small" data-test="link-detail">· {{ status.error }}</span>
        </div>

        <!-- Not attached: the site's address (yawble.ai unless the person changes it), then Pair. -->
        <template v-if="!attached">
          <label class="field">
            <span>Cloud site address</span>
            <input v-model="url" data-test="link-url" :placeholder="status.defaultUrl ?? 'https://cloud.yawble.ai/'" spellcheck="false" />
          </label>
          <div class="row">
            <button class="primary" data-test="link-connect" :disabled="busy || !url.trim()" @click="pair"><BusyLabel :busy="doing === 'link.pair'" text="Pairing…">Pair</BusyLabel></button>
          </div>
        </template>

        <!-- Attached. -->
        <template v-else>
          <dl class="facts">
            <dt>Site</dt><dd data-test="link-site"><a :href="status.url ?? undefined" target="_blank" rel="noopener" title="Open in your browser">{{ status.url }}</a></dd>
            <dt>Shown there as</dt><dd data-test="link-name">{{ status.instanceName }}</dd>
            <dt>Organization</dt><dd data-test="link-org">{{ status.org || '—' }}</dd>
            <dt>Group</dt><dd data-test="link-workgroup">{{ status.workgroup || '—' }}</dd>
            <dt>Viewing now</dt>
            <dd data-test="link-viewers">{{ status.viewers.length ? status.viewers.join(', ') : 'nobody' }}</dd>
          </dl>
          <!-- The Code browser over the link: on unless turned off. Only when attached: the core keeps
               this setting with the link's settings and refuses it (bad_state) while there are none. -->
          <label class="check">
            <input type="checkbox" data-test="link-show-code" :checked="status.showCode !== false" :disabled="busy"
              @change="run('link.setShowCode', { show: ($event.target as HTMLInputElement).checked })" />
            <span>Show members' code to cloud viewers <span class="muted small">(the read-only Code browser of each member's working copy; files that may hold secrets are never shown)</span></span>
          </label>
          <label class="check">
            <input type="checkbox" data-test="link-allow-host" :checked="status.allowHostTerminals" :disabled="busy"
              @change="run('link.setAllowHostTerminals', { allow: ($event.target as HTMLInputElement).checked })" />
            <span>Allow host terminals from the site <span class="muted small">(a shell on this computer, outside any sandbox; used to sign in to agent CLIs remotely)</span></span>
          </label>
          <p class="muted small">Remote viewers can do what you can do in this window, except change this link, enter credentials and, unless host terminals are allowed, add MCP servers or define host tasks.
            What they change is recorded in the Cloud Link audit log in the app's data folder.</p>
          <div class="row">
            <button class="danger" data-test="link-forget" :disabled="busy" @click="run('link.forget')"><BusyLabel :busy="doing === 'link.forget'" text="Detaching…">Detach this computer</BusyLabel></button>
          </div>
        </template>
      </template>
    </template>
  </Modal>
</template>

<style scoped>
.intro { margin-top: 0; }
.state { margin: 6px 0 12px; }
.dot { width: 10px; height: 10px; border-radius: 50%; background: #aab; display: inline-block; flex: none; }
.dot.connected { background: #2e9d4f; }
.dot.connecting, .dot.pairing { background: #e0a400; }
.dot.pairing { animation: pulse 1.4s ease-in-out infinite; }
.dot.offline, .dot.failed { background: #c9473d; }
@keyframes pulse { 50% { opacity: .35; } }
.field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 10px; }
.field input { font-size: 14px; padding: 6px 8px; }
.pairing { text-align: center; padding: 4px 0 2px; }
.pairing p { margin: 0; }
.code { font: 600 34px/1.2 ui-monospace, Consolas, monospace; letter-spacing: .12em; padding: 10px 18px; background: #f1f4fa; border-radius: 8px; margin: 6px auto 14px; display: inline-block; }
.phase { justify-content: center; gap: 8px; }
.detail { max-width: 460px; margin: 6px auto 0 !important; }
.buttons { justify-content: center; gap: 8px; margin-top: 16px; }
.facts { display: grid; grid-template-columns: 140px 1fr; gap: 4px 12px; margin: 0 0 12px; }
.facts dt { color: var(--muted); }
.facts dd { margin: 0; }
.facts a { color: var(--accent); text-decoration: none; }
.facts a:hover { text-decoration: underline; }
.check { display: flex; gap: 8px; align-items: flex-start; margin: 8px 0; }
.small { font-size: 12px; }
.danger { color: #b3261e; }
</style>
