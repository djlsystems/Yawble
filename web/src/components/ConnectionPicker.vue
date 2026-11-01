<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { listConnections, startConnection, startDeviceConnection } from '../api/client';
import type { Connection, ConnectionDeviceStart, ConnectionFlow, ConnectionProvider, ConnectionSlot } from '../api/types';
import { goTo } from '../lib/browserNavigation';
import { refusedReconnect } from '../lib/slotBinding';
import CodeSignIn from './CodeSignIn.vue';
import DirectoryField from './DirectoryField.vue';
import SlotConnect from './SlotConnect.vue';
import SlotReconnect from './SlotReconnect.vue';
import ConnectionSelect from './ConnectionSelect.vue';
import {
  connectionsForSlot,
  directoryRefusal,
  goneRefusal,
  guidedProviders,
  missingScopes,
  providerName,
  scopeRefusal,
  slotHint,
  statusLabel,
  UsernameIncorrectHint,
} from '../lib/connections';

/**
 * ONE CONNECTION SLOT'S PICKER, in a plugin member's settings - at hire and in Member settings. It
 * lists the connections of the providers the slot allows and stores the chosen connection's ID;
 * no token is anywhere near it.
 *
 * It says, before the save, what the Host would say of a connection that lacks a scope the slot
 * needs (refused at the save) -
 * with Reconnect, which asks the Host for that connection's consent page with the missing scopes
 * added and sends the browser there. The Host is still the check: it refuses the save in the same
 * words.
 *
 * RECONNECT ON A MICROSOFT CONNECTION SIGNS IN WITH A CODE, as the Connections dialog's does:
 * Microsoft has no consent page to send the browser to (its sign-in takes a code), so Reconnect -
 * beside a chosen connection, or the one a refused binding offers, and with `reconnectInTab` too -
 * asks the Host for a code (`flow: "device"`, the connection's ID, the scopes asked) and shows it
 * here through CodeSignIn, the same component Add connection shows its code with. Once approved the
 * connection is read again and bound as a connection made from this slot is (`connected`); a
 * refused or expired code says so in the Host's sentence and binds nothing. Cancel beside the code ends
 * the sign-in at the Host and takes the code away.
 *
 * A MICROSOFT RECONNECT NAMES ITS DIRECTORY: beside the Reconnect for a Microsoft connection is the same
 * optional DirectoryField (with UsernameIncorrectHint under it) as Add connection and Admin ->
 * Connections' Reconnect. What is typed is sent as `tenant`; left empty, no `tenant` is sent and the
 * Host keeps the connection's own (a connection made with none signs in through `common`, where a
 * personal Microsoft account cannot get Azure tokens). The Host's refusal of it is shown at the field.
 *
 * CONNECT <PROVIDER> comes first while nothing suitable is bound, given the slot's `plugin`: Add
 * connection, started from this slot. When its sign-in completes, `bind` (where the member exists)
 * binds the new connection through the member's settings route and the opener shows it bound; with
 * no `bind` (a hire, an install) the connection is chosen here and the save binds it. A binding the
 * Host refuses is shown in its words, with Reconnect when only scopes are missing.
 *
 * With `reconnectInTab` (the solution wizard, whose answers a trip away would lose) Reconnect opens
 * the provider in another tab and waits here - a Microsoft one shows its code here, as everywhere; the reconnected connection comes back as `connected`
 * and stays the binding, and a sign-in that granted fewer scopes than asked says so.
 *
 * ONLY WHAT THE TEAM WAS GIVEN (`assigned`): a member's slot lists only the connections assigned to
 * its team (Team settings → Connections) that fit the slot - its kind, providers and scopes - and
 * when exactly one fits, it is preselected. The select is ConnectionSelect: **New connection...**, the
 * list's last entry, opens the connection wizard (ConnectDialog) for this slot, over the form it is in;
 * what it saves is bound as `connected` binds, and
 * the Host's binding assigns it to the team in the same transaction. Without `assigned` (a new
 * team's install, before it has any) every connection that fits is offered.
 */
const props = defineProps<{
  slotName: string;
  spec: ConnectionSlot;
  connections: Connection[];
  providers: ConnectionProvider[];
  /** The plugin declaring the slot: with it, Connect is offered. */
  plugin?: string | undefined;
  /** Binds a connection to this slot at the Host, throwing its refusal. */
  bind?: ((connectionId: string) => Promise<void>) | undefined;
  /** The picker's label, when the slot's name alone does not say whose it is. */
  label?: string | undefined;
  /** The line under the picker, in place of the slot's own description and summary. */
  hint?: string | undefined;
  /** The plugin's name, for a slot whose plugin is not installed yet. */
  pluginName?: string | undefined;
  /** Reconnect in another tab, leaving this page as it is. */
  reconnectInTab?: boolean | undefined;
  /** The ids of the connections assigned to the member's team: only these are offered. */
  assigned?: readonly string[] | undefined;
  /** False where choosing binds at once (a blocked install's fix): nothing is chosen for the person. */
  noPreselect?: boolean | undefined;
}>();

