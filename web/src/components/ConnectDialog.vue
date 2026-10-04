<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { getConnectionNeeds, renameConnection, saveConnectionProvider, startConnection } from '../api/client';
import type { Connection, ConnectionGuideStep, ConnectionNeeds, ConnectionProvider, ConnectionProviderSave } from '../api/types';
import { currentOrigin, goTo } from '../lib/browserNavigation';
import { copyText } from '../lib/clipboard';
import {
  googleClientIdProblem,
  guideLink,
  named,
  redirectUriWarning,
  rememberGuidedConnect,
} from '../lib/connections';
import { productCli } from '../presentation/product';

/**
 * ADD CONNECTION: connecting an account for a person who has never registered an OAuth app.
 *
 * 1. SERVICE - Google or Microsoft, and what the installed plugins will ask of it, in words, each
 *    line naming the plugin that wants it. All are ticked; any may be unticked. Nothing is typed:
 *    the scopes come from the plugins' connection slots, through the Host's needs read.
 * 2. SET UP THE APP - only when the provider's client is not set up: the provider's own guide as a
 *    checklist, with deep links (into the person's project, once they name it), values to copy and
 *    a Done tick per step. Its last step takes the client ID and secret and saves them.
 * 3. SIGN IN - the existing web flow with the ticked scopes. The browser leaves for the provider and
 *    comes back to the Console, which opens this dialog again at `result`: the account, and an
 *    optional name for it.
 *
 * Microsoft's sign-in is not guided yet: choosing it hands over to Advanced (`advanced`), the
 * Connect form with Microsoft chosen.
 *
 * THE CLIENT SECRET IS WRITE-ONLY here as in Advanced: typed, sent once, never shown or kept.
 */
const props = defineProps<{
  providers: ConnectionProvider[];
  connections: Connection[];
  /** The connection the guided sign-in came back with: opens at the last step. */
  returned?: { id: string | null } | { refused: string } | null;
}>();
const open = defineModel<boolean>({ required: true });
const emit = defineEmits<{
  /** Connect this provider from Advanced instead. */
  advanced: [provider: string];
  /** A provider's client or a connection's name was saved: read the lists again. */
  changed: [];
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

watch(open, (showing) => {
  if (!showing) return;
  problem.value = '';

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
  projectId.value = '';
  done.value = new Set();
  clientId.value = '';
  clientSecret.value = '';
});

// --- 1. Service -----------------------------------------------------------------------------------

const services = [
  { id: 'google', name: 'Google' },
  { id: 'microsoft', name: 'Microsoft' },
];

async function choose(id: string) {
  if (id === 'microsoft') {
    open.value = false;
    emit('advanced', id);
    return;
  }

  providerId.value = id;
  needs.value = null;
  unticked.value = new Set();
  problem.value = '';
  needsLoading.value = true;

  try {
    needs.value = await getConnectionNeeds(id);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    needsLoading.value = false;
  }
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

function next() {
  problem.value = '';
  step.value = needsSetUp.value ? 'setup' : 'signin';
}

// --- 2. Set up the app ----------------------------------------------------------------------------

const projectId = ref('');
const done = ref<Set<string>>(new Set());
const clientId = ref('');
const clientSecret = ref('');
const clientBusy = ref(false);

const steps = computed<ConnectionGuideStep[]>(() => provider.value?.guide?.steps ?? []);
const redirectWarning = computed(() => redirectUriWarning(currentOrigin(), productCli));
const clientIdProblem = computed(() => (provider.value?.kind === 'google' ? googleClientIdProblem(clientId.value) : null));

async function copy(value: string) {
  try {
    await copyText(value);
  } catch {
    problem.value = 'Could not copy to the clipboard. Select the value and copy it by hand.';
  }
}

async function saveClient() {
  const chosen = provider.value;
  if (!chosen || clientBusy.value || clientId.value.trim() === '' || clientIdProblem.value) return;

  const body: ConnectionProviderSave = { clientId: clientId.value.trim() };
  if (clientSecret.value !== '') body.clientSecret = clientSecret.value;

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

  leaving.value = true;
  problem.value = '';
  try {
    const flow = await startConnection({ provider: chosen.id, scopes: chosenScopes.value });
    rememberGuidedConnect(chosen.id);
    goTo(flow.authorizationUrl);
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    leaving.value = false;
  }
}

// --- Back from the provider -----------------------------------------------------------------------

const resultName = ref('');
const finishing = ref(false);

const returnedConnection = computed(() => {
  const returned = props.returned;
  if (!returned || !('id' in returned) || !returned.id) return null;
  return props.connections.find((connection) => connection.id === returned.id) ?? null;
});
const refusal = computed(() => (props.returned && 'refused' in props.returned ? props.returned.refused : null));

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
        <div v-if="providerId === null" class="text-caption os-text-muted">
          Microsoft is set up under Advanced for now.
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
              <div
                v-if="item.id === 'client' && redirectWarning"
                class="text-warning"
                data-redirect-warning
              >
                {{ redirectWarning }}
              </div>
              <div>{{ item.text }}</div>

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

              <template v-if="item.id === 'apis' && needs">
                <div v-for="api in needs.apis" :key="api.api" data-guide-api>
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
                  label="Client ID"
                  spellcheck="false"
                  autocomplete="off"
                  :error="clientIdProblem !== null"
                  :hide-bottom-space="clientIdProblem === null"
                >
                  <template #error><span data-client-id-shape>{{ clientIdProblem }}</span></template>
                </q-input>
                <q-input
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
                    :disable="clientBusy || clientId.trim() === '' || clientIdProblem !== null"
                    @click="saveClient"
                  />
                </div>
              </template>
            </div>
          </li>
        </ol>
      </q-card-section>

      <!-- 3. SIGN IN -->
      <q-card-section v-else-if="step === 'signin' && provider" data-connect-step="signin" class="q-gutter-y-sm">
        <div>
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
          <q-btn color="primary" no-caps label="Next" :disable="!provider || needsLoading || needs === null" @click="next" />
        </template>
        <template v-else-if="step === 'setup'">
          <q-btn flat no-caps label="Back" @click="step = 'service'" />
        </template>
        <template v-else-if="step === 'signin' && provider">
          <q-btn flat no-caps label="Back" @click="step = 'service'" />
          <q-btn color="primary" no-caps :label="`Sign in with ${provider.name}`" :loading="leaving" :disable="leaving" @click="signIn" />
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
