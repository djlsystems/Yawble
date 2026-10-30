<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import {
  ActionRefused,
  assignTeamConnection,
  listConnectionProviders,
  listConnections,
  listTeamConnections,
  removeTeamConnection,
} from '../api/client';
import { asTeamId, type Connection, type ConnectionProvider, type ConnectionUse, type TeamConnectionEntry } from '../api/types';
import { kindLabel, providerName, statusLabel } from '../lib/connections';
import ConnectDialog from './ConnectDialog.vue';

/**
 * WHICH CONNECTIONS A TEAM MAY USE: Team settings → Connections and New team → Connections. A
 * connection is set up once (Admin → Connections) and assigned to as many teams as a person chooses;
 * a plugin member of the team binds only one assigned here. A team starts with none.
 *
 * WITH `team` (Team settings) every change is the Host's at once: Add assigns, Remove asks first -
 * naming the members of this team that use it, from the list and then from the Host's 409 - and,
 * confirmed, removes it and unbinds them in one transaction (each then reads Needs you). New
 * connection... opens the same wizard as Admin → Connections and assigns what it saves to this team.
 *
 * WITHOUT `team` (New team, before the team exists) the choice is the `chosen` model: the ids the
 * create sends as `connections`, so the team is made with them. Nothing is bound yet, so a removal
 * asks nothing. New connection... still saves the connection at once (it exists on its own), and
 * chooses it here.
 *
 * Never a token or a masked value: only what `GET /api/connections` lists.
 */
const props = defineProps<{
  /** The team, once it exists: changes go to the Host at once. Absent in New team. */
  team?: string | undefined;
}>();

/** New team's choice, the ids to assign at creation. Unused once `team` is given. */
const chosen = defineModel<string[]>('chosen', { default: () => [] });

const all = ref<Connection[]>([]);
const providers = ref<ConnectionProvider[]>([]);
const entries = ref<TeamConnectionEntry[]>([]);
const loading = ref(false);
const problem = ref('');

async function load() {
  loading.value = true;
  problem.value = '';
  try {
    const team = props.team;
    const [connections, kinds, assigned] = await Promise.all([
      listConnections(),
      listConnectionProviders().catch(() => [] as ConnectionProvider[]),
      team ? listTeamConnections(asTeamId(team)) : Promise.resolve(null),
    ]);
    all.value = connections;
    providers.value = kinds;
    if (assigned) entries.value = assigned.connections;
  } catch (cause) {
    problem.value = `The connections could not be read: ${cause instanceof Error ? cause.message : String(cause)}`;
  } finally {
    loading.value = false;
  }
}

onMounted(() => void load());
watch(() => props.team, () => void load());

/** What the list shows: the Host's assignments, or New team's choice read from every connection. */
const listed = computed<Connection[]>(() =>
  props.team
    ? entries.value.map((entry) => entry.connection)
    : chosen.value.map((id) => all.value.find((connection) => connection.id === id)).filter((c): c is Connection => !!c),
);

const listedIds = computed(() => new Set(listed.value.map((connection) => connection.id)));

function describe(connection: Connection): string {
  const provider = connection.provider && connection.kind === 'oauth' ? ` · ${providerName(connection.provider, providers.value)}` : '';
  return `${kindLabel(connection)}${provider}${connection.account ? ` · ${connection.account}` : ''}`;
}

/** Add connection's pick list: every connection not on the team yet, by name. */
const addOptions = computed(() =>
  all.value
    .filter((connection) => !listedIds.value.has(connection.id))
    .sort((a, b) => a.name.localeCompare(b.name))
    .map((connection) => ({ value: connection.id, label: `${connection.name} - ${describe(connection)}` })),
);

const adding = ref<string | null>(null);
const busy = ref(false);