const bound = defineModel<string>({ required: true });
const emit = defineEmits<{
  /** A sign-in from this slot made a connection: read the lists again. */
  connected: [connection: Connection];
  /** Add connection, opened from this slot, was closed. */
  closed: [];
}>();

/**
 * WHAT FITS THE SLOT AND THE TEAM: of the team's assigned connections, those of a provider the slot
 * takes holding every scope it asks. The one bound stays listed whatever it is, so the list shows it.
 */
const choices = computed(() => {
  const given = props.assigned ? new Set(props.assigned) : null;
  return connectionsForSlot(props.spec, props.connections).filter(
    (connection) => connection.id === bound.value || !given || given.has(connection.id),
  );
});

/** The connections that fit outright: the team's, of the slot's providers, lacking no scope. */
const fitting = computed(() =>
  choices.value.filter((connection) => connection.status === 'ok' && missingScopes(props.spec, connection).length === 0),
);

// EXACTLY ONE FITS: it is preselected, once, while nothing is bound. The person still saves it.
watch(
  fitting,
  (fits) => {
    // An optional slot a member already has is left as the person left it.
    const choosing = !props.noPreselect && (props.spec.required || !props.bind);
    if (choosing && bound.value === '' && fits.length === 1 && props.assigned) bound.value = fits[0]!.id;
  },
  { immediate: true },
);

/** How a connection reads in the slot's list, as it always has. */
function optionLabel(connection: Connection): string {
  return (
    `${connection.name} - ${connection.account} (${providerName(connection.provider, props.providers)})` +
    (connection.status === 'ok' ? '' : `, ${statusLabel(connection)}`)
  );
}

/** New connection... from this slot: the wizard, for this slot's kinds and scopes. */
const wizardNeed = computed(() =>
  props.plugin
    ? {
        plugin: props.plugin,
        slot: props.slotName,
        providers: props.spec.providers,
        scopes: props.spec.scopes,
        pluginName: props.pluginName,
      }
    : null,
);

function picked(value: string | null) {
  bound.value = value ?? '';
}

const chosen = computed(() => props.connections.find((connection) => connection.id === bound.value) ?? null);

const missing = computed(() => (chosen.value ? missingScopes(props.spec, chosen.value) : []));

/**
 * The sentence the Host would refuse the binding with. A required slot left unbound is no refusal
 * here: the picker and Connect are right above, and the Host still hires and saves, so it says only
 * what is left to do.
 */
const unbound = computed(() => bound.value === '' && props.spec.required);

const refusal = computed(() => {
  if (bound.value === '') return '';
  if (!chosen.value) return props.connections.length > 0 ? goneRefusal(props.slotName) : '';
  if (missing.value.length > 0) return scopeRefusal(props.slotName, chosen.value, missing.value);
  return '';
});

/** The name of a connection's provider, for Reconnect's link. */
function providerOf(connectionId: string): string {
  const connection = props.connections.find((candidate) => candidate.id === connectionId);
  return connection ? providerName(connection.provider, props.providers) : 'the provider';
}

/** A connection's name, for the code's line. */
function connectionName(connectionId: string): string {
  return props.connections.find((candidate) => candidate.id === connectionId)?.name ?? 'the connection';
}

/** Reconnected in another tab: the lists are read again, and the binding stays the same connection. */
function reconnectedHere(connection: Connection) {
  emit('connected', connection);
  if (bindReconnect.value?.connectionId === connection.id && missingScopes(props.spec, connection).length === 0) {
    bindRefusal.value = '';
    bindReconnect.value = null;
  }
}

const reconnecting = ref(false);
const reconnectProblem = ref('');

/** Microsoft's connections are made with a code, and reconnect with one: no redirect to come back by. */
function withCode(connectionId: string): boolean {
  return props.connections.find((connection) => connection.id === connectionId)?.providerKind === 'microsoft';
}

/** A Microsoft Reconnect's directory, optional: sent as `tenant` only when typed. */
const reconnectDirectory = ref('');
/** The Host's refusal of the directory typed, shown at the field. */
const reconnectDirectoryProblem = ref('');

watch(reconnectDirectory, () => (reconnectDirectoryProblem.value = ''));

