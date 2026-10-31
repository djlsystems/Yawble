<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { listConnections } from '../api/client';
import type { Connection } from '../api/types';
import {
  addMcpServer,
  changeMcpServer,
  healthLabel,
  listMcpServers,
  McpNameRule,
  removeMcpServer,
  testMcpServer,
  type McpServer,
  type McpServerCheck,
  type McpServerKind,
  type McpServerSave,
} from '../api/mcpServers';
import { productTitle } from '../presentation/product';

/** The second kind's name, in the product's own words. */
const runsIt = `${productTitle} runs it`;

/**
 * ADMIN → MCP SERVERS (MCP 1): the instance's list, which Team settings → MCP servers gives to teams.
 *
 * An entry is a WEB ADDRESS - optionally sent an API key connection's value in a header made from a
 * template holding `{value}` - checked by the Host on every save and by Test: it connects, initializes
 * and lists the server's tools, and this dialog shows their names and how many. A refused credential
 * marks the entry and its connection Needs you; a server not reached fails the check with a sentence
 * and marks neither. Or THE PLATFORM RUNS IT: a command, a package at one exact version, fixed arguments, and
 * environment variables each filled from a connection's field - stored and validated now, checked when
 * MCP 3 is in place.
 *
 * Never a value: an entry names its connection and field, and its address is shown without its query;
 * an edit that leaves the address empty keeps the stored one whole.
 */
const open = defineModel<boolean>({ required: true });

const servers = ref<McpServer[]>([]);
const connections = ref<Connection[]>([]);
const loaded = ref(false);
const error = ref('');

async function load() {
  error.value = '';
  try {
    const [list, all] = await Promise.all([listMcpServers(), listConnections()]);
    servers.value = list;
    connections.value = all;
    loaded.value = true;
  } catch (cause) {
    error.value = `The MCP servers could not be read: ${cause instanceof Error ? cause.message : String(cause)}`;
  }
}

watch(open, (showing) => {
  if (showing) void load();
}, { immediate: true });

const apiKeys = computed(() => connections.value.filter((c) => c.kind === 'apikey'));
const credentialConnections = computed(() => connections.value.filter((c) => c.kind === 'apikey' || c.kind === 'login' || c.kind === 'website'));

function fieldsOf(id: string | null | undefined): string[] {
  const connection = connections.value.find((c) => c.id === id);
  return connection?.fields?.map((f) => f.name) ?? [];
}

function describe(server: McpServer): string {
  if (server.kind === 'runs') return `${server.command} ${server.package}@${server.version}`;
  return server.connection ? `${server.url} · sends ${server.connection.name ?? server.connection.id}` : server.url ?? '';
}

// --- the form: add and edit ----------------------------------------------------------------------

interface EnvRow {
  name: string;
  connection: string | null;
  field: string | null;
}

const editing = ref<McpServer | null>(null);
const formOpen = ref(false);
const name = ref('');
const kind = ref<McpServerKind>('url');
const url = ref('');
const connection = ref<string | null>(null);
const field = ref<string | null>(null);
const header = ref('Authorization');
const template = ref('Bearer {value}');
const command = ref<'npx' | 'uvx'>('npx');
const pkg = ref('');
const version = ref('');
const args = ref('');
const env = ref<EnvRow[]>([]);
const saving = ref(false);
const formError = ref('');

/** The last check, shown under the list: the sentence, and the tools when it was ok. */
const check = ref<{ name: string; check: McpServerCheck } | null>(null);

function startAdd() {
  editing.value = null;
  name.value = '';
  kind.value = 'url';
  url.value = '';
  connection.value = null;
  field.value = null;
  header.value = 'Authorization';
  template.value = 'Bearer {value}';
  command.value = 'npx';
  pkg.value = '';
  version.value = '';
  args.value = '';
  env.value = [];
  formError.value = '';
  formOpen.value = true;
}

function startEdit(server: McpServer) {
  editing.value = server;
  name.value = server.name;
  kind.value = server.kind;
  url.value = '';
  connection.value = server.connection?.id ?? null;
  field.value = server.field;
  header.value = server.header ?? 'Authorization';
  template.value = server.template ?? 'Bearer {value}';
  command.value = server.command ?? 'npx';
  pkg.value = server.package ?? '';
  version.value = server.version ?? '';
  args.value = server.args.join('\n');
  env.value = server.env.map((e) => ({ name: e.name, connection: e.connection, field: e.field }));
  formError.value = '';
  formOpen.value = true;
}

const nameOk = computed(() => McpNameRule.test(name.value.trim()));

