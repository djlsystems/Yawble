<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import type { AgentModelList, AgentProfileRow, AvailableBackend, CommandLines, CredentialStatus, MemberPolicyView, MemberRow, YawbleApi, PolicyPatchDoc } from '../../../shared/api';
import { errorText, resolveApi, when } from '../api';
import { CLIPBOARD, effectiveOf, fromText, getPath, POLICY_FIELDS, setPath, show, testId, toText } from '../policyFields';
import { agentClis } from '../agentClis';
import { agentDefaultLabel } from '../agentArgs';
import GuardrailPanel from './GuardrailPanel.vue';
import TeamNetworkAllows from './TeamNetworkAllows.vue';
import Modal from './Modal.vue';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

// ⚙ Settings: General (CLI preset, model, credential, instructions) and Policy (global, override
// and effective per field, an "inherit" toggle, and the separate Loosen… section with its confirmation), and
// Guardrails (the guardrail check under this agent's effective policy; the card's 🛡 opens it directly).
const props = defineProps<{ memberId: number; initialTab?: 'general' | 'policy' | 'guardrails'; api?: YawbleApi; pollMs?: number }>();
const emit = defineEmits<{ close: []; changed: [] }>();
const api = resolveApi(props.api);

const tab = ref<'general' | 'policy' | 'guardrails'>(props.initialTab ?? 'general');
const member = ref<MemberRow | null>(null);
const view = ref<MemberPolicyView | null>(null);
const creds = ref<CredentialStatus | null>(null);
const backends = ref<AvailableBackend[]>([]);
const error = ref<string | null>(null);
const saved = ref<string | null>(null);
// The Save / Preview / Confirm buttons: one at a time, the clicked one shows the spinner.
const work = useBusy();
const busy = computed(() => work.any());

const general = reactive({ cli: 'claude', model: '', credential: 'default', instructions: '' });
const rows = reactive<Record<string, { inherit: boolean; text: string }>>({});
const loosenText = reactive<Record<string, string>>({});
const preview = ref<string[] | null>(null);
// The agents one can choose (built-ins then custom), the picked agent's default model and known models, and a live command line.
const agentChoices = ref<AgentProfileRow[]>([]);
const modelList = ref<string[]>([]);
const cmdLines = ref<CommandLines | null>(null);
const CUSTOM_MODEL = '__custom__';
const modelPick = ref('');
const agentOptions = computed(() => agentChoices.value.length
  ? agentChoices.value.map((a) => ({ id: a.name, label: a.custom ? `${a.name} (my agent)` : (agentClis.find((c) => c.id === a.name)?.label ?? a.name) }))
  : agentClis.map((c) => ({ id: c.id as string, label: c.label as string })));
const agentDefault = computed(() => agentChoices.value.find((a) => a.name === general.cli)?.defaultModel ?? '');
const baseCli = computed(() => agentChoices.value.find((a) => a.name === general.cli)?.baseCli ?? general.cli);

function syncPick() {
  modelPick.value = !general.model ? '' : modelList.value.includes(general.model) ? general.model : CUSTOM_MODEL;
}
async function refreshAgentInfo() {
  try {
    const l = await api.call<AgentModelList>('agents.models', { cli: baseCli.value });
    modelList.value = l.models;
  } catch { modelList.value = []; }
  syncPick();
  await refreshPreview();
}
// The picked agent itself (a custom agent by its name): its own arguments and default model, not its base CLI's.
async function refreshPreview() {
  try { cmdLines.value = await api.call<CommandLines>('agents.commandLine', { cli: general.cli, model: general.model || undefined }); }
  catch { cmdLines.value = null; }
}
function onModelPick() {
  if (modelPick.value !== CUSTOM_MODEL) general.model = modelPick.value;
  else if (modelList.value.includes(general.model)) general.model = '';
  void refreshPreview();
}
async function onAgentChange() {
  general.credential = 'default';
  await refreshAgentInfo();
}
const previewFor = ref<string>('');

const credentialOptions = computed(() => {
  if (general.cli !== 'claude' && general.cli !== 'grok') return [['default', 'Its own sign-in on this computer']];
  const appMode = general.cli === 'claude'
    ? (creds.value?.claude.mode === 'apiKey' ? 'Anthropic API key' : 'subscription')
    : (creds.value?.grok.mode === 'apiKey' ? 'xAI API key' : 'Grok login');
  return general.cli === 'claude'
    ? [['default', `App switch (now: ${appMode})`], ['subscription', 'Claude subscription'], ['apiKey', 'Anthropic API key']]
    : [['default', `App switch (now: ${appMode})`], ['login', 'My Grok login'], ['apiKey', 'xAI API key']];
});