/** The connection a Reconnect offered here would sign in again, when it is a Microsoft one: the field shows. */
const directoryFor = computed(() => {
  if (reconnectCode.value && !reconnectEnded.value) return null;
  if (bindRefusal.value && bindReconnect.value && !(props.reconnectInTab && !withCode(bindReconnect.value.connectionId))) {
    return withCode(bindReconnect.value.connectionId) ? bindReconnect.value.connectionId : null;
  }
  if (refusal.value && missing.value.length > 0 && chosen.value) return withCode(chosen.value.id) ? chosen.value.id : null;
  return null;
});

async function reconnect(asked?: { connectionId: string; scopes: string[] }) {
  const target = asked ?? (chosen.value ? { connectionId: chosen.value.id, scopes: missing.value } : null);
  if (!target || reconnecting.value) return;

  reconnecting.value = true;
  reconnectProblem.value = '';
  reconnectDirectoryProblem.value = '';
  reconnectDone.value = '';

  try {
    if (withCode(target.connectionId)) {
      const tenant = reconnectDirectory.value.trim();
      // A fresh object each time, so pressing Reconnect again starts the code over.
      const code = await startDeviceConnection({
        reconnectId: target.connectionId,
        scopes: target.scopes,
        flow: 'device',
        ...(tenant ? { tenant } : {}),
      });
      reconnectCode.value = { connectionId: target.connectionId, code: { ...code } };
      return;
    }
    const flow = await startConnection({ reconnectId: target.connectionId, scopes: target.scopes });
    goTo(flow.authorizationUrl);
  } catch (cause) {
    const text = cause instanceof Error ? cause.message : String(cause);
    if (withCode(target.connectionId) && directoryRefusal(text)) reconnectDirectoryProblem.value = text;
    else reconnectProblem.value = text;
  } finally {
    reconnecting.value = false;
  }
}

// --- Reconnect with a code ------------------------------------------------------------------------

/** The code shown for a Microsoft Reconnect, and the connection it is for; null when none is. */
const reconnectCode = ref<{ connectionId: string; code: ConnectionDeviceStart } | null>(null);
/** The code ended without a connection: the refused or expired code stays shown with its sentence. */
const reconnectEnded = ref(false);
/** "Reconnected Work (person@example.test).", once the code was approved and the connection read again. */
const reconnectDone = ref('');

watch(reconnectCode, () => (reconnectEnded.value = false));

async function codeEnded(read: ConnectionFlow) {
  const asked = reconnectCode.value;
  if (!asked) return;
  if (read.state !== 'done') {
    reconnectEnded.value = true;
    return;
  }

  reconnectCode.value = null;
  try {
    const id = read.connection?.id ?? asked.connectionId;
    const connection = (await listConnections()).find((candidate) => candidate.id === id);
    if (!connection) {
      reconnectProblem.value = 'The sign-in came back, but the connection could not be found. Choose it again.';
      return;
    }
    reconnectDone.value = `Reconnected ${connection.name} (${connection.account}).`;
    // Bound as a connection made from this slot is: the Host checks the binding again.
    await connected(connection);
  } catch (cause) {
    reconnectProblem.value = cause instanceof Error ? cause.message : String(cause);
  }
}

// --- Connect, from this slot ----------------------------------------------------------------------

/**
 * Connect is offered while nothing suitable is bound - none, or one that no longer exists - to a
 * slot Add connection can sign in for. A custom provider's slot says where its connection is made.
 */
const offerConnect = computed(() => !chosen.value && !!props.plugin && guidedProviders(props.spec).length > 0);

const binding = ref(false);
const bindRefusal = ref('');
const bindReconnect = ref<{ connectionId: string; scopes: string[] } | null>(null);

async function connected(connection: Connection) {
  emit('connected', connection);
  bindRefusal.value = '';
  bindReconnect.value = null;

  if (!props.bind) {
    bound.value = connection.id;
    return;
  }

  binding.value = true;
  try {
    await props.bind(connection.id);
  } catch (cause) {
    bindRefusal.value = cause instanceof Error ? cause.message : String(cause);
    bindReconnect.value = refusedReconnect(cause);
  } finally {
    binding.value = false;
  }
}
</script>