function request(): McpServerSave {
  const save: McpServerSave = { name: name.value.trim(), kind: kind.value };
  if (kind.value === 'url') {
    if (url.value.trim()) save.url = url.value.trim();
    save.connection = connection.value;
    if (connection.value) {
      save.field = field.value;
      save.header = header.value.trim();
      save.template = template.value.trim();
    }
  } else {
    save.command = command.value;
    save.package = pkg.value.trim();
    save.version = version.value.trim();
    save.args = args.value.split('\n').map((a) => a.trim()).filter((a) => a.length > 0);
    save.env = env.value.map((e) => ({ name: e.name.trim(), connection: e.connection ?? '', field: e.field ?? '' }));
  }
  return save;
}

async function save() {
  if (saving.value) return;
  saving.value = true;
  formError.value = '';
  try {
    const saved = editing.value ? await changeMcpServer(editing.value.id, request()) : await addMcpServer(request());
    check.value = saved.check ? { name: saved.server.name, check: saved.check } : null;
    formOpen.value = false;
    await load();
  } catch (cause) {
    formError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    saving.value = false;
  }
}

// --- Test and Remove -----------------------------------------------------------------------------

const testing = ref<string | null>(null);

async function test(server: McpServer) {
  testing.value = server.id;
  error.value = '';
  try {
    const answer = await testMcpServer(server.id);
    check.value = { name: server.name, check: answer.check };
    await load();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    testing.value = null;
  }
}

const removing = ref<McpServer | null>(null);
const removeError = ref('');

async function remove() {
  const server = removing.value;
  if (!server) return;
  removeError.value = '';
  try {
    await removeMcpServer(server.id);
    removing.value = null;
    if (check.value?.name === server.name) check.value = null;
    await load();
  } catch (cause) {
    removeError.value = cause instanceof Error ? cause.message : String(cause);
  }
}
</script>

