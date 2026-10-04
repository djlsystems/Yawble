<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import {
  getConnectionFlow,
  getConnectionNeeds,
  listConnectionProviders,
  listConnections,
  listOpenConnectionFlows,
  renameConnection,
  saveConnectionProvider,
  startConnection,
  startDeviceConnection,
} from '../api/client';
import type {
  Connection,
  ConnectionDeviceStart,
  ConnectionFlow,
  ConnectionGuide,
  ConnectionGuideStep,
  ConnectionNeeds,
  ConnectionOpenFlow,
  ConnectionProvider,
  ConnectionProviderSave,
  MicrosoftAudience,
} from '../api/types';
import { currentOrigin, goTo } from '../lib/browserNavigation';
import { copyText } from '../lib/clipboard';
import {
  type CallbackOutcome,
  countdown,
  googleClientIdProblem,
  guideLink,
  microsoftIdProblem,
  named,
  redirectUriWarning,
  rememberGuidedConnect,
} from '../lib/connections';
import { productCli } from '../presentation/product';
import { awaitProviderReturn, newSigninTag, signinTabAddress } from '../lib/providerReturn';

/**
 * ADD CONNECTION: connecting an account for a person who has never registered an OAuth app.
 *
 * 1. SERVICE - Google or Microsoft, and what the installed plugins will ask of it, in words, each
 *    line naming the plugin that wants it. All are ticked; any may be unticked. Nothing is typed:
 *    the scopes come from the plugins' connection slots, through the Host's needs read.
 * 2. SET UP THE APP - only when the provider's client is not set up: the provider's own guide, read
 *    for the ticked scopes, as a
 *    checklist, with deep links (into the person's project, once they name it), values to copy and
 *    a Done tick per step. Its last step takes the client ID (and, for Google, the secret) and saves
 *    them. Microsoft's first step also asks who can sign in; its client is a public one, with no
 *    secret at all.
 * 3. SIGN IN - for Google, the existing web flow with the ticked scopes: the browser leaves for the
 *    provider and comes back to the Console, which opens this dialog again at `result`, the account
 *    and an optional name for it. For Microsoft, sign-in with a code: the Host asks Microsoft for
 *    one, the person enters it at Microsoft's page in another tab, and this step reads the flow until
 *    it is done and moves on to `result` by itself. Nothing leaves this address, so it works from
 *    localhost, a LAN address or a tunnel alike.
 *
 * STARTED FROM A SLOT (`need`): a plugin's connection slot that has nothing suitable bound opens this
 * dialog for that slot alone - its providers only, the scopes of that one slot - and skips the
 * service step when the slot takes one provider. Google's sign-in then opens in a new tab, so the
 * form the slot is in is still there when it comes back; `connected` says which connection the
 * sign-in made, and the opener binds it.
 *
 * A SIGN-IN WITH A CODE STILL WAITING at the Host is picked back up on open: closing the dialog, or
 * reloading the page, does not lose the code the person may be typing at Microsoft.
 *
 * THE CLIENT SECRET IS WRITE-ONLY here as in Advanced: typed, sent once, never shown or kept. The
 * provider's device code never reaches the browser: only the code the person types does.
 */
const props = defineProps<{
  providers: ConnectionProvider[];
  connections: Connection[];
  /** The connection the guided sign-in came back with: opens at the last step. */
  returned?: { id: string | null } | { refused: string } | null;
  /**
   * The slot this was started from: its plugin, its name and the providers it takes. `scopes` and
   * `pluginName` stand in when the needs read does not know the slot - a plugin not installed yet.
   */
  need?: {
    plugin: string;
    slot: string;
    providers: string[];
    scopes?: Record<string, string[]> | undefined;
    pluginName?: string | undefined;
  } | null;
}>();
const open = defineModel<boolean>({ required: true });
const emit = defineEmits<{
  /** A provider's client or a connection's name was saved: read the lists again. */
  changed: [];
  /** The sign-in completed and stored this connection. */
  connected: [connection: Connection];
}>();

