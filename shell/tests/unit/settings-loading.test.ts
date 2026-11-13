// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import AgentsDialog from '../../src/renderer/src/components/AgentsDialog.vue';
import AgentCommandLineDialog from '../../src/renderer/src/components/AgentCommandLineDialog.vue';
import AgentModelDialog from '../../src/renderer/src/components/AgentModelDialog.vue';
import AgentEditDialog from '../../src/renderer/src/components/AgentEditDialog.vue';
import SkillsDialog from '../../src/renderer/src/components/SkillsDialog.vue';
import HealthPanel from '../../src/renderer/src/components/HealthPanel.vue';
import GlobalPolicyDialog from '../../src/renderer/src/components/GlobalPolicyDialog.vue';
import IntegrationsPanel from '../../src/renderer/src/components/IntegrationsPanel.vue';
import ApisPanel from '../../src/renderer/src/components/ApisPanel.vue';
import McpServersPanel from '../../src/renderer/src/components/McpServersPanel.vue';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
import TasksPanel from '../../src/renderer/src/components/TasksPanel.vue';
import LinkDialog from '../../src/renderer/src/components/LinkDialog.vue';
import CompanionPanel from '../../src/renderer/src/components/CompanionPanel.vue';
import BackupsSection from '../../src/renderer/src/components/BackupsSection.vue';
import SystemPanel from '../../src/renderer/src/components/SystemPanel.vue';
import LoadingNote from '../../src/renderer/src/components/LoadingNote.vue';
import type { DetectedCli, YawbleApi } from '../../src/shared/api';