async function load() {
  error.value = null;
  try {
    const [m, v, c] = await Promise.all([
      api.call<MemberRow>('members.get', { memberId: props.memberId }),
      api.call<MemberPolicyView>('policy.getMember', { memberId: props.memberId }),
      api.call<CredentialStatus>('credentials.status'),
    ]);
    member.value = m;
    creds.value = c;
    Object.assign(general, { cli: m.cli, model: m.model, credential: m.credential, instructions: m.instructions });
    applyView(v);
    backends.value = await api.getAvailableBackends().catch(() => []);
    agentChoices.value = await api.call<AgentProfileRow[]>('agentProfiles.list').catch(() => []);
    await refreshAgentInfo();
  } catch (e) {
    error.value = errorText(e);
  }
}

function applyView(v: MemberPolicyView) {
  view.value = v;
  for (const f of POLICY_FIELDS) {
    const o = getPath(v.override, f.path);
    // An inherited select still shows a value: the global one.
    const shown = o === undefined && f.kind === 'clipboard' ? getPath(v.global, f.path) : o;
    rows[f.path] = { inherit: o === undefined, text: toText(shown) };
    if (f.loosen) loosenText[f.path] = toText(getPath(v.loosen, f.path));
  }
  preview.value = null;
}

onMounted(load);

// A team host removed beside the policy: read the effective policy again, keeping the override edits in progress.
async function teamHostsChanged() {
  try { view.value = await api.call<MemberPolicyView>('policy.getMember', { memberId: props.memberId }); } catch (e) { error.value = errorText(e); }
}

const saveGeneral = () => work.run('general', doSaveGeneral);
async function doSaveGeneral() {
  error.value = saved.value = null;
  try {
    member.value = await api.call<MemberRow>('members.update', { memberId: props.memberId, ...general });
    saved.value = 'Saved.';
    emit('changed');
  } catch (e) {
    error.value = errorText(e);
  }
}

function overrideDoc(): PolicyPatchDoc {
  const doc: any = {};
  for (const f of POLICY_FIELDS) {
    const r = rows[f.path];
    if (!r || r.inherit) continue;
    const value = f.kind === 'clipboard' ? (r.text || 'none') : fromText(f, r.text);
    if (value !== undefined) setPath(doc, f.path, value);
  }
  return doc;
}

function loosenDoc(): PolicyPatchDoc {
  const doc: any = {};
  for (const f of POLICY_FIELDS.filter((f) => f.loosen)) {
    const value = fromText(f, loosenText[f.path] ?? '');
    if (value === undefined || (Array.isArray(value) && value.length === 0)) continue;
    setPath(doc, f.path, value);
  }
  return doc;
}

const saveOverride = () => work.run('override', doSaveOverride);
async function doSaveOverride() {
  error.value = saved.value = null;
  try {
    applyView(await api.call<MemberPolicyView>('policy.saveOverride', { memberId: props.memberId, override: overrideDoc() }));
    saved.value = 'Override saved. It applies from the next run; a running sandbox keeps its policy.';
    emit('changed');
  } catch (e) {
    error.value = errorText(e);
  }
}

const previewLoosen = () => work.run('preview', doPreviewLoosen);
async function doPreviewLoosen() {
  error.value = saved.value = null;
  try {
    const doc = loosenDoc();
    preview.value = (await api.call<{ loosened: string[] }>('policy.previewLoosen', { memberId: props.memberId, loosen: doc })).loosened;
    previewFor.value = JSON.stringify(doc);
  } catch (e) {
    error.value = errorText(e);
    preview.value = null;
  }
}

const confirmLoosen = () => work.run('loosen', doConfirmLoosen);
async function doConfirmLoosen() {
  error.value = saved.value = null;
  try {
    const doc = JSON.parse(previewFor.value);
    applyView(await api.call<MemberPolicyView>('policy.saveLoosen', { memberId: props.memberId, loosen: doc, confirmed: [...(preview.value ?? [])] }));
    saved.value = 'Loosening saved with your confirmation.';
    emit('changed');
  } catch (e) {
    error.value = errorText(e);
  }
}

const loosenChanged = () => { preview.value = null; };
</script>