type Step = 'service' | 'setup' | 'signin' | 'result';

const step = ref<Step>('service');
const providerId = ref<string | null>(null);
const needs = ref<ConnectionNeeds | null>(null);
const needsLoading = ref(false);
const unticked = ref<Set<string>>(new Set());
const detailed = ref<Set<string>>(new Set());
const problem = ref('');

/** Set up during this dialog, before the lists are read again. */
const setUpNow = ref(false);

const provider = computed(() => props.providers.find((candidate) => candidate.id === providerId.value) ?? null);
const needsSetUp = computed(() => provider.value !== null && !provider.value.configured && !setUpNow.value);

/** Each opening, so an answer read for an earlier one is dropped. */
let opening = 0;

watch(open, (showing) => {
  if (!showing) {
    // Closing cancels nothing at the Host: it only stops reading the flow.
    stopDevice();
    stopWaiting();
    return;
  }
  problem.value = '';
  device.value = null;
  deviceConnection.value = null;
  tabUrl.value = null;
  tabRefusal.value = null;

  if (props.returned) {
    step.value = 'result';
    resultName.value = '';
    return;
  }

  step.value = 'service';
  providerId.value = null;
  needs.value = null;
  unticked.value = new Set();
  detailed.value = new Set();
  setUpNow.value = false;
  guide.value = null;
  projectId.value = '';
  done.value = new Set();
  clientId.value = '';
  clientSecret.value = '';
  audience.value = 'common';
  tenantId.value = '';

  void begin(++opening);
});

/** A waiting sign-in with a code is picked back up; else a slot taking one provider skips the service step. */
async function begin(seq: number) {
  let flows: ConnectionOpenFlow[] = [];
  try {
    flows = await listOpenConnectionFlows();
  } catch {
    // Nothing to pick up: the dialog starts at the service, as it would have.
  }
  if (seq !== opening || !open.value || step.value !== 'service' || providerId.value !== null) return;

  const waiting = flows.find(
    (flow) =>
      flow.state === 'waiting'
      && new Date(flow.expiresAt).getTime() > Date.now()
      && services.value.some((service) => service.id === flow.provider)
      && props.providers.some((candidate) => candidate.id === flow.provider),
  );
  if (waiting) {
    resumeDevice(waiting);
    return;
  }

  const only = props.need && services.value.length === 1 ? services.value[0]! : null;
  if (!only) return;
  await choose(only.id);
  if (seq === opening && open.value && needs.value && step.value === 'service') await next();
}

// --- 1. Service -----------------------------------------------------------------------------------

const builtIn = [
  { id: 'google', name: 'Google' },
  { id: 'microsoft', name: 'Microsoft' },
];

/** The services offered: a slot's own, when started from one. */
const services = computed(() => (props.need ? builtIn.filter((service) => props.need!.providers.includes(service.id)) : builtIn));

async function choose(id: string) {
  providerId.value = id;
  needs.value = null;
  unticked.value = new Set();
  problem.value = '';
  needsLoading.value = true;

  try {
    const need = props.need;
    needs.value = need ? orSlot(await getConnectionNeeds(id, { plugin: need.plugin, slot: need.slot }), id) : await getConnectionNeeds(id);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    needsLoading.value = false;
  }
}

/**
 * The needs read knows only installed plugins: for a slot it does not know, the scopes the slot
 * itself names, shown as themselves.
 */
function orSlot(read: ConnectionNeeds, id: string): ConnectionNeeds {
  const need = props.need;
  const scopes = need?.scopes?.[id] ?? [];
  if (!need || read.needs.length > 0 || scopes.length === 0) return read;
  const plugins = [need.pluginName ?? need.plugin];
  return {
    ...read,
    needs: [{ plugin: need.plugin, slot: need.slot, description: null, scopes }],
    scopes: scopes.map((scope) => ({ scope, words: null, plugins })),
  };
}

