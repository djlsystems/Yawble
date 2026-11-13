<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { CredentialStatus, YawbleApi, TerminalSessionInfo } from '../../../shared/api';
import { errorText, resolveApi, when } from '../api';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import TerminalView from './TerminalView.vue';

// Settings → Model credentials. Values go in once and never come back: the core
// answers only "connected", dates and the switch position.
const props = defineProps<{ api?: YawbleApi }>();
const emit = defineEmits<{ close: [] }>();
const api = resolveApi(props.api);

const status = ref<CredentialStatus | null>(null);
const error = ref<string | null>(null);
const pane = ref<null | 'claude-setup-token' | 'grok-login'>(null);
const paneSession = ref<TerminalSessionInfo | null>(null);
const setupToken = ref('');
const anthropicKey = ref('');
const xaiKey = ref('');

/** The button whose action is running (save-setup-token, remove:<which>, mode:<cli>, ...): it shows the spinner. */
const doing = ref<string | null>(null);
async function run(work: () => Promise<CredentialStatus | unknown>, label: string | null = null) {
  if (label && doing.value) return;
  error.value = null;
  doing.value = label;
  try {
    const result = await work();
    if (result && typeof result === 'object' && 'claude' in result) status.value = result as CredentialStatus;
    else status.value = await api.call<CredentialStatus>('credentials.status');
  } catch (e) {
    error.value = errorText(e);
  } finally {
    if (label) doing.value = null;
  }
}

onMounted(() => run(() => api.call('credentials.status')));

async function startPane(kind: 'claude-setup-token' | 'grok-login') {
  pane.value = kind;
  paneSession.value = null;
  await run(() => api.call('hostTerminal.start', { kind }));
}

async function onPaneSession(s: TerminalSessionInfo | null) {
  paneSession.value = s;
  if (s?.ended && pane.value === 'grok-login') await run(() => api.call('credentials.status'));
}

const saveSetupToken = () => run(async () => {
  const r = await api.call('credentials.setClaudeSetupToken', { token: setupToken.value.trim() });
  setupToken.value = '';
  pane.value = null;
  return r;
}, 'setup-token');
const saveAnthropicKey = () => run(async () => {
  const r = await api.call('credentials.setAnthropicApiKey', { key: anthropicKey.value.trim() });
  anthropicKey.value = '';
  return r;
}, 'anthropic-key');
const saveXaiKey = () => run(async () => {
  const r = await api.call('credentials.setXaiApiKey', { key: xaiKey.value.trim() });
  xaiKey.value = '';
  return r;
}, 'xai-key');
const setMode = (cli: 'claude' | 'grok', mode: string) => run(() => api.call('credentials.setMode', { cli, mode }), `mode:${cli}`);
const remove = (which: string) => run(() => api.call('credentials.remove', { which }), `remove:${which}`);
</script>