<template>
  <Modal :title="member ? `${member.team} / ${member.name} · settings` : 'Agent settings'" wide @close="emit('close')">
    <nav class="tabs">
      <button :class="{ on: tab === 'general' }" data-test="tab-general" @click="tab = 'general'">General</button>
      <button :class="{ on: tab === 'policy' }" data-test="tab-policy" @click="tab = 'policy'">Policy</button>
      <button :class="{ on: tab === 'guardrails' }" data-test="tab-guardrails" @click="tab = 'guardrails'">Guardrails</button>
    </nav>
    <p v-if="error" class="error" data-test="settings-error">{{ error }}</p>
    <p v-if="saved" class="ok" data-test="settings-saved">{{ saved }}</p>
    <LoadingNote v-if="!member && !error" text="Loading the agent's settings…" />

    <!-- General -->
    <div v-if="tab === 'general' && member" class="general">
      <label>Name <input :value="member.name" disabled /></label>
      <label>Role <input :value="member.role" disabled /></label>
      <label>Agent CLI
        <select v-model="general.cli" data-test="member-cli" @change="onAgentChange">
          <option v-for="c in agentOptions" :key="c.id" :value="c.id">{{ c.label }}</option>
        </select>
      </label>
      <label>Model
        <select v-model="modelPick" data-test="member-model-pick" @change="onModelPick">
          <option value="">{{ agentDefaultLabel(agentDefault) }}</option>
          <option v-for="m in modelList" :key="m" :value="m">{{ m }}</option>
          <option :value="CUSTOM_MODEL">Custom…</option>
        </select>
        <input v-if="modelPick === CUSTOM_MODEL" v-model="general.model" placeholder="model name" data-test="member-model" @input="refreshPreview" />
      </label>
      <div v-if="cmdLines" class="wide cmdline" data-test="member-cmdline">
        <span class="muted small">Command line ({{ cmdLines.model.label }}):</span>
        <code class="mono small" data-test="member-cmdline-headless">{{ cmdLines.headless.argv.join(' ') }}</code>
        <code class="mono small" data-test="member-cmdline-interactive">{{ cmdLines.interactive.argv.join(' ') }}</code>
      </div>
      <label>Credential
        <select v-model="general.credential" data-test="member-credential">
          <option v-for="[value, label] in credentialOptions" :key="value" :value="value">{{ label }}</option>
        </select>
      </label>
      <label class="wide">Member instructions
        <textarea v-model="general.instructions" rows="6" data-test="member-instructions"></textarea>
      </label>
      <div class="row wide"><span class="grow"></span><button class="primary" data-test="save-general" :disabled="busy" :aria-busy="work.is('general')" @click="saveGeneral"><BusyLabel :busy="work.is('general')" text="Saving…">Save</BusyLabel></button></div>
    </div>

    <!-- Guardrails -->
    <GuardrailPanel v-if="tab === 'guardrails' && member" :team="member.team" :member="member.name" :api="api" :poll-ms="pollMs" />

    <!-- Policy -->
    <div v-if="tab === 'policy' && view" class="policy">
      <p class="muted">Backend: <strong>{{ view.backend }}</strong>. An override can only tighten the global policy; widening needs the Loosen… section below.</p>
      <p v-if="view.net" class="small" data-test="policy-net"><strong>Network:</strong> {{ view.net }}</p>
      <table data-test="policy-table">
        <thead><tr><th>Field</th><th>Global</th><th>Inherit</th><th>Override</th><th>Effective</th></tr></thead>
        <tbody>
          <tr v-for="f in POLICY_FIELDS" :key="f.path" :data-test="`row-${testId(f.path)}`">
            <td>{{ f.label }}<div v-if="f.hint" class="muted small">{{ f.hint }}</div></td>
            <td class="mono">{{ show(getPath(view.global, f.path)) }}</td>
            <td><input type="checkbox" v-model="rows[f.path].inherit" :data-test="`inherit-${testId(f.path)}`" /></td>
            <td>
              <select v-if="f.kind === 'clipboard'" v-model="rows[f.path].text" :disabled="rows[f.path].inherit" :data-test="`override-${testId(f.path)}`">
                <option v-for="c in CLIPBOARD" :key="c" :value="c">{{ c }}</option>
              </select>
              <textarea v-else-if="f.kind === 'list'" v-model="rows[f.path].text" rows="1" :disabled="rows[f.path].inherit"
                :placeholder="rows[f.path].inherit ? 'inherits' : 'one per line'" :data-test="`override-${testId(f.path)}`"></textarea>
              <input v-else v-model="rows[f.path].text" :disabled="rows[f.path].inherit" inputmode="numeric"
                :placeholder="rows[f.path].inherit ? 'inherits' : ''" :data-test="`override-${testId(f.path)}`" />
            </td>
            <td class="mono" :data-test="`effective-${testId(f.path)}`">{{ show(effectiveOf(view, f.path)) }}</td>
          </tr>
        </tbody>
      </table>
      <section v-if="member" class="team-allows" data-test="policy-team-hosts">
        <TeamNetworkAllows :team-id="member.teamId" :api="api" @changed="teamHostsChanged" />
      </section>
      <ul v-if="view.widening.length" class="warn small" data-test="policy-widening">
        <li v-for="w in view.widening" :key="w">Dropped at launch: {{ w }}</li>
      </ul>
      <ul v-if="view.violations.length" class="error small"><li v-for="w in view.violations" :key="w">{{ w }}</li></ul>
      <ul v-if="view.effective.notes.length" class="muted small"><li v-for="n in view.effective.notes" :key="n">{{ n }}</li></ul>
      <div class="row"><span class="grow muted small">Every save is compiled and dry-run through the sandbox; its errors appear as they are.</span>
        <button class="primary" data-test="save-override" :disabled="busy" :aria-busy="work.is('override')" @click="saveOverride"><BusyLabel :busy="work.is('override')" text="Saving…">Save override</BusyLabel></button></div>

      <details class="implicit">
        <summary>Implicit (always, not editable)</summary>
        <p class="small"><strong>Read/write:</strong> <code>{{ view.implicit.readwrite.join(', ') }}</code></p>
        <p class="small"><strong>Read-only:</strong> <code>{{ view.implicit.readonly.join(', ') }}</code></p>
        <p class="small"><strong>Denied:</strong> <code>{{ view.implicit.denied.join(', ') }}</code></p>
      </details>

      <details class="capabilities">
        <summary>Backend capability notes</summary>
        <p v-if="backends.length === 0" class="small muted">The sandbox reports no containment backend available on this computer.</p>
        <p v-for="b in backends" :key="b.backend" class="small">
          <strong>{{ b.backend }}</strong>: {{ b.capabilities.join(', ') || 'no capabilities reported' }}
          <span v-if="b.warnings.length" class="warn"> · {{ b.warnings.join('; ') }}</span>
        </p>
      </details>

      <section class="loosen" data-test="loosen-section">
        <h3>⚠ Loosen… (widens this agent beyond the global policy)</h3>
        <p class="small">Only you can save this, after confirming exactly what it loosens. Vendor API and sign-in hosts, IP literals and wildcards are never allowed; no denial can be removed; timeouts up to 24 h; memory up to 75% of this machine.</p>
        <p v-if="view.loosenSummary.length" class="small" data-test="loosen-current">
          Currently loosened (confirmed {{ when(view.loosenApprovedAt) }}): <strong>{{ view.loosenSummary.join('; ') }}</strong>
        </p>
        <div class="loosen-grid">
          <label v-for="f in POLICY_FIELDS.filter((x) => x.loosen)" :key="f.path">
            {{ f.label }} <span v-if="f.hint" class="muted small">({{ f.hint }})</span>
            <textarea v-if="f.kind === 'list'" v-model="loosenText[f.path]" rows="1" placeholder="add, one per line"
              :data-test="`loosen-${testId(f.path)}`" @input="loosenChanged"></textarea>
            <input v-else v-model="loosenText[f.path]" inputmode="numeric" :data-test="`loosen-${testId(f.path)}`" @input="loosenChanged" />
          </label>
        </div>
        <div class="row"><span class="grow"></span><button data-test="preview-loosen" :disabled="busy" :aria-busy="work.is('preview')" @click="previewLoosen"><BusyLabel :busy="work.is('preview')" text="Checking…">Preview loosening</BusyLabel></button></div>
        <div v-if="preview" class="confirm" data-test="loosen-preview">
          <template v-if="preview.length">
            <p><strong>This loosens exactly:</strong></p>
            <ul><li v-for="l in preview" :key="l" data-test="loosen-preview-item">{{ l }}</li></ul>
          </template>
          <p v-else>This removes all loosening for this agent.</p>
          <div class="row"><span class="grow"></span>
            <button class="primary" data-test="confirm-loosen" :disabled="busy" :aria-busy="work.is('loosen')" @click="confirmLoosen"><BusyLabel :busy="work.is('loosen')" text="Saving…">I confirm exactly this</BusyLabel></button></div>
        </div>
      </section>
    </div>
  </Modal>
</template>

<style scoped>
.team-allows { border: 1px solid var(--line-soft); border-radius: 8px; padding: 8px 10px; margin: 8px 0; }
.tabs { display: flex; gap: 4px; margin-bottom: 10px; }
.tabs button.on { background: var(--accent); color: #fff; border-color: var(--accent); }
.general { display: grid; grid-template-columns: 1fr 1fr; gap: 10px 16px; }
.general label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--muted); }
.general .wide { grid-column: 1 / -1; }
.cmdline { display: flex; flex-direction: column; gap: 2px; word-break: break-all; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; padding: 4px 6px; border-bottom: 1px solid #eceef2; vertical-align: top; }
td textarea, td input:not([type=checkbox]), td select { width: 100%; }
.small { font-size: 12px; }
details { margin: 10px 0; }
.loosen { border: 2px solid #e9b84a; background: #fffaf0; border-radius: 6px; padding: 8px 12px; margin-top: 12px; }
.loosen h3 { margin: 2px 0 6px; font-size: 14px; color: var(--warn); }
.loosen-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px 16px; }
.loosen-grid label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; }
.confirm { border-top: 1px dashed #e9b84a; margin-top: 8px; padding-top: 6px; }
.confirm ul { margin: 4px 0; }
</style>