/** Ticks or unticks one entry of a set: a scope left out, a scope's details shown, a guide step done. */
function toggle(which: 'unticked' | 'detailed' | 'done', key: string) {
  const set = { unticked, detailed, done }[which];
  const next = new Set(set.value);
  if (next.has(key)) next.delete(key);
  else next.add(key);
  set.value = next;
}

/** The scopes to ask for: every one the plugins want, less those unticked, in the Host's order. */
const chosenScopes = computed(() => (needs.value?.scopes ?? []).map((line) => line.scope).filter((scope) => !unticked.value.has(scope)));

const guideLoading = ref(false);

async function next() {
  problem.value = '';
  if (!needsSetUp.value) {
    step.value = 'signin';
    return;
  }

  // The guide is read again for what is ticked: its APIs and data access are those scopes' own.
  // With nothing ticked, the provider's default scopes stand for "nothing more".
  const chosen = provider.value!;
  const scopes = chosenScopes.value.length > 0 ? chosenScopes.value : chosen.defaultScopes;
  guideLoading.value = true;
  try {
    const read = await listConnectionProviders(scopes);
    guide.value = read.find((candidate) => candidate.id === chosen.id)?.guide ?? chosen.guide ?? null;
    step.value = 'setup';
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    guideLoading.value = false;
  }
}

// --- 2. Set up the app ----------------------------------------------------------------------------

const guide = ref<ConnectionGuide | null>(null);
const projectId = ref('');
const done = ref<Set<string>>(new Set());
const clientId = ref('');
const clientSecret = ref('');

/** Each API's own link, for just the APIs the guide (read for the ticked scopes) names on this step. */
function apisFor(item: ConnectionGuide['steps'][number]) {
  const named = new Set(item.copy.map((value) => value.value));
  return (needs.value?.apis ?? []).filter((api) => named.has(api.api));
}
const clientBusy = ref(false);

const steps = computed<ConnectionGuideStep[]>(() => guide.value?.steps ?? []);
const redirectWarning = computed(() => redirectUriWarning(currentOrigin(), productCli));

/** Microsoft's client is public and signs in with a code: no secret, no redirect URI. */
const isMicrosoft = computed(() => provider.value?.kind === 'microsoft');

const clientIdProblem = computed(() => {
  if (provider.value?.kind === 'google') return googleClientIdProblem(clientId.value);
  if (isMicrosoft.value) return microsoftIdProblem(clientId.value, 'Application (client) ID');
  return null;
});

/** Who can sign in through a Microsoft app, chosen on its first step. */
const audience = ref<MicrosoftAudience>('common');
const tenantId = ref('');
const audiences: { value: MicrosoftAudience; label: string; hint: string }[] = [
  { value: 'common', label: 'Personal and any work account', hint: 'Choose the same when you register the app.' },
  { value: 'organizations', label: 'Work accounts only', hint: 'Any organisation\'s work or school account, no personal one.' },
  { value: 'tenant', label: 'Only my organisation', hint: 'Accounts in your own directory only.' },
];
const tenantIdProblem = computed(() => (audience.value === 'tenant' ? microsoftIdProblem(tenantId.value, 'Directory (tenant) ID') : null));

/** Anything still in the way of Save client, or null. */
const cannotSave = computed(
  () =>
    clientId.value.trim() === ''
    || clientIdProblem.value !== null
    || (isMicrosoft.value && audience.value === 'tenant' && (tenantId.value.trim() === '' || tenantIdProblem.value !== null)),
);

async function copy(value: string) {
  try {
    await copyText(value);
  } catch {
    problem.value = 'Could not copy to the clipboard. Select the value and copy it by hand.';
  }
}

async function saveClient() {
  const chosen = provider.value;
  if (!chosen || clientBusy.value || cannotSave.value) return;

  const body: ConnectionProviderSave = { clientId: clientId.value.trim() };
  if (isMicrosoft.value) {
    body.audience = audience.value;
    if (audience.value === 'tenant') body.tenantId = tenantId.value.trim();
  } else if (clientSecret.value !== '') {
    body.clientSecret = clientSecret.value;
  }

  clientBusy.value = true;
  problem.value = '';
  try {
    await saveConnectionProvider(chosen.id, body);
    clientSecret.value = '';
    setUpNow.value = true;
    emit('changed');
    step.value = 'signin';
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    clientBusy.value = false;
  }
}