<template>
  <Modal title="Settings → Model credentials" wide @close="emit('close')">
    <p class="warn" data-test="cred-host-sign-in-note">
      Agents use each CLI's own sign-in on this computer: sign in to <code>claude</code> and <code>grok</code> in your own
      terminal, and the Agents dialog shows whether each is signed in. Nothing stored on this screen is used by agent runs.
    </p>
    <p v-if="status?.implementation === 'fake'" class="fake-note" data-test="cred-fake-note">
      <span class="chip fake">FAKE</span> {{ status.storageWarning }}
    </p>
    <p v-else-if="status?.storageWarning" class="warn" data-test="cred-storage-warning">{{ status.storageWarning }}</p>
    <p v-else-if="status" class="muted small" data-test="cred-storage">Stored in this computer's {{ status.storage === 'keychain' ? 'system keychain' : status.storage }}.</p>
    <p v-if="error" class="error" data-test="cred-error">{{ error }}</p>
    <LoadingNote v-if="!status && !error" text="Loading the credentials…" />

    <div v-if="status" class="grid">
      <!-- Claude Code -->
      <section data-test="claude-section">
        <h3>Claude Code</h3>
        <div class="switch" role="radiogroup" aria-label="Claude credential">
          <label><input type="radio" name="claude-mode" value="subscription" data-test="claude-mode-subscription"
            :checked="status.claude.mode === 'subscription'" @change="setMode('claude', 'subscription')" /> Subscription (setup-token)</label>
          <label><input type="radio" name="claude-mode" value="apiKey" data-test="claude-mode-apiKey"
            :checked="status.claude.mode === 'apiKey'" @change="setMode('claude', 'apiKey')" /> Anthropic API key</label>
        </div>
        <p class="muted small" data-test="claude-mode">Runs use: <strong>{{ status.claude.mode === 'subscription' ? 'the subscription' : 'the API key' }}</strong></p>

        <h4>Claude subscription</h4>
        <p data-test="claude-sub-status">
          <template v-if="status.claude.subscription.connected">
            <span class="ok">Connected</span> · added {{ when(status.claude.subscription.addedAt) }}
            · expires {{ when(status.claude.subscription.expiresAt) }}<template v-if="status.claude.subscription.lastUsedAt"> · last used {{ when(status.claude.subscription.lastUsedAt) }}</template>
          </template>
          <span v-else class="muted">Not connected</span>
        </p>
        <p v-if="status.claude.subscription.renewSoon" class="warn" data-test="claude-renew">
          The setup-token expires within 30 days. Run <code>claude setup-token</code> again (Connect Claude subscription) and paste the new token. The app never renews it by itself.
        </p>
        <div class="row">
          <button data-test="connect-claude" :disabled="pane === 'claude-setup-token' && !paneSession?.ended" @click="startPane('claude-setup-token')">
            Connect Claude subscription
          </button>
          <button v-if="status.claude.subscription.connected" class="danger" data-test="remove-claude-sub" :disabled="!!doing" @click="remove('claudeSubscription')"><BusyLabel :busy="doing === 'remove:claudeSubscription'" text="Removing…">Remove</BusyLabel></button>
        </div>
        <div v-if="pane === 'claude-setup-token'" class="pane">
          <p class="muted small">Host terminal pane, <strong>unsandboxed</strong>: <code>claude setup-token</code> signs in with Anthropic in your browser and prints a token.</p>
          <TerminalView channel="host:claude-setup-token" :api="api" :rows="8" @session="onPaneSession" />
          <div class="row paste">
            <input v-model="setupToken" class="grow" type="password" autocomplete="off" spellcheck="false"
              placeholder="Paste the token claude setup-token printed" data-test="setup-token-input" />
            <button class="primary" data-test="save-setup-token" :disabled="!setupToken.trim() || !!doing" @click="saveSetupToken"><BusyLabel :busy="doing === 'setup-token'" text="Saving…">Save token</BusyLabel></button>
          </div>
        </div>

        <h4>Anthropic API key</h4>
        <p data-test="anthropic-key-status">
          <template v-if="status.claude.apiKey.connected"><span class="ok">Saved</span><template v-if="status.claude.apiKey.addedAt"> · added {{ when(status.claude.apiKey.addedAt) }}</template><template v-if="status.claude.apiKey.lastUsedAt"> · last used {{ when(status.claude.apiKey.lastUsedAt) }}</template></template>
          <span v-else class="muted">No key</span>
        </p>
        <div class="row">
          <input v-model="anthropicKey" class="grow" type="password" autocomplete="off" placeholder="Anthropic Console API key" data-test="anthropic-key-input" />
          <button data-test="save-anthropic-key" :disabled="!anthropicKey.trim() || !!doing" @click="saveAnthropicKey"><BusyLabel :busy="doing === 'anthropic-key'" text="Saving…">Save key</BusyLabel></button>
          <button v-if="status.claude.apiKey.connected" class="danger" :disabled="!!doing" @click="remove('anthropicApiKey')"><BusyLabel :busy="doing === 'remove:anthropicApiKey'" text="Removing…">Remove</BusyLabel></button>
        </div>
      </section>

      <!-- Grok -->
      <section data-test="grok-section">
        <h3>Grok</h3>
        <div class="switch" role="radiogroup" aria-label="Grok credential">
          <label><input type="radio" name="grok-mode" value="login" data-test="grok-mode-login"
            :checked="status.grok.mode === 'login'" @change="setMode('grok', 'login')" /> My Grok login</label>
          <label><input type="radio" name="grok-mode" value="apiKey" data-test="grok-mode-apiKey"
            :checked="status.grok.mode === 'apiKey'" @change="setMode('grok', 'apiKey')" /> xAI API key</label>
        </div>
        <p class="muted small" data-test="grok-mode">Runs use: <strong>{{ status.grok.mode === 'login' ? 'your Grok login' : 'the xAI API key' }}</strong></p>

        <h4>Grok login</h4>
        <p data-test="grok-login-status">
          <template v-if="status.grok.login.signedIn">
            <span class="ok">Signed in</span> · session expires {{ when(status.grok.login.expiresAt) }} · last refresh {{ when(status.grok.login.lastRefreshAt) }}
            <span v-if="status.grok.login.problem" class="warn" data-test="grok-login-problem"> · {{ status.grok.login.problem }}</span>
          </template>
          <span v-else class="muted">Not signed in</span>
        </p>
        <div class="row">
          <button data-test="grok-sign-in" :disabled="pane === 'grok-login' && !paneSession?.ended" @click="startPane('grok-login')">Sign in to Grok</button>
          <button v-if="status.grok.login.signedIn" class="danger" :disabled="!!doing" @click="remove('grokLogin')"><BusyLabel :busy="doing === 'remove:grokLogin'" text="Signing out…">Sign out</BusyLabel></button>
        </div>
        <div v-if="pane === 'grok-login'" class="pane">
          <p class="muted small">Host terminal pane, <strong>unsandboxed</strong>: xAI's own <code>grok login</code> against the app's private Grok home. The app's Grok keeper refreshes the session there.</p>
          <TerminalView channel="host:grok-login" :api="api" :rows="8" @session="onPaneSession" />
        </div>

        <h4>xAI API key</h4>
        <p data-test="xai-key-status">
          <template v-if="status.grok.apiKey.connected"><span class="ok">Saved</span><template v-if="status.grok.apiKey.addedAt"> · added {{ when(status.grok.apiKey.addedAt) }}</template><template v-if="status.grok.apiKey.lastUsedAt"> · last used {{ when(status.grok.apiKey.lastUsedAt) }}</template></template>
          <span v-else class="muted">No key</span>
        </p>
        <div class="row">
          <input v-model="xaiKey" class="grow" type="password" autocomplete="off" placeholder="xAI API key" data-test="xai-key-input" />
          <button data-test="save-xai-key" :disabled="!xaiKey.trim() || !!doing" @click="saveXaiKey"><BusyLabel :busy="doing === 'xai-key'" text="Saving…">Save key</BusyLabel></button>
          <button v-if="status.grok.apiKey.connected" class="danger" :disabled="!!doing" @click="remove('xaiApiKey')"><BusyLabel :busy="doing === 'remove:xaiApiKey'" text="Removing…">Remove</BusyLabel></button>
        </div>
      </section>
    </div>
    <p class="muted small">No credential ever enters a sandbox: each run gets a run token, and the app's gateway swaps in the real credential. Each agent can follow these switches or pick a mode in its own settings.</p>
  </Modal>
</template>

<style scoped>
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
section { border: 1px solid var(--line); border-radius: 6px; padding: 8px 12px 12px; }
h3 { margin: 4px 0 8px; font-size: 15px; }
h4 { margin: 14px 0 4px; font-size: 13px; }
.switch { display: flex; gap: 14px; background: #f1f3f6; padding: 6px 8px; border-radius: 6px; }
.small { font-size: 12px; }
.pane { margin-top: 8px; height: auto; }
.pane :deep(.terminal-view) { height: 160px; }
.paste { margin-top: 6px; }
.fake-note { background: #fff4f4; border: 1px solid #f3c9c9; padding: 6px 10px; border-radius: 6px; }
p { margin: 4px 0; }
</style>