<template>
  <div class="connection-picker" :data-connection-slot="slotName">
    <!-- AN OPTIONAL SLOT SAYS SO, and what it is for in the manifest's own words, so its Connect
         does not read as a step every person must take; its button is the quiet one. -->
    <div v-if="!spec.required" class="text-caption os-text-muted q-mb-xs" data-slot-optional>
      <span class="text-weight-medium">{{ slotName }} (optional)</span><template v-if="spec.description">: {{ spec.description }}</template>
    </div>
    <SlotConnect
      v-if="plugin"
      class="q-mb-xs"
      :plugin="plugin"
      :plugin-name="pluginName"
      :slot-name="slotName"
      :spec="spec"
      :providers="providers"
      :connections="connections"
      :offered="offerConnect"
      :quiet="!spec.required"
      :busy="binding"
      @connected="connected"
      @closed="emit('closed')"
    >
      <div v-if="choices.length > 0" class="text-caption os-text-muted q-mt-xs">Or choose an existing connection:</div>
    </SlotConnect>

    <!-- The slot's choices are ConnectionSelect's: its kinds and providers, the team's, the one bound;
         what New connection... makes is bound by `connected`, never chosen by the select itself. -->
    <ConnectionSelect
      :model-value="bound === '' ? null : bound"
      :connections="choices"
      :catalog="providers"
      :for-slot="spec"
      :need="wizardNeed"
      :option-label="optionLabel"
      clearable
      no-select-created
      :label="label ?? `Connection for ${slotName}`"
      :hint="hint ?? slotHint(spec, providers)"
      data-connection-select
      @update:model-value="picked"
      @created="connected"
    />

    <div v-if="choices.length === 0 && !offerConnect" class="text-caption os-text-muted q-mt-xs" data-no-connections>
      {{
        assigned
          ? "No connection of that kind is assigned to this team. Choose New connection..., or assign one in Team settings → Connections."
          : 'No connection of that kind yet. Choose New connection..., or connect one in Admin → Connections.'
      }}
    </div>

    <div v-if="unbound" class="text-caption os-text-muted q-mt-xs" data-slot-unbound>
      Required: the member's runs are blocked until a connection is chosen here.
    </div>

    <div v-if="refusal" class="text-caption text-negative q-mt-xs row items-center q-gutter-x-sm" data-binding-refusal>
      <span>{{ refusal }}</span>
      <SlotReconnect
        v-if="missing.length > 0 && chosen && reconnectInTab && !withCode(chosen.id)"
        :connection-id="chosen.id"
        :scopes="missing"
        :provider-name="providerOf(chosen.id)"
        @reconnected="reconnectedHere"
      />
      <q-btn
        v-else-if="missing.length > 0"
        flat
        dense
        no-caps
        size="sm"
        color="primary"
        label="Reconnect"
        :loading="reconnecting"
        @click="reconnect()"
      />
    </div>
    <div v-if="bindRefusal" class="text-caption text-negative q-mt-xs row items-center q-gutter-x-sm" data-bind-refused>
      <span>{{ bindRefusal }}</span>
      <SlotReconnect
        v-if="bindReconnect && reconnectInTab && !withCode(bindReconnect.connectionId)"
        :connection-id="bindReconnect.connectionId"
        :scopes="bindReconnect.scopes"
        :provider-name="providerOf(bindReconnect.connectionId)"
        @reconnected="reconnectedHere"
      />
      <q-btn
        v-else-if="bindReconnect"
        flat
        dense
        no-caps
        size="sm"
        color="primary"
        label="Reconnect"
        :loading="reconnecting"
        @click="reconnect(bindReconnect)"
      />
    </div>
    <!-- RECONNECT WITH A CODE: Microsoft's, shown here as Add connection shows it. -->
    <div v-if="reconnectCode" class="q-mt-sm" data-reconnect-code>
      <div v-if="!reconnectEnded" class="text-caption q-mb-xs">
        Reconnect {{ connectionName(reconnectCode.connectionId) }} by signing in again at {{ providerOf(reconnectCode.connectionId) }} with this code.
      </div>
      <CodeSignIn
        :code="reconnectCode.code"
        :provider-name="providerOf(reconnectCode.connectionId)"
        @ended="codeEnded"
        @cancelled="reconnectCode = null"
        @problem="(text: string) => (reconnectProblem = text)"
      />
    </div>
    <!-- A MICROSOFT RECONNECT'S DIRECTORY, optional: sent as `tenant` only when typed. -->
    <div v-if="directoryFor" class="q-mt-xs" data-reconnect-directory>
      <DirectoryField v-model="reconnectDirectory" :error="reconnectDirectoryProblem" />
      <div class="text-caption os-text-muted" data-reconnect-directory-hint>{{ UsernameIncorrectHint }}</div>
    </div>
    <div v-if="reconnectDone" class="text-caption text-positive q-mt-xs" data-reconnect-done>{{ reconnectDone }}</div>
    <div v-if="reconnectProblem" class="text-caption text-negative q-mt-xs" data-reconnect-problem>
      {{ reconnectProblem }}
    </div>
  </div>
</template>