// --- 3. Sign in -----------------------------------------------------------------------------------

const leaving = ref(false);

async function signIn() {
  const chosen = provider.value;
  if (!chosen || leaving.value) return;
  if (isMicrosoft.value) {
    await startDevice();
    return;
  }

  leaving.value = true;
  problem.value = '';
  try {
    const flow = await startConnection({ provider: chosen.id, scopes: chosenScopes.value });
    if (props.need) {
      // From a slot: the provider opens in another tab, and this one waits for its return.
      // The tab is tagged, so only this sign-in's return is taken here.
      stopWaiting();
      const tag = newSigninTag();
      tabUrl.value = signinTabAddress(tag);
      stopReturn = awaitProviderReturn(tag, flow.authorizationUrl, (outcome) => void providerReturned(outcome));
      return;
    }
    rememberGuidedConnect(chosen.id);
    goTo(flow.authorizationUrl);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    leaving.value = false;
  }
}

// --- 3. Sign in with a code -----------------------------------------------------------------------

/** How often the flow is read while it waits. The Host does the waiting at the provider. */
const flowReadEvery = 3000;

const device = ref<ConnectionDeviceStart | null>(null);
const deviceRead = ref<ConnectionFlow | null>(null);
const deviceConnection = ref<Connection | null>(null);
const now = ref(Date.now());
let readTimer: ReturnType<typeof setTimeout> | null = null;
let clock: ReturnType<typeof setInterval> | null = null;

const deviceState = computed(() => deviceRead.value?.state ?? 'waiting');
const deviceLeft = computed(() => (device.value ? countdown(device.value.expiresAt, now.value) : ''));

function stopDevice() {
  if (readTimer !== null) clearTimeout(readTimer);
  if (clock !== null) clearInterval(clock);
  readTimer = null;
  clock = null;
}

onBeforeUnmount(stopDevice);

/** Asks the Host for a code (again, on Try again) and starts reading the flow. */
async function startDevice() {
  const chosen = provider.value;
  if (!chosen) return;

  stopDevice();
  leaving.value = true;
  problem.value = '';
  try {
    device.value = await startDeviceConnection({ provider: chosen.id, scopes: chosenScopes.value, flow: 'device' });
    deviceRead.value = null;
    now.value = Date.now();
    clock = setInterval(() => (now.value = Date.now()), 1000);
    readTimer = setTimeout(readFlow, flowReadEvery);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    leaving.value = false;
  }
}

/** A sign-in with a code the Host still holds, shown again as it was: its code, link and countdown. */
function resumeDevice(flow: ConnectionOpenFlow) {
  stopDevice();
  providerId.value = flow.provider;
  device.value = { flowId: flow.flowId, userCode: flow.userCode, verificationUri: flow.verificationUri, expiresAt: flow.expiresAt };
  deviceRead.value = null;
  step.value = 'signin';
  now.value = Date.now();
  clock = setInterval(() => (now.value = Date.now()), 1000);
  // Read at once: the flow may have moved on while the dialog was closed.
  readTimer = setTimeout(readFlow, 0);
}

/** Back to the service: the code shown is left to expire at the Host. */
function back() {
  stopDevice();
  stopWaiting();
  tabUrl.value = null;
  device.value = null;
  deviceRead.value = null;
  step.value = 'service';
}

async function readFlow() {
  const flow = device.value;
  readTimer = null;
  if (!flow || !open.value) return;

  let read: ConnectionFlow;
  try {
    read = await getConnectionFlow(flow.flowId);
  } catch (cause) {
    // A read that failed is not an ending: say so and read again.
    problem.value = cause instanceof Error ? cause.message : String(cause);
    if (device.value === flow && open.value) readTimer = setTimeout(readFlow, flowReadEvery);
    return;
  }
  // Try again or a close while the read was out: this answer is for a flow no longer shown.
  if (device.value !== flow || !open.value) return;

  problem.value = '';
  deviceRead.value = read;
  if (read.state === 'waiting') {
    readTimer = setTimeout(readFlow, flowReadEvery);
    return;
  }

  stopDevice();
  if (read.state === 'done') {
    deviceConnection.value = read.connection ?? null;
    resultName.value = '';
    emit('changed');
    step.value = 'result';
    if (read.connection) emit('connected', read.connection);
  }
}

