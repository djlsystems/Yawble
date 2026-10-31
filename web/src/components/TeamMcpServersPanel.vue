<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { asTeamId } from '../api/types';
import {
  assignTeamMcpServer,
  healthLabel,
  listMcpServers,
  listTeamMcpServers,
  removeTeamMcpServer,
  type McpServer,
  type TeamMcpServerEntry,
} from '../api/mcpServers';
import { productTitle } from '../presentation/product';

/**
 * TEAM SETTINGS → MCP SERVERS (MCP 1), beside Connections: which of the instance's MCP servers this team
 * is given. The list itself is Admin → MCP servers'; here a person picks from it. Each change is the
 * Host's at once, with its tenant row; taking one off asks nothing, because nothing on the team binds it
 * yet (MCP 2 makes members use them). A team with none works as it always has.
 */
const props = defineProps<{ team: string }>();

const all = ref<McpServer[]>([]);
const entries = ref<TeamMcpServerEntry[]>([]);
const loading = ref(false);
const busy = ref(false);
const problem = ref('');

async function load() {
  loading.value = true;
  problem.value = '';
  try {
    const [servers, assigned] = await Promise.all([listMcpServers(), listTeamMcpServers(asTeamId(props.team))]);
    all.value = servers;
    entries.value = assigned;
  } catch (cause) {
    problem.value = `The MCP servers could not be read: ${cause instanceof Error ? cause.message : String(cause)}`;
  } finally {
    loading.value = false;
  }
}

onMounted(() => void load());
watch(() => props.team, () => void load());

const listedIds = computed(() => new Set(entries.value.map((entry) => entry.server.id)));

const addOptions = computed(() =>
  all.value
    .filter((server) => !listedIds.value.has(server.id))
    .map((server) => ({ value: server.id, label: `${server.name} - ${server.kind === 'url' ? 'web address' : `${productTitle} runs it`}` })),
);

const adding = ref<string | null>(null);

async function add(id: string | null) {
  adding.value = null;
  if (!id || busy.value) return;
  busy.value = true;
  problem.value = '';
  try {
    await assignTeamMcpServer(asTeamId(props.team), id);
    await load();
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

async function remove(server: McpServer) {
  if (busy.value) return;
  busy.value = true;
  problem.value = '';
  try {
    await removeTeamMcpServer(asTeamId(props.team), server.id);
    await load();
  } catch (cause) {
    problem.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="team-mcp-servers" data-team-mcp-servers>
    <div class="os-body os-text-muted q-mb-sm">
      The MCP servers this team is given, from the instance's list in Admin → MCP servers.
    </div>

    <q-select
      v-model="adding"
      class="q-mb-md"
      :options="addOptions"
      emit-value
      map-options
      outlined
      dense
      label="Add MCP server"
      :disable="busy || loading"
      data-team-mcp-add
      @update:model-value="add"
    >
      <template #no-option>
        <q-item>
          <q-item-section class="os-text-muted">Every MCP server is already on this team, or there are none: add one in Admin → MCP servers.</q-item-section>
        </q-item>
      </template>
    </q-select>

    <div v-if="problem" class="text-negative text-caption q-mb-sm" data-team-mcp-problem>{{ problem }}</div>

    <div v-if="loading && entries.length === 0" class="os-text-muted">Reading the MCP servers…</div>
    <div v-else-if="entries.length === 0" class="os-text-muted" data-team-mcp-empty>
      No MCP servers yet. This team works as it always has until it is given one.
    </div>

    <q-list v-else bordered separator class="rounded-borders">
      <q-item v-for="entry in entries" :key="entry.server.id" :data-team-mcp-server="entry.server.name">
        <q-item-section>
          <q-item-label class="text-weight-medium">
            {{ entry.server.name }}
            <span v-if="entry.server.health === 'needs-you'" class="text-warning text-caption"> · Needs you</span>
          </q-item-label>
          <q-item-label caption>
            {{ entry.server.kind === 'url' ? entry.server.url : `${entry.server.command} ${entry.server.package}@${entry.server.version}` }}
            · {{ healthLabel(entry.server) }}
          </q-item-label>
        </q-item-section>
        <q-item-section side>
          <q-btn flat dense no-caps color="negative" label="Remove" :disable="busy" data-team-mcp-remove @click="remove(entry.server)" />
        </q-item-section>
      </q-item>
    </q-list>
  </div>
</template>