// Settings tabs and the Agents dialogs that fetch on open show one loading state (LoadingNote) until their first
// answer arrives. A fake API whose every call stays pending until the test answers it: no core, no CLI, no network.
function pendingApi() {
  const waiting = new Map<string, { resolve: (v: unknown) => void; reject: (e: unknown) => void }[]>();
  const calls: string[] = [];
  let info: ((v: unknown) => void) | null = null;
  const api = {
    getInfo: () => new Promise((resolve) => { info = resolve; }),
    getAvailableBackends: async () => [],
    call: (method: string) => {
      calls.push(method);
      return new Promise((resolve, reject) => {
        const list = waiting.get(method) ?? [];
        list.push({ resolve, reject });
        waiting.set(method, list);
      });
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  async function answer(method: string, value: unknown) {
    const list = waiting.get(method);
    if (!list?.length) throw new Error(`nothing is waiting for ${method} (called: ${calls.join(', ')})`);
    waiting.delete(method);
    for (const w of list) w.resolve(value);
    await flushPromises();
  }
  async function fail(method: string, message: string) {
    const list = waiting.get(method) ?? [];
    waiting.delete(method);
    for (const w of list) w.reject(new Error(message));
    await flushPromises();
  }
  async function answerInfo(value: unknown) { info?.(value); await flushPromises(); }
  return { api, calls, answer, fail, answerInfo };
}

const notes = (w: VueWrapper) => w.findAll('[data-test=loading-note]').map((n) => n.text());

const claude: DetectedCli = {
  cliId: 'claude', found: true, launcherPath: '/bin/claude', executablePath: '/opt/claude/2.2.0', version: '2.2.0', installKind: 'native',
  readOnlyFolders: ['/opt/claude'], configFolders: ['/home/u/.claude'], problems: [], installHelp: '',
};
const team = { id: 1, name: 'alpha', repoPath: null, members: 1, running: 0, queued: 0 };

let wrapper: VueWrapper | null = null;
afterEach(() => { wrapper?.unmount(); wrapper = null; });
function keep(w: VueWrapper) { wrapper = w; return w; }

describe('LoadingNote', () => {
  it('is a status line with a spinner and its text', () => {
    const w = keep(mount(LoadingNote, { props: { text: 'Loading skills…' } }));
    expect(w.attributes('role')).toBe('status');
    expect(w.find('.spinner').exists()).toBe(true);
    expect(w.text()).toBe('Loading skills…');
  });
});

describe('settings tabs: a loading state until the first answer', () => {
  it('Agents: the tiles area shows it while agents.detect runs; a tile keeps its model line while its data loads', async () => {
    const f = pendingApi();
    const w = keep(mount(AgentsDialog, { props: { api: f.api, inline: true } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading agents (looking for the agent CLIs on this computer)…']);
    expect(w.find('[data-test=agent-cli-claude]').exists()).toBe(false);

    await f.answer('agents.detect', [claude]);
    expect(notes(w)).toEqual([]);
    // agentProfiles.list is still pending: the default-model line is held with a faint "…", not missing.
    const tile = w.get('[data-test=agent-cli-claude]');
    expect(tile.get('[data-test=tile-default-model-loading]').text()).toBe('Default model: …');
    expect(tile.find('[data-test=tile-default-model]').exists()).toBe(false);

    await f.answer('agentProfiles.list', [{ id: 0, name: 'claude', baseCli: 'claude', custom: false, defaultModel: 'opus', argsBefore: [], argsAfter: [], removedArgs: [] }]);
    expect(tile.find('[data-test=tile-default-model-loading]').exists()).toBe(true); // versionStatus still pending
    await f.answer('agents.versionStatus', { cli: 'claude', detected: '2.2.0', lastRun: '2.2.0', changed: false });
    expect(tile.find('[data-test=tile-default-model-loading]').exists()).toBe(false);
    expect(tile.get('[data-test=tile-default-model]').text()).toBe('Default model: opus');
  });

  it('Agents: a failed agentProfiles.list shows its error but keeps the loading state while detect is still running', async () => {
    const f = pendingApi();
    const w = keep(mount(AgentsDialog, { props: { api: f.api, inline: true } }));
    await flushPromises();
    await f.fail('agentProfiles.list', 'the agents store is busy');
    expect(w.get('[data-test=agents-error]').text()).toContain('the agents store is busy');
    expect(notes(w)).toEqual(['Loading agents (looking for the agent CLIs on this computer)…']);
    await f.answer('agents.detect', [claude]);
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=agent-cli-claude]').exists()).toBe(true);
  });

  it('Agents: a failed detect shows the error, not the loading state', async () => {
    const f = pendingApi();
    const w = keep(mount(AgentsDialog, { props: { api: f.api, inline: true } }));
    await flushPromises();
    await f.fail('agents.detect', 'the core is restarting');
    expect(notes(w)).toEqual([]);
    expect(w.get('[data-test=agents-error]').text()).toContain('the core is restarting');
  });

  it('Skills: shown until skills.list answers', async () => {
    const f = pendingApi();
    const w = keep(mount(SkillsDialog, { props: { api: f.api, inline: true, teams: [] } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading skills…']);
    await f.answer('skills.list', [{ kind: 'builtin', name: 'review', description: 'd', roles: ['member'], body: 'b', teamId: null, team: null }]);
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=skill-review]').exists()).toBe(true);
  });

  it('Skills: a failed list ends the loading state too', async () => {
    const f = pendingApi();
    const w = keep(mount(SkillsDialog, { props: { api: f.api, inline: true, teams: [] } }));
    await flushPromises();
    await f.fail('skills.list', 'no');
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=skills-error]').exists()).toBe(true);
  });

  it('Health: shown until ops.doctor answers', async () => {
    const f = pendingApi();
    const w = keep(mount(HealthPanel, { props: { api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading health checks…']);
    await f.answer('ops.doctor', { checks: [{ id: 'db', title: 'Database', status: 'ok', detail: 'fine', fix: null, fixes: [] }] });
    expect(notes(w)).toEqual([]);
    expect(w.findAll('[data-test=health-check]')).toHaveLength(1);
  });

  it('Guardrails: shown until policy.getGlobal answers', async () => {
    const f = pendingApi();
    const w = keep(mount(GlobalPolicyDialog, { props: { api: f.api, inline: true } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the global policy…']);
    await f.answer('policy.getGlobal', {
      app: { maxConcurrentAgents: 4, backend: 'sandbox' }, network: { allowHosts: [] },
      filesystem: { grantableRoots: [], readonly: [], readwrite: [], denied: [] },
      compute: { wallTimeoutSec: 3600, idleTimeoutSec: 600, memoryMb: null, cpuPercent: null, maxProcesses: null }, ui: { clipboard: 'none' },
    });
    expect(notes(w)).not.toContain('Loading the global policy…');
    expect(w.find('[data-test=global-maxConcurrentAgents]').exists()).toBe(true);
  });

  it('Integrations: each list (CLIs, APIs, MCP servers, connections, host tasks) shows it until it answers', async () => {
    const f = pendingApi();
    const w = keep(mount(IntegrationsPanel, { props: { api: f.api, teams: [team] } }));
    await flushPromises();
    expect(notes(w)).toEqual(expect.arrayContaining(['Loading integrations…', 'Loading APIs…', 'Loading MCP servers…', 'Loading connections…', 'Loading host tasks…']));
    await f.answer('integrations.list', { integrations: [] });
    expect(notes(w)).not.toContain('Loading integrations…');
  });

  it.each([
    ['APIs', ApisPanel, { teams: [] }, 'integrations.apiList', [], 'Loading APIs…'],
    ['MCP servers', McpServersPanel, { teams: [] }, 'integrations.mcpList', [], 'Loading MCP servers…'],
    ['connections', ConnectionsPanel, {}, 'integrations.connections', [], 'Loading connections…'],
  ] as const)('Integrations > %s: shown until its list answers', async (_n, component, props, method, value, text) => {
    const f = pendingApi();
    const w = keep(mount(component as any, { props: { api: f.api, ...props } }));
    await flushPromises();
    expect(notes(w)).toContain(text);
    await f.answer(method, value);
    expect(notes(w)).not.toContain(text);
  });

  it('Integrations > host tasks: shown until tasks.list and tasks.runs answer', async () => {
    const f = pendingApi();
    const w = keep(mount(TasksPanel, { props: { api: f.api, teams: [team] } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading host tasks…']);
    expect(w.find('[data-test=tasks-empty]').exists()).toBe(false);
    await f.answer('tasks.list', { tasks: [], repos: ['main'] });
    await f.answer('tasks.runs', []);
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=tasks-empty]').exists()).toBe(true);
  });

  it('Cloud Link: shown until link.status answers', async () => {
    const f = pendingApi();
    const w = keep(mount(LinkDialog, { props: { api: f.api, inline: true } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the Cloud Link…']);
    await f.answer('link.status', {
      state: 'off', url: null, defaultUrl: 'https://cloud.example/', pairUrl: null, machineName: 'DESK', code: null, codeExpiresAt: null,
      instanceId: null, instanceName: null, org: null, workgroup: null, allowHostTerminals: false, viewers: [], error: null,
    });
    expect(notes(w)).toEqual([]);
  });

  it('Companion: shown until companion.list and companion.status answer; "None yet." only after', async () => {
    const f = pendingApi();
    const w = keep(mount(CompanionPanel, { props: { api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the companion…']);
    expect(w.find('[data-test=companion-no-clients]').exists()).toBe(false);
    await f.answer('companion.list', { clients: [] });
    await f.answer('companion.status', { endpoint: null, controlFile: null, binaryPath: null, claudeCommand: null, problem: 'off' });
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=companion-no-clients]').exists()).toBe(true);
  });

  it('General > Backups: shown until the list answers; "No backups yet" only after', async () => {
    const f = pendingApi();
    const w = keep(mount(BackupsSection, { props: { api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading backups…']);
    expect(w.find('[data-test=no-backups]').exists()).toBe(false);
    await f.answer('ops.backups', []);
    await f.answer('ops.keepDaily', { keepDaily: 7 });
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=no-backups]').exists()).toBe(true);
  });

  it('System: shown until the core answers', async () => {
    const f = pendingApi();
    const w = keep(mount(SystemPanel, { props: { api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(["Loading the core's details…"]);
    await f.answerInfo({ version: '1', platform: 'win', os: 'Windows', dotnet: '10', sandbox: { sdkVersion: '1', nativeVersion: '1', requestContract: 'v1', telemetryEnabled: false }, schema: [] });
    expect(notes(w)).toEqual([]);
    expect(w.get('[data-test=core-version]').text()).toBe('1');
  });
});

describe('agents dialogs that fetch on open', () => {
  it('Command line: shown until agents.commandLine answers', async () => {
    const f = pendingApi();
    const w = keep(mount(AgentCommandLineDialog, { props: { api: f.api, cli: 'claude', title: 'Claude Code', detected: claude }, attachTo: document.body }));
    await flushPromises();
    expect(document.body.textContent).toContain('Loading the command line…');
    await f.answer('agents.commandLine', { cli: 'claude', agent: 'claude', model: { model: '', source: 'cli', label: "The CLI's default" },
      headless: { argv: ['claude', '-p', '<prompt>'], env: [] }, interactive: { argv: ['claude'], env: [] } });
    expect(document.body.textContent).not.toContain('Loading the command line…');
    expect(document.body.querySelector('[data-test=cmd-headless]')).not.toBeNull();
    void w;
  });

  it('Model: shown until agents.models answers, then the picker', async () => {
    const f = pendingApi();
    const agent = { id: 0, name: 'claude', baseCli: 'claude', custom: false, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] };
    keep(mount(AgentModelDialog, { props: { api: f.api, agent, title: 'Claude Code' }, attachTo: document.body }));
    await flushPromises();
    expect(document.body.textContent).toContain('Loading the models…');
    await f.answer('agents.models', { models: ['opus'], source: 'app' });
    expect(document.body.textContent).not.toContain('Loading the models…');
  });

  it('Edit: the models and the preview each show it until they answer', async () => {
    const f = pendingApi();
    const initial = { name: 'Mine', defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: [] };
    keep(mount(AgentEditDialog, { props: { api: f.api, mode: 'new', baseCli: 'claude', baseTitle: 'Claude Code', initial }, attachTo: document.body }));
    await flushPromises();
    expect(document.body.textContent).toContain('Loading the models…');
    expect(document.body.textContent).toContain('Loading the command line…');
    await f.answer('agents.models', { models: ['opus'], source: 'app' });
    expect(document.body.textContent).not.toContain('Loading the models…');
    await f.answer('agentProfiles.check', []);
    await f.answer('agents.commandLine', { cli: 'claude', agent: 'claude', model: { model: '', source: 'cli', label: "The CLI's default" },
      headless: { argv: ['claude'], env: [], launch: ['claude'], groups: [] }, interactive: { argv: ['claude'], env: [], launch: ['claude'], groups: [] } });
    expect(document.body.textContent).not.toContain('Loading the command line…');
    expect(document.body.querySelector('[data-test=editor-preview]')).not.toBeNull();
  });
});