// --- 3. Sign in in another tab, from a slot ---------------------------------------------------------

/** The Console address that opens the other tab and sends it on to the provider; null until the sign-in is started. */
const tabUrl = ref<string | null>(null);
const tabRefusal = ref<string | null>(null);
let stopReturn: (() => void) | null = null;

function stopWaiting() {
  stopReturn?.();
  stopReturn = null;
}

onBeforeUnmount(stopWaiting);

/** The other tab came back from the provider: the connection it made, or the Host's reason. */
async function providerReturned(outcome: CallbackOutcome) {
  stopWaiting();
  if (!open.value) return;
  resultName.value = '';

  if (outcome.outcome === 'refused') {
    tabRefusal.value = outcome.reason;
    step.value = 'result';
    return;
  }

  try {
    const made = outcome.id ? (await listConnections()).find((connection) => connection.id === outcome.id) : undefined;
    emit('changed');
    if (made) {
      deviceConnection.value = made;
      step.value = 'result';
      emit('connected', made);
    } else {
      step.value = 'result';
    }
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  }
}

// --- Back from the provider -----------------------------------------------------------------------

const resultName = ref('');
const finishing = ref(false);

const returnedConnection = computed(() => {
  if (deviceConnection.value) return deviceConnection.value;
  const returned = props.returned;
  if (!returned || !('id' in returned) || !returned.id) return null;
  return props.connections.find((connection) => connection.id === returned.id) ?? null;
});
const refusal = computed(() => tabRefusal.value ?? (props.returned && 'refused' in props.returned ? props.returned.refused : null));

async function finish() {
  const connection = returnedConnection.value;
  const name = resultName.value.trim();
  if (finishing.value) return;

  if (connection && name !== '' && name !== connection.name) {
    finishing.value = true;
    problem.value = '';
    try {
      await renameConnection(connection.id, name);
      emit('changed');
    } catch (cause) {
      problem.value = cause instanceof Error ? cause.message : String(cause);
      return;
    } finally {
      finishing.value = false;
    }
  }

  open.value = false;
}