<template>
  <q-dialog v-model="open" no-route-dismiss>
    <q-card class="os-dialog-xl" data-mcp-servers-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">MCP servers</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        The MCP servers this instance knows. A server is added once here and given to the teams that need
        it in Team settings → MCP servers. Its name prefixes its tools' names.
      </q-card-section>

      <q-card-section class="row items-center q-py-sm">
        <q-btn color="primary" no-caps icon="add" label="Add MCP server" :disable="!loaded" data-mcp-add @click="startAdd" />
      </q-card-section>

      <q-card-section v-if="error" class="q-py-none">
        <q-banner dense class="os-bg-tint-error text-negative q-mb-md" data-mcp-error>{{ error }}</q-banner>
      </q-card-section>

      <q-card-section v-if="check" class="q-py-none">
        <q-banner
          dense
          :class="check.check.outcome === 'ok' ? 'os-bg-tint-ok q-mb-md' : 'os-bg-tint-error text-negative q-mb-md'"
          data-mcp-check
        >
          <div class="text-weight-medium">{{ check.name }}: {{ check.check.sentence }}</div>
          <div v-if="check.check.tools" data-mcp-check-tools>
            {{ check.check.toolCount }} tool{{ check.check.toolCount === 1 ? '' : 's' }}<span v-if="check.check.tools.length > 0">:
              {{ check.check.tools.join(', ') }}</span>
          </div>
        </q-banner>
      </q-card-section>

      <q-card-section class="q-pt-none">
        <div v-if="loaded && servers.length === 0" class="os-text-muted" data-mcp-empty>
          No MCP servers yet. Add one, then give it to a team in Team settings.
        </div>

        <q-list v-else bordered separator class="rounded-borders">
          <q-item v-for="server in servers" :key="server.id" :data-mcp-server="server.name">
            <q-item-section>
              <q-item-label class="text-weight-medium">
                {{ server.name }}
                <span class="text-caption os-text-muted"> · {{ server.kind === 'url' ? 'Web address' : runsIt }}</span>
              </q-item-label>
              <q-item-label caption>{{ describe(server) }}</q-item-label>
              <q-item-label caption data-mcp-health>
                <span :class="server.health === 'needs-you' ? 'text-warning text-weight-medium' : ''">{{ healthLabel(server) }}</span>
                <span v-if="server.kind === 'url' && server.healthReason"> - {{ server.healthReason }}</span>
                <span v-if="server.kind === 'url' && server.toolCount !== null"> · {{ server.toolCount }} tool{{ server.toolCount === 1 ? '' : 's' }}</span>
              </q-item-label>
              <q-item-label caption>{{ server.teams.length > 0 ? `Given to ${server.teams.join(', ')}` : 'Not given to any team yet.' }}</q-item-label>
            </q-item-section>
            <q-item-section side>
              <div class="row no-wrap q-gutter-xs">
                <q-btn
                  v-if="server.kind === 'url'"
                  flat
                  dense
                  no-caps
                  label="Test"
                  :loading="testing === server.id"
                  data-mcp-test
                  @click="test(server)"
                />
                <q-btn flat dense no-caps label="Edit" data-mcp-edit @click="startEdit(server)" />
                <q-btn flat dense no-caps color="negative" label="Remove" data-mcp-remove @click="removing = server; removeError = ''" />
              </div>
            </q-item-section>
          </q-item>
        </q-list>
      </q-card-section>
    </q-card>

    <!-- ADD AND EDIT. Saving a web address checks it; the answer shows above the list. -->
    <q-dialog v-model="formOpen" no-backdrop-dismiss>
      <q-card class="os-dialog-md" data-mcp-form>
        <q-card-section class="os-dialog-title">{{ editing ? `Edit ${editing.name}` : 'Add MCP server' }}</q-card-section>
        <q-card-section class="q-gutter-y-sm">
          <q-input
            v-model="name"
            outlined
            dense
            label="Name"
            hint="Letters, digits, - and _, at most 20. It prefixes the server's tools' names."
            :error="name.length > 0 && !nameOk"
            data-mcp-name
          />
          <q-btn-toggle
            v-model="kind"
            no-caps
            unelevated
            toggle-color="primary"
            :options="[{ label: 'Web address', value: 'url' }, { label: runsIt, value: 'runs' }]"
            data-mcp-kind
          />

          <template v-if="kind === 'url'">
            <q-input
              v-model="url"
              outlined
              dense
              label="Address"
              :placeholder="editing?.url ? `${editing.url} (leave empty to keep it)` : 'https://example.com/mcp'"
              data-mcp-url
            />
            <q-select
              v-model="connection"
              :options="apiKeys.map((c) => ({ value: c.id, label: c.name }))"
              emit-value
              map-options
              clearable
              outlined
              dense
              label="Connection (API key, optional)"
              data-mcp-connection
            />
            <template v-if="connection">
              <q-select v-model="field" :options="fieldsOf(connection)" clearable outlined dense label="Field (default: its secret field)" />
              <q-input v-model="header" outlined dense label="Header name" data-mcp-header />
              <q-input v-model="template" outlined dense label="Value template" hint="{value} is replaced by the connection's value." data-mcp-template />
            </template>
          </template>

          <template v-else>
            <q-select v-model="command" :options="['npx', 'uvx']" outlined dense label="Command" data-mcp-command />
            <q-input v-model="pkg" outlined dense label="Package" data-mcp-package />
            <q-input v-model="version" outlined dense label="Version" hint="One exact version, such as 1.4.2." data-mcp-version />
            <q-input v-model="args" type="textarea" autogrow outlined dense label="Arguments, one per line" data-mcp-args />
            <div class="text-caption os-text-muted">Environment variables, each filled from a connection's field</div>
            <div v-for="(row, index) in env" :key="index" class="row no-wrap q-gutter-x-sm items-center">
              <q-input v-model="row.name" class="col" outlined dense label="Name" />
              <q-select
                v-model="row.connection"
                class="col"
                :options="credentialConnections.map((c) => ({ value: c.id, label: c.name }))"
                emit-value
                map-options
                outlined
                dense
                label="Connection"
              />
              <q-select v-model="row.field" class="col" :options="fieldsOf(row.connection)" outlined dense label="Field" />
              <q-btn flat round dense icon="delete" :aria-label="`Remove ${row.name || 'variable'}`" @click="env.splice(index, 1)" />
            </div>
            <q-btn flat dense no-caps icon="add" label="Add variable" @click="env.push({ name: '', connection: null, field: null })" />
            <div class="text-caption os-text-muted">Checked when MCP 3 is in place.</div>
          </template>

          <div v-if="formError" class="text-negative" data-mcp-form-error>{{ formError }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="formOpen = false" />
          <q-btn color="primary" no-caps label="Save" :disable="!nameOk" :loading="saving" data-mcp-save @click="save" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <!-- REMOVING ASKS FIRST, naming the teams that lose it. -->
    <q-dialog :model-value="removing !== null" no-backdrop-dismiss @update:model-value="(showing) => { if (!showing) removing = null; }">
      <q-card class="os-dialog-sm" data-mcp-confirm>
        <q-card-section class="os-dialog-title">Remove {{ removing?.name }}?</q-card-section>
        <q-card-section class="q-pt-none">
          {{ removing && removing.teams.length > 0
            ? `It is taken from ${removing.teams.join(', ')} too.`
            : 'No team has it.' }}
        </q-card-section>
        <q-card-section v-if="removeError" class="text-negative q-pt-none">{{ removeError }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="removing = null" />
          <q-btn color="negative" no-caps label="Remove" data-mcp-confirm-remove @click="remove" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-dialog>
</template>
