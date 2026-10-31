<script setup lang="ts">
import { computed, ref } from 'vue';
import type { Connection, ConnectionKind, ConnectionProvider, ConnectionSlot } from '../api/types';
import { connectionsForSlot, kindLabel, kindOf, providerName, slotAllows, slotKinds, statusLabel } from '../lib/connections';
import ConnectDialog from './ConnectDialog.vue';
import FieldHelp from './FieldHelp.vue';

defineOptions({ inheritAttrs: false });

/**
 * A CONNECTION IS MADE WHEREVER ONE IS ASKED FOR. This is the select every place that chooses a
 * connection uses: the existing connections that place accepts, and as the LAST entry, always,
 * **New connection...**.
 *
 * WHAT IS LISTED: the connections of the accepted `kinds` and `providers`; for a plugin's slot
 * (`forSlot`), what ConnectionPicker has always filtered on - the slot's providers, and of those only the
 * team's (`assigned`) when given. The connection chosen stays listed whatever it is, so the select
 * shows it. `exclude` leaves out ones the place already has (Team settings -> Connections).
 *
 * NEW CONNECTION... opens the connection wizard (ConnectDialog) over the dialog this sits in, offering
 * only the kinds the place accepts (`start` opens it at one kind's form and preset, such as API key's
 * Other). Nothing typed underneath is touched: the wizard is a dialog of its own, a sign-in that would
 * leave the page opens in another tab and waits (`inTab`, as the solution wizard's Reconnect does), and
 * a Microsoft one shows its code in place (CodeSignIn). On save the new connection is listed and chosen
 * at once - `created` says which, so the opener reads its lists again and whatever depends on the
 * choice follows it; with `noSelectCreated` (a slot that binds through the Host) the opener chooses it.
 * Cancel leaves the select as it was: New connection... is never a value.
 */
const props = defineProps<{
  /** Every connection the place may offer, from `GET /api/connections`. */
  connections: Connection[];
  /** The provider definitions, for names and the wizard's sign-ins. */
  catalog?: ConnectionProvider[] | undefined;
  /** The kinds the place accepts: only these are listed and offered. All when absent. */
  kinds?: readonly ConnectionKind[] | undefined;
  /** The providers the place accepts: only their connections are listed, and only their sign-ins offered. */
  providers?: readonly string[] | undefined;
  /** A plugin's slot: what ConnectionPicker filters on - its providers (and kinds), and `assigned`. */
  forSlot?: ConnectionSlot | undefined;
  /** The ids of the connections assigned to the team: with a slot, only these are listed. */
  assigned?: readonly string[] | undefined;
  /** The slot the wizard is opened for, as ConnectDialog takes it: its scopes, and the plugin asking. */
  need?: InstanceType<typeof ConnectDialog>['$props']['need'];
  /** Connections not to list (the ones a team already has). */
  exclude?: readonly string[] | undefined;
  /** Where the wizard opens: a kind, and a preset of it (API key, Other). */
  start?: { kind: ConnectionKind; preset?: string | undefined } | null | undefined;
  label?: string | undefined;
  /** The field's help, beside it. */
  hint?: string | undefined;
  clearable?: boolean | undefined;
  disable?: boolean | undefined;
  /** How each connection reads in the list. */
  optionLabel?: ((connection: Connection) => string) | undefined;
  /** A connection made here is not chosen by the select: the opener chooses it (a slot binding at the Host). */
  noSelectCreated?: boolean | undefined;
}>();

/** The chosen connection's id; null (or '') when none is. */
const chosen = defineModel<string | null>({ default: null });

const emit = defineEmits<{
  /** New connection... saved this connection: read the lists again. */
  created: [connection: Connection];
}>();

/** The value of the list's New connection... entry: never a connection's id. */
const NewConnection = '__new_connection__';

/** Connections made here that the opener's list does not hold yet: listed at once, until it does. */
const made = ref<Connection[]>([]);

const all = computed(() => [
  ...props.connections,
  ...made.value.filter((connection) => !props.connections.some((listed) => listed.id === connection.id)),
]);

/** The kinds the place accepts: its own, else those of a slot's (or the accepted) providers. */
const acceptedKinds = computed(() => {
  if (props.kinds) return props.kinds;
  const providers = props.forSlot?.providers ?? props.providers;
  return providers ? slotKinds(providers) : undefined;
});
const acceptedProviders = computed(() => props.providers ?? props.forSlot?.providers);

const choices = computed(() => {
  const current = chosen.value ?? '';
  const given = props.assigned ? new Set(props.assigned) : null;
  const left = new Set(props.exclude ?? []);
  const base = props.forSlot
    ? connectionsForSlot(props.forSlot, all.value).filter((connection) => !given || given.has(connection.id))
    : [...all.value].sort((a, b) => a.name.localeCompare(b.name));
  const kinds = props.kinds;
  const providers = props.providers;
  const fits = base.filter(
    (connection) =>
      !left.has(connection.id)
      && (!kinds || kinds.includes(kindOf(connection)))
      && (!providers || slotAllows({ providers: [...providers] } as ConnectionSlot, connection)),
  );
  // The one chosen stays listed whatever it is, so the select shows it.
  const kept = current && !fits.some((connection) => connection.id === current) ? all.value.find((connection) => connection.id === current) : undefined;
  return kept ? [kept, ...fits] : fits;
});

function describe(connection: Connection): string {
  if (props.optionLabel) return props.optionLabel(connection);
  const way = kindOf(connection) === 'oauth' ? providerName(connection.provider, props.catalog ?? []) : kindLabel(connection);
  return (
    `${connection.name}${connection.account ? ` - ${connection.account}` : ''} (${way})` +
    (connection.status === 'ok' ? '' : `, ${statusLabel(connection)}`)
  );
}

const options = computed(() =>
  choices.value
    .map((connection) => ({ value: connection.id, label: describe(connection) }))
    .concat([{ value: NewConnection, label: 'New connection...' }]),
);

const wizardOpen = ref(false);

function picked(value: string | null) {
  if (value === NewConnection) {
    wizardOpen.value = true;
    return;
  }
  chosen.value = value;
}

function created(connection: Connection) {
  made.value = [...made.value.filter((existing) => existing.id !== connection.id), connection];
  emit('created', connection);
  if (!props.noSelectCreated) chosen.value = connection.id;
}

const accepts = computed(() => ({ kinds: acceptedKinds.value, providers: acceptedProviders.value }));

defineExpose({ open: () => (wizardOpen.value = true) });
</script>

<template>
  <q-select
    v-bind="$attrs"
    :model-value="chosen === '' || chosen === null ? null : chosen"
    :options="options"
    emit-value
    map-options
    outlined
    dense
    :clearable="clearable"
    :disable="disable"
    :label="label"
    @update:model-value="picked"
  >
    <template v-if="hint" #append><FieldHelp :text="hint" /></template>
  </q-select>
  <ConnectDialog
    v-model="wizardOpen"
    :providers="catalog ?? []"
    :connections="all"
    :need="need ?? null"
    :accepts="accepts"
    :start="start ?? null"
    in-tab
    @connected="created"
  />
</template>