const stepLabels = computed(() => [
  { id: 'service', label: 'Service' },
  {
    id: 'setup',
    label: provider.value && provider.value.configured && !setUpNow.value ? 'Set up the app (already set up)' : 'Set up the app',
  },
  { id: 'signin', label: 'Sign in' },
]);
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-md" data-guided-connect>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Add connection</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section v-if="step !== 'result'" class="q-py-xs">
        <ol class="connect-steps text-caption">
          <li
            v-for="(item, index) in stepLabels"
            :key="item.id"
            :class="{ 'connect-steps-now': item.id === step }"
          >
            {{ index + 1 }}. {{ item.label }}
          </li>
        </ol>
      </q-card-section>

      <!-- 1. SERVICE -->
      <q-card-section v-if="step === 'service'" data-connect-step="service" class="q-gutter-y-md">
        <div>Which account do you want to connect?</div>
        <div class="row q-gutter-sm">
          <q-btn
            v-for="service in services"
            :key="service.id"
            no-caps
            :outline="providerId !== service.id"
            :color="providerId === service.id ? 'primary' : undefined"
            :label="service.name"
            :data-connect-service="service.id"
            @click="choose(service.id)"
          />
        </div>

        <template v-if="provider">
          <div v-if="needsLoading" class="os-text-muted">Reading what the plugins need…</div>
          <template v-else-if="needs">
            <div v-if="needs.scopes.length === 0" class="os-text-muted" data-no-needs>
              No installed plugin asks anything of {{ provider.name }} yet. Signing in asks only for
              the account's name and email address.
            </div>
            <template v-else>
              <div>The plugins will be allowed to:</div>
              <div
                v-for="line in needs.scopes"
                :key="line.scope"
                class="connect-scope"
                :data-connect-scope="line.scope"
              >
                <q-checkbox
                  dense
                  :model-value="!unticked.has(line.scope)"
                  :aria-label="line.words ?? line.scope"
                  @update:model-value="toggle('unticked', line.scope)"
                />
                <div class="connect-scope-text">
                  <div data-scope-words :class="{ mono: line.words === null }">{{ line.words ?? line.scope }}</div>
                  <div class="text-caption os-text-muted" data-scope-plugins>
                    Wanted by {{ line.plugins.join(', ') }}
                  </div>
                  <q-btn
                    v-if="line.words !== null"
                    flat
                    dense
                    no-caps
                    size="sm"
                    class="connect-details"
                    :label="detailed.has(line.scope) ? 'hide details' : 'details'"
                    @click="toggle('detailed', line.scope)"
                  />
                  <div v-if="line.words !== null && detailed.has(line.scope)" class="mono text-caption" data-scope-raw>
                    {{ line.scope }}
                  </div>
                </div>
              </div>
              <div class="text-caption os-text-muted">
                Untick anything you do not want to allow. A plugin that needs it will say so when it runs.
              </div>
            </template>
          </template>
        </template>
      </q-card-section>

      <!-- 2. SET UP THE APP -->
      <q-card-section v-else-if="step === 'setup' && provider" data-connect-step="setup" class="q-gutter-y-sm">
        <div>
          {{ provider.name }} needs an app of your own to sign in through. Work down the list in another
          tab; tick each step as you finish it.
        </div>

        <ol class="connect-guide">
          <li v-for="item in steps" :key="item.id" class="connect-guide-step" :data-guide-step="item.id">
            <div class="row items-center no-wrap">
              <q-checkbox
                dense
                :model-value="done.has(item.id)"
                :aria-label="`Done: ${item.title}`"
                data-guide-done
                @update:model-value="toggle('done', item.id)"
              />
              <span class="text-weight-medium q-ml-sm">{{ item.title }}</span>
            </div>

            <div class="connect-guide-body" data-guide-body>
              <!-- When this address will be refused, the Host's text for the client step opens with why. -->
              <div :class="{ 'text-warning': item.id === 'client' && redirectWarning }">{{ item.text }}</div>

              <q-input
                v-if="item.id === 'project'"
                v-model="projectId"
                outlined
                dense
                label="Project ID (optional)"
                hint="Name it and the links below open in that project."
                spellcheck="false"
                autocomplete="off"
              />

              <a
                v-if="item.link"
                :href="guideLink(item.link, projectId)"
                target="_blank"
                rel="noopener noreferrer"
                data-guide-link
              >Open in {{ provider.name }}</a>

              <div v-if="isMicrosoft && item.id === 'register'" class="connect-audience" data-guide-audience>
                <div class="text-caption os-text-muted">Who can sign in? Choose the same here as at Microsoft.</div>
                <div v-for="option in audiences" :key="option.value">
                  <q-radio
                    v-model="audience"
                    dense
                    :val="option.value"
                    :label="option.label"
                    :data-audience="option.value"
                  />
                  <div class="text-caption os-text-muted connect-audience-hint">{{ option.hint }}</div>
                </div>
                <q-input
                  v-if="audience === 'tenant'"
                  v-model="tenantId"
                  outlined
                  dense
                  label="Directory (tenant) ID"
                  hint="On the app's Overview page, under the Application (client) ID."
                  spellcheck="false"
                  autocomplete="off"
                  :error="tenantIdProblem !== null"
                >
                  <template #error><span data-tenant-id-shape>{{ tenantIdProblem }}</span></template>
                </q-input>
              </div>

              <template v-if="item.id === 'apis'">
                <div v-for="api in apisFor(item)" :key="api.api" data-guide-api>
                  <a :href="guideLink(api.link, projectId)" target="_blank" rel="noopener noreferrer" class="mono">{{ api.api }}</a>
                </div>
              </template>

              <div v-for="value in item.copy" :key="`${value.label}:${value.value}`" class="connect-copy">
                <span class="text-caption os-text-muted">{{ value.label }}</span>
                <span class="mono connect-copy-value">{{ value.value }}</span>
                <q-btn flat round dense size="sm" icon="content_copy" :aria-label="`Copy ${value.label}`" @click="copy(value.value)" />
              </div>

              <template v-if="item.id === 'credentials'">
                <q-input
                  v-model="clientId"
                  outlined
                  dense
                  :label="isMicrosoft ? 'Application (client) ID' : 'Client ID'"
                  spellcheck="false"
                  autocomplete="off"
                  :error="clientIdProblem !== null"
                  :hide-bottom-space="clientIdProblem === null"
                >
                  <template #error><span data-client-id-shape>{{ clientIdProblem }}</span></template>
                </q-input>
                <q-input
                  v-if="!isMicrosoft"
                  v-model="clientSecret"
                  outlined
                  dense
                  type="password"
                  label="Client secret"
                  hint="Stored encrypted and never shown again."
                  autocomplete="new-password"
                  spellcheck="false"
                />
                <div class="row justify-end">
                  <q-btn
                    color="primary"
                    no-caps
                    label="Save client"
                    :loading="clientBusy"
                    :disable="clientBusy || cannotSave"
                    @click="saveClient"
                  />
                </div>
              </template>
            </div>
          </li>
        </ol>
      </q-card-section>

      <!-- 3. SIGN IN WITH A CODE -->
      <q-card-section v-else-if="step === 'signin' && provider && isMicrosoft" data-connect-step="signin" class="q-gutter-y-sm">
        <template v-if="device === null">
          <div>
            {{ provider.name }} gives you a code to enter on its own sign-in page, in another tab. Nothing
            has to come back to this address, so it works however you reached this page.
            {{ chosenScopes.length === 0
              ? 'Only the account\'s name and email address are asked for.'
              : `${chosenScopes.length === 1 ? 'One thing is' : `${chosenScopes.length} things are`} asked for besides the account's name and email address.` }}
          </div>
        </template>
        <div v-else :data-device-state="deviceState">
          <template v-if="deviceState === 'waiting'">
            <div>
              Open
              <a :href="device.verificationUri" target="_blank" rel="noopener noreferrer" data-device-link>{{ device.verificationUri }}</a>
              and enter this code:
            </div>
            <div class="row items-center q-gutter-sm q-my-sm">
              <span class="mono connect-device-code" data-device-code>{{ device.userCode }}</span>
              <q-btn flat round dense icon="content_copy" aria-label="Copy the code" @click="copy(device.userCode)" />
            </div>
            <div class="text-caption os-text-muted" data-device-countdown>
              The code expires in {{ deviceLeft }}. Sign in there and agree; this step moves on by itself.
            </div>
            <div class="row items-center q-gutter-sm q-mt-sm os-text-muted">
              <q-spinner size="16px" />
              <span>Waiting for you to sign in…</span>
            </div>
          </template>
          <div v-else class="text-negative" data-device-sentence>{{ deviceRead?.sentence }}</div>
        </div>
      </q-card-section>

      <!-- 3. SIGN IN -->
      <q-card-section v-else-if="step === 'signin' && provider" data-connect-step="signin" class="q-gutter-y-sm">
        <div v-if="tabUrl" data-signin-tab>
          <div>
            <a :href="tabUrl" target="_blank" rel="noopener noreferrer" data-signin-link>Open {{ provider.name }} to sign in</a>
            in another tab, and agree there. This step moves on by itself when you are back.
          </div>
          <div class="row items-center q-gutter-sm q-mt-sm os-text-muted">
            <q-spinner size="16px" />
            <span>Waiting for you to sign in…</span>
          </div>
        </div>
        <div v-else-if="need">
          {{ provider.name }} opens in another tab to sign in and agree, so this page stays as it is.
          {{ chosenScopes.length === 0
            ? 'Only the account\'s name and email address are asked for.'
            : `${chosenScopes.length === 1 ? 'One thing is' : `${chosenScopes.length} things are`} asked for besides the account's name and email address.` }}
        </div>
        <div v-else>
          Your browser goes to {{ provider.name }} to sign in and agree, then comes back here.
          {{ chosenScopes.length === 0
            ? 'Only the account\'s name and email address are asked for.'
            : `${chosenScopes.length === 1 ? 'One thing is' : `${chosenScopes.length} things are`} asked for besides the account's name and email address.` }}
        </div>
        <div v-if="redirectWarning" class="text-caption text-warning" data-redirect-warning>{{ redirectWarning }}</div>
        <div class="text-caption os-text-muted">
          If {{ provider.name }} says it has not verified the app, that is expected for your own app:
          choose Advanced, then go on to it.
        </div>
      </q-card-section>

      <!-- BACK FROM THE PROVIDER -->
      <q-card-section v-else-if="step === 'result'" data-connect-step="result" class="q-gutter-y-md">
        <template v-if="refusal">
          <div class="text-negative">The account was not connected: {{ refusal }}</div>
        </template>
        <template v-else>
          <div class="text-positive">
            Connected{{ returnedConnection ? ` ${named(returnedConnection)}` : '' }}.
          </div>
          <q-input
            v-model="resultName"
            outlined
            dense
            label="Name (optional)"
            :hint="returnedConnection ? `Leave empty to keep ${returnedConnection.name}.` : 'Leave empty to keep the account\'s name.'"
            maxlength="80"
            autocomplete="off"
            @keydown.enter.prevent="finish"
          />
        </template>
      </q-card-section>

      <q-card-section v-if="problem" class="text-negative q-pt-none" data-guided-problem>{{ problem }}</q-card-section>

      <q-card-actions align="right">
        <template v-if="step === 'service'">
          <q-btn v-close-popup flat no-caps label="Cancel" />
          <q-btn
            color="primary"
            no-caps
            label="Next"
            :loading="guideLoading"
            :disable="!provider || needsLoading || needs === null || guideLoading"
            @click="next"
          />
        </template>
        <template v-else-if="step === 'setup'">
          <q-btn flat no-caps label="Back" @click="step = 'service'" />
        </template>
        <template v-else-if="step === 'signin' && provider">
          <q-btn flat no-caps label="Back" @click="back" />
          <q-btn
            v-if="isMicrosoft && device !== null && deviceState !== 'waiting'"
            color="primary"
            no-caps
            label="Try again"
            :loading="leaving"
            :disable="leaving"
            @click="startDevice"
          />
          <q-btn
            v-else-if="(!isMicrosoft || device === null) && tabUrl === null"
            color="primary"
            no-caps
            :label="`Sign in with ${provider.name}`"
            :loading="leaving"
            :disable="leaving"
            @click="signIn"
          />
        </template>
        <template v-else-if="step === 'result'">
          <q-btn color="primary" no-caps label="Finish" :loading="finishing" @click="finish" />
        </template>
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.connect-steps {
  display: flex;
  gap: 16px;
  list-style: none;
  margin: 0;
  padding: 0;
  color: var(--os-ink-muted);
}

.connect-steps-now {
  color: inherit;
  font-weight: 600;
}

.connect-scope {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.connect-scope-text {
  min-width: 0;
  overflow-wrap: anywhere;
}

.connect-details {
  padding: 0 4px;
  margin-left: -4px;
}

.connect-guide {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.connect-guide-body {
  margin-left: 30px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.connect-audience-hint {
  margin-left: 30px;
}

.connect-device-code {
  font-size: 2rem;
  font-weight: 600;
  letter-spacing: 0.1em;
  user-select: all;
}

.connect-copy {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.connect-copy-value {
  overflow-wrap: anywhere;
  min-width: 0;
}
</style>