async function add(id: string | null) {
  adding.value = null;
  if (!id || busy.value) return;

  if (!props.team) {
    if (!chosen.value.includes(id)) chosen.value = [...chosen.value, id];
    return;
  }

  busy.value = true;
  problem.value = '';
  try {
    await assignTeamConnection(asTeamId(props.team), id);
    await load();
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

// --- New connection... ----------------------------------------------------------------------------

const wizardOpen = ref(false);

/** The wizard saved a connection: it is read again with the rest, and assigned (or chosen) here. */
async function made(connection: Connection) {
  all.value = [...all.value.filter((existing) => existing.id !== connection.id), connection];
  await add(connection.id);
}

// --- Remove ---------------------------------------------------------------------------------------

const removing = ref<Connection | null>(null);
/** The members of this team that use it, named before the person confirms. */
const removeUsers = ref<string[]>([]);
const removeProblem = ref('');

function labelsOf(uses: readonly ConnectionUse[]): string[] {
  const byMember = new Map<string, string[]>();
  for (const use of uses) byMember.set(use.label || use.member, [...(byMember.get(use.label || use.member) ?? []), use.slot]);
  return [...byMember].map(([label, slots]) => `${label} (${slots.join(', ')})`);
}

function startRemove(connection: Connection) {
  if (!props.team) {
    chosen.value = chosen.value.filter((id) => id !== connection.id);
    return;
  }
  removing.value = connection;
  removeProblem.value = '';
  removeUsers.value = labelsOf(connection.usedBy.filter((use) => use.team.toLowerCase() === props.team!.toLowerCase()));
}

async function remove() {
  const connection = removing.value;
  if (!connection || !props.team || busy.value) return;

  busy.value = true;
  removeProblem.value = '';
  try {
    // CONFIRMED FOR THE MEMBERS LISTED: with none listed, one bound meanwhile is refused, and named.
    await removeTeamConnection(asTeamId(props.team), connection.id, removeUsers.value.length > 0);
    removing.value = null;
    await load();
  } catch (cause) {
    removeProblem.value = cause instanceof Error ? cause.message : String(cause);
    const usedBy = cause instanceof ActionRefused ? cause.body.usedBy : undefined;
    if (Array.isArray(usedBy)) removeUsers.value = labelsOf(usedBy as ConnectionUse[]);
  } finally {
    busy.value = false;
  }
}

/** The members of this team using a connection, for its row. */
function usersOf(connection: Connection): string[] {
  return props.team ? labelsOf(connection.usedBy.filter((use) => use.team.toLowerCase() === props.team!.toLowerCase())) : [];
}
</script>

<template>
  <div class="team-connections" data-team-connections>
    <div class="os-body os-text-muted q-mb-sm">
      The connections this team's plugin members may use. A member's connection slot lists only these.
      A connection is set up once and can be given to as many teams as you choose.
    </div>

    <div class="row items-center q-gutter-sm q-mb-md no-wrap">
      <q-select
        v-model="adding"
        class="col"
        :options="addOptions"
        emit-value
        map-options
        outlined
        dense
        label="Add connection"
        :disable="busy || loading"
        data-team-connection-add
        @update:model-value="add"
      >
        <template #no-option>
          <q-item>
            <q-item-section class="os-text-muted">Every connection is already on this team. Choose New connection... for another.</q-item-section>
          </q-item>
        </template>
      </q-select>
      <q-btn
        outline
        no-caps
        color="primary"
        icon="add"
        label="New connection..."
        :disable="busy"
        data-team-connection-new
        @click="wizardOpen = true"
      />
    </div>

    <div v-if="problem" class="text-negative text-caption q-mb-sm" data-team-connections-problem>{{ problem }}</div>

    <div v-if="loading && listed.length === 0" class="os-text-muted">Reading the connections…</div>
    <div v-else-if="listed.length === 0" class="os-text-muted" data-team-connections-empty>
      No connections yet. {{ team ? 'This team' : 'The new team' }} starts with none: add one above.
    </div>

    <q-list v-else bordered separator class="rounded-borders">
      <q-item v-for="connection in listed" :key="connection.id" :data-team-connection="connection.name">
        <q-item-section>
          <q-item-label class="text-weight-medium">
            {{ connection.name }}
            <span v-if="connection.status !== 'ok'" class="text-warning text-caption"> · {{ statusLabel(connection) }}</span>
          </q-item-label>
          <q-item-label caption>{{ describe(connection) }}</q-item-label>
          <q-item-label v-if="team" caption data-team-connection-users>
            {{ usersOf(connection).length > 0 ? `Used by ${usersOf(connection).join(', ')}` : 'No member of this team uses it yet.' }}
          </q-item-label>
        </q-item-section>
        <q-item-section side>
          <q-btn
            flat
            dense
            no-caps
            color="negative"
            label="Remove"
            :disable="busy"
            data-team-connection-remove
            @click="startRemove(connection)"
          />
        </q-item-section>
      </q-item>
    </q-list>

    <ConnectDialog v-model="wizardOpen" :providers="providers" :connections="all" @connected="made" />

    <!-- REMOVING ASKS FIRST, naming the members that use it. `no-backdrop-dismiss`: a stray click must
         not answer a destructive question, and Escape cancels. -->
    <q-dialog :model-value="removing !== null" no-backdrop-dismiss @update:model-value="(showing) => { if (!showing) removing = null; }">
      <q-card class="os-dialog-sm" data-team-connection-confirm>
        <q-card-section class="os-dialog-title">Remove {{ removing?.name }} from this team?</q-card-section>
        <q-card-section class="q-pt-none">
          The connection itself stays, and other teams keep it.
        </q-card-section>
        <q-card-section v-if="removeUsers.length > 0" class="q-pt-none" data-team-connection-confirm-users>
          <div>Used by:</div>
          <ul class="q-my-xs">
            <li v-for="label in removeUsers" :key="label">{{ label }}</li>
          </ul>
          <div>They are unbound, and their runs wait for you until another connection is chosen for them.</div>
        </q-card-section>
        <q-card-section v-if="removeProblem" class="text-negative q-pt-none">{{ removeProblem }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="removing = null" />
          <q-btn
            color="negative"
            no-caps
            :label="removeUsers.length > 0 ? 'Remove and unbind' : 'Remove'"
            :loading="busy"
            data-team-connection-confirm-remove
            @click="remove"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
