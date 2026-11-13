// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import AgentsDialog from '../../src/renderer/src/components/AgentsDialog.vue';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import type { ArgGroup, DetectedCli, YawbleApi } from '../../src/shared/api';
import { isUiMethod } from '../../src/shared/api';
import { freeCopyName } from '../../src/renderer/src/agentArgs';

// Settings > Agents (the tile list and its dialogs) and Member settings against a scripted core: a fake API, no core,
// no CLI, no network port, no browser.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const found = (cliId: string, version: string): DetectedCli => ({
  cliId, found: true, launcherPath: `/bin/${cliId}`, executablePath: `/opt/${cliId}/${version}`, version, installKind: 'native',
  readOnlyFolders: [`/opt/${cliId}`], configFolders: [`/home/u/.${cliId}`], problems: [], installHelp: '',
});
const builtin = (name: string, defaultModel = '') => ({ id: 0, name, baseCli: name, custom: false, defaultModel, argsBefore: [], argsAfter: [], removedArgs: [] });
const mine = { id: 7, name: 'Claude - Opus long runs', baseCli: 'claude', custom: true, defaultModel: 'opus', argsBefore: ['--effort=high'], argsAfter: ['--foo'], removedArgs: [] };
const grokMine = { id: 8, name: 'Grok quiet', baseCli: 'grok', custom: true, defaultModel: '', argsBefore: [], argsAfter: [], removedArgs: ['--no-auto-update'] };

// A stand-in for the core's builder: the built-in groups per CLI, the draft's changes applied the core's way.
function described(p: any) {
  const cli = p.cli === mine.name ? 'claude' : p.cli === grokMine.name ? 'grok' : p.cli;
  const model: string = p.model ?? '';
  const removed: string[] = p.removedArgs ?? [];
  const modelGroup: ArgGroup[] = model ? [{ tokens: ['--model', model], locked: true, tail: false, removed: false, reason: 'the model is set only by the model setting' }] : [];
  const base: ArgGroup[] = cli === 'grok'
    ? [{ tokens: ['--no-auto-update'], locked: false, tail: false, removed: false, reason: null },
      { tokens: ['--session-id', '<session id>'], locked: true, tail: false, removed: false, reason: 'the app tracks the run by its session id' },
      ...modelGroup,
      { tokens: ['<prompt>'], locked: true, tail: true, removed: false, reason: "the run's prompt" }]
    : [{ tokens: ['-p', '<prompt>'], locked: true, tail: false, removed: false, reason: "the run's prompt" },
      { tokens: ['--mcp-config', '<workspace>/.yawble/mcp.json'], locked: true, tail: false, removed: false, reason: "the agent's tools (the bus) come through it" },
      ...modelGroup];
  const groups = base.map((g) => ({ ...g, removed: !g.locked && !g.tail && removed.some((r) => r === g.tokens[0] || r === g.tokens.join(' ')) }));
  const launch = [cli];
  const argv = [...launch, ...(p.argsBefore ?? []), ...groups.filter((g) => !g.tail && !g.removed).flatMap((g) => g.tokens), ...(p.argsAfter ?? []),
    ...groups.filter((g) => g.tail).flatMap((g) => g.tokens)];
  const kind = { argv, env: ['YAWBLE_RUN_TOKEN'], launch, groups };
  return { cli, agent: p.cli, model: { model, source: model ? 'agent' : 'cli', label: model ? `Agent default (${model})` : "The CLI's default" },
    headless: kind, interactive: { ...kind, argv: argv.filter((a) => a !== '-p') } };
}

function core(extra: Record<string, (p: any) => unknown> = {}) {
  return {
    'agents.detect': () => [found('claude', '2.2.0'), found('grok', '1.0.77')],
    'agentProfiles.list': () => [builtin('claude', 'opus'), builtin('grok'), mine, grokMine],
    'agents.commandLine': described,
    'agents.models': (p: any) => ({ models: p.cli === 'grok' ? ['grok-4'] : ['opus', 'sonnet', 'haiku'], source: 'app' }),
    'agents.versionStatus': (p: any) => ({ cli: p.cli, detected: '2.2.0', lastRun: p.cli === 'claude' ? '2.1.0' : '2.2.0', changed: p.cli === 'claude' }),
    'agentProfiles.check': () => [],
    ...extra,
  };
}

let wrapper: VueWrapper | null = null;
// The editor's preview is debounced (setTimeout): fake timers, advanced by settle().
beforeEach(() => { vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }); });
afterEach(() => { wrapper?.unmount(); wrapper = null; document.body.innerHTML = ''; vi.useRealTimers(); });
async function settle() {
  await flushPromises();
  vi.advanceTimersByTime(500);
  await flushPromises();
}

async function open(answers: Record<string, (p: any) => unknown>) {
  const { api, calls } = fakeApi(answers);
  wrapper = mount(AgentsDialog, { props: { api, inline: true }, attachTo: document.body });
  await settle();
  return { w: wrapper, calls };
}
const esc = (s: string) => s.replace(/ /g, '\\ ');
const tile = (w: VueWrapper, name: string) => w.get(`[data-test=agent-custom-${esc(name)}]`);
const chips = (w: VueWrapper, kind: string) => w.findAll(`[data-test=agent-editor] [data-test=${kind}]`);

describe('agents screen: one list of tiles', () => {
  it('one tile per built-in, each custom agent right after its base; no History or Recent runs', async () => {
    const { w, calls } = await open(core());
    const order = w.findAll('[data-test=agent-tiles] > article').map((t) => t.attributes('data-test'));
    expect(order).toEqual(['agent-cli-claude', `agent-custom-${mine.name}`, 'agent-cli-grok', `agent-custom-${grokMine.name}`]);
    expect(tile(w, mine.name).get('[data-test=tile-chip]').text()).toBe('My agent · based on Claude Code');
    expect(tile(w, mine.name).get('[data-test=tile-default-model]').text()).toBe('Default model: opus');
    expect(w.get('[data-test=agent-cli-grok] [data-test=tile-default-model]').text()).toBe("Default model: the CLI's default");
    // A custom tile shows its base CLI's install.
    expect(tile(w, mine.name).get('[data-test=cli-version]').text()).toBe('2.2.0');
    expect(w.get('[data-test=agent-cli-claude] [data-test=version-changed]').text()).toContain('2.1.0 → 2.2.0');
    expect(w.find('[data-test=agent-cli-grok] [data-test=version-changed]').exists()).toBe(false);
    // Built-ins: no Edit / Delete; customs: no Update, but Terminal and Test (their base CLI is found).
    expect(w.find('[data-test=agent-cli-claude] [data-test=tile-edit]').exists()).toBe(false);
    expect(w.find('[data-test=agent-cli-claude] [data-test=tile-delete]').exists()).toBe(false);
    expect(tile(w, mine.name).find('[data-test=tile-update]').exists()).toBe(false);
    expect(tile(w, mine.name).find('[data-test=test-claude]').exists()).toBe(true);
    expect(tile(w, mine.name).find('[data-test=terminal-claude]').exists()).toBe(true);
    expect(w.text()).not.toContain('History');
    expect(w.text()).not.toContain('Recent runs');
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.history');
    expect(calls.map((c) => c.method)).not.toContain('agents.runRecords');
  });

  it('Command line opens a dialog with both lines and env names only', async () => {
    const { w, calls } = await open(core());
    await tile(w, mine.name).get('[data-test=tile-cmd]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'agents.commandLine').at(-1)!.params).toEqual({ cli: mine.name });
    expect(w.get('[data-test=cmd-headless]').text()).toContain('claude -p <prompt> --mcp-config <workspace>/.yawble/mcp.json');
    expect(w.get('[data-test=cmd-headless]').text()).toContain('YAWBLE_RUN_TOKEN');
    expect(w.get('[data-test=cmd-interactive]').exists()).toBe(true);
  });

  it('Update asks the core for the CLI\'s own update; a refusal is shown', async () => {
    const { w, calls } = await open(core({ 'agents.checkForUpdates': () => { throw new Error('forbidden'); } }));
    await w.get('[data-test=agent-cli-claude] [data-test=tile-update]').trigger('click');
    await settle();
    expect(calls.at(-1)).toEqual({ method: 'agents.checkForUpdates', params: { cli: 'claude' } });
    expect(w.get('[data-test=agents-error]').text()).toContain('forbidden');
  });
});

describe('agents screen: Model', () => {
  it('a built-in saves through setDefaultModel, a custom model through the text box', async () => {
    const { w, calls } = await open(core({ 'agentProfiles.setDefaultModel': () => ({}) }));
    await w.get('[data-test=agent-cli-claude] [data-test=tile-model]').trigger('click');
    await settle();
    expect((w.get('[data-test=model-dialog-pick]').element as HTMLSelectElement).value).toBe('opus');
    await w.get('[data-test=model-dialog-pick]').setValue('sonnet');
    await w.get('[data-test=model-dialog-save]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'agentProfiles.setDefaultModel')!.params).toEqual({ baseCli: 'claude', model: 'sonnet' });
    expect(w.find('[data-test=model-dialog]').exists()).toBe(false);
    expect(w.get('[data-test=agents-notice]').text()).toContain('Default model of Claude Code saved.');

    await w.get('[data-test=agent-cli-grok] [data-test=tile-model]').trigger('click');
    await settle();
    await w.get('[data-test=model-dialog-pick]').setValue('__custom__');
    await w.get('[data-test=model-dialog-pick-text]').setValue('grok-x');
    await w.get('[data-test=model-dialog-save]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'agentProfiles.setDefaultModel').at(-1)!.params).toEqual({ baseCli: 'grok', model: 'grok-x' });
  });

  it('a custom agent saves through agentProfiles.update with its existing arguments', async () => {
    const { w, calls } = await open(core({ 'agentProfiles.update': (p) => ({ ...mine, ...p }) }));
    await tile(w, mine.name).get('[data-test=tile-model]').trigger('click');
    await settle();
    await w.get('[data-test=model-dialog-pick]').setValue('haiku');
    await w.get('[data-test=model-dialog-save]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'agentProfiles.update')!.params).toEqual({
      id: 7, name: mine.name, defaultModel: 'haiku', argsBefore: ['--effort=high'], argsAfter: ['--foo'], removedArgs: [],
    });
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.setDefaultModel');
  });
});

describe('agents screen: Duplicate and Edit', () => {
  it('a free copy name: "<base> - copy", then "- copy 2"', () => {
    expect(freeCopyName('Claude Code', ['claude', 'grok'])).toBe('Claude Code - copy');
    expect(freeCopyName('Claude Code', ['claude code - copy', 'Claude Code - copy 2'])).toBe('Claude Code - copy 3');
  });

  it('Duplicate opens the editor with a free name, and Cancel creates nothing', async () => {
    const taken = { ...mine, id: 9, name: 'Claude Code - copy', argsBefore: [], argsAfter: [] };
    const { w, calls } = await open(core({ 'agentProfiles.list': () => [builtin('claude'), builtin('grok'), taken] }));
    await w.get('[data-test=agent-cli-claude] [data-test=tile-duplicate]').trigger('click');
    await settle();
    expect(w.get('[data-test=agent-editor]').attributes('data-mode')).toBe('new');
    expect((w.get('[data-test=editor-name]').element as HTMLInputElement).value).toBe('Claude Code - copy 2');
    await w.get('[data-test=editor-cancel]').trigger('click');
    await settle();
    expect(w.find('[data-test=agent-editor]').exists()).toBe(false);
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.duplicate');
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.update');
  });

  it('Save in a new agent calls duplicate, then update with the draft; a custom source brings its args and model', async () => {
    const made = { ...mine, id: 12, name: `${mine.name} - copy`, argsBefore: [], argsAfter: [] };
    const { w, calls } = await open(core({
      'agentProfiles.duplicate': (p) => ({ ...made, name: p.name }),
      'agentProfiles.update': (p) => ({ ...made, ...p }),
    }));
    await tile(w, mine.name).get('[data-test=tile-duplicate]').trigger('click');
    await settle();
    expect((w.get('[data-test=editor-name]').element as HTMLInputElement).value).toBe(`${mine.name} - copy`);
    expect(chips(w, 'chip-before').map((c) => c.text())).toEqual(['--effort=high×']);
    expect((w.get('[data-test=editor-model]').element as HTMLSelectElement).value).toBe('opus');
    await w.get('[data-test=editor-save]').trigger('click');
    await settle();
    const methods = calls.map((c) => c.method).filter((m) => m === 'agentProfiles.duplicate' || m === 'agentProfiles.update');
    expect(methods).toEqual(['agentProfiles.duplicate', 'agentProfiles.update']);
    expect(calls.find((c) => c.method === 'agentProfiles.duplicate')!.params).toEqual({ baseCli: 'claude', name: `${mine.name} - copy`, defaultModel: 'opus' });
    expect(calls.find((c) => c.method === 'agentProfiles.update')!.params).toEqual({
      id: 12, name: `${mine.name} - copy`, defaultModel: 'opus', argsBefore: ['--effort=high'], argsAfter: ['--foo'], removedArgs: [],
    });
    expect(w.find('[data-test=agent-editor]').exists()).toBe(false);
    expect(w.get('[data-test=agents-notice]').text()).toContain('saved');
  });

  it('chips: the launcher plain, locked groups with their reason and no ×, the prompt locked last', async () => {
    const { w } = await open(core());
    await tile(w, grokMine.name).get('[data-test=tile-edit]').trigger('click');
    await settle();
    expect(chips(w, 'chip-launch').map((c) => c.text())).toEqual(['grok']);
    const groups = chips(w, 'chip-group');
    const session = groups.find((g) => g.text().includes('--session-id'))!;
    expect(session.attributes('data-locked')).toBe('true');
    expect(session.attributes('title')).toContain('the app tracks the run by its session id');
    expect(session.find('[data-test=chip-remove]').exists()).toBe(false);
    expect(session.find('[data-test=chip-restore]').exists()).toBe(false);
    const tail = chips(w, 'chip-tail');
    expect(tail.map((t) => t.text())).toEqual(['🔒<prompt>']);
    expect(tail[0].find('button').exists()).toBe(false);
    // Grok quiet removes --no-auto-update: shown struck through with ↺.
    const update = groups.find((g) => g.text().includes('--no-auto-update'))!;
    expect(update.attributes('data-removed')).toBe('true');
    expect(update.find('[data-test=chip-restore]').exists()).toBe(true);
  });

  it('removing an unlocked chip puts it in removedArgs and restore takes it out', async () => {
    const { w, calls } = await open(core({ 'agentProfiles.update': (p) => ({ ...grokMine, ...p }) }));
    await w.get('[data-test=agent-cli-grok] [data-test=tile-duplicate]').trigger('click');
    await settle();
    const update = () => chips(w, 'chip-group').find((g) => g.text().includes('--no-auto-update'))!;
    expect(update().attributes('data-removed')).toBe('false');
    await update().get('[data-test=chip-remove]').trigger('click');
    await settle();
    expect(update().attributes('data-removed')).toBe('true');
    expect(calls.filter((c) => c.method === 'agentProfiles.check').at(-1)!.params.removedArgs).toEqual(['--no-auto-update']);
    const shown = calls.filter((c) => c.method === 'agents.commandLine').at(-1)!.params;
    expect(shown).toMatchObject({ cli: 'grok', removedArgs: ['--no-auto-update'] });
    expect(w.get('[data-test=preview-headless]').text()).not.toContain('--no-auto-update');
    await update().get('[data-test=chip-restore]').trigger('click');
    await settle();
    expect(update().attributes('data-removed')).toBe('false');
    expect(calls.filter((c) => c.method === 'agentProfiles.check').at(-1)!.params.removedArgs).toEqual([]);
    expect(w.get('[data-test=preview-headless]').text()).toContain('--no-auto-update');
  });

  it('Enter in Add after adds ONE argv item, spaces and all; × removes it', async () => {
    const { w, calls } = await open(core({ 'agentProfiles.update': (p) => ({ ...mine, ...p }) }));
    await tile(w, mine.name).get('[data-test=tile-edit]').trigger('click');
    await settle();
    const box = w.get('[data-test=add-after]');
    await box.setValue('--flag=value with space');
    await box.trigger('keydown', { key: 'Enter' });
    await settle();
    expect(chips(w, 'chip-after').map((c) => c.text())).toEqual(['--foo×', '--flag=value with space×']);
    expect((box.element as HTMLInputElement).value).toBe('');
    expect(calls.filter((c) => c.method === 'agents.commandLine').at(-1)!.params.argsAfter).toEqual(['--foo', '--flag=value with space']);
    expect(w.get('[data-test=preview-headless]').text()).toContain('--foo --flag=value with space');
    await chips(w, 'chip-after')[0].get('[data-test=chip-remove]').trigger('click');
    await settle();
    await w.get('[data-test=editor-save]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'agentProfiles.update')!.params).toMatchObject({ id: 7, argsBefore: ['--effort=high'], argsAfter: ['--flag=value with space'] });
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.duplicate');
  });

  it('a before chip is edited in place', async () => {
    const { w, calls } = await open(core());
    await tile(w, mine.name).get('[data-test=tile-edit]').trigger('click');
    await settle();
    await chips(w, 'chip-before')[0].get('[data-test=chip-text]').trigger('click');
    const input = w.get('[data-test=chip-edit]');
    await input.setValue('--effort=low');
    await input.trigger('keydown', { key: 'Enter' });
    await settle();
    expect(chips(w, 'chip-before').map((c) => c.text())).toEqual(['--effort=low×']);
    expect(calls.filter((c) => c.method === 'agentProfiles.check').at(-1)!.params.argsBefore).toEqual(['--effort=low']);
  });

  it('the core\'s refusals show under the chips and Save is disabled while there are any', async () => {
    const { w, calls } = await open(core({
      'agentProfiles.check': (p) => (p.argsAfter.includes('--session-id=x') ? [{ argument: '--session-id=x', reason: '--session-id is locked: the app tracks the run by its session id' }] : []),
      'agentProfiles.update': (p) => ({ ...mine, ...p }),
    }));
    await tile(w, mine.name).get('[data-test=tile-edit]').trigger('click');
    await settle();
    const drafts = () => calls.filter((c) => c.method === 'agents.commandLine').length;
    const before = drafts();
    const box = w.get('[data-test=add-after]');
    await box.setValue('--session-id=x');
    await box.trigger('keydown', { key: 'Enter' });
    await settle();
    expect(w.get('[data-test=editor-refusals]').text()).toContain('the app tracks the run by its session id');
    expect(w.get('[data-test=editor-save]').attributes('disabled')).toBeDefined();
    expect(drafts()).toBe(before); // the draft is only described once check is clean
    await w.get('[data-test=editor-save]').trigger('click');
    await settle();
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.update');
    const last = chips(w, 'chip-after').at(-1)!;
    await last.get('[data-test=chip-remove]').trigger('click');
    await settle();
    expect(w.find('[data-test=editor-refusals]').exists()).toBe(false);
    expect(w.get('[data-test=editor-save]').attributes('disabled')).toBeUndefined();
  });
});

describe('agents screen: Delete', () => {
  it('asks first; an agent in use names the members and offers to move them to the base', async () => {
    let first = true;
    const { w, calls } = await open(core({
      'agentProfiles.delete': (p) => {
        if (first && !p.moveToBase) { first = false; throw new Error(`${mine.name} is in use by T/Ada; move them to claude or choose another agent for them first`); }
        return { deleted: true };
      },
    }));
    await tile(w, mine.name).get('[data-test=tile-delete]').trigger('click');
    await settle();
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.delete'); // a confirm first
    await w.get('[data-test=delete-confirm]').trigger('click');
    await settle();
    expect(w.get('[data-test=delete-in-use]').text()).toContain('T/Ada');
    expect(w.get('[data-test=delete-move]').text()).toBe('Move those members to Claude Code and delete');
    await w.get('[data-test=delete-move]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'agentProfiles.delete').map((c) => c.params)).toEqual([{ id: 7, moveToBase: false }, { id: 7, moveToBase: true }]);
    expect(w.find('[data-test=delete-dialog]').exists()).toBe(false);
    expect(w.get('[data-test=agents-notice]').text()).toContain(`${mine.name} deleted.`);
  });

  it('Cancel deletes nothing', async () => {
    const { w, calls } = await open(core());
    await tile(w, mine.name).get('[data-test=tile-delete]').trigger('click');
    await settle();
    await w.get('[data-test=delete-cancel]').trigger('click');
    await settle();
    expect(w.find('[data-test=delete-dialog]').exists()).toBe(false);
    expect(calls.map((c) => c.method)).not.toContain('agentProfiles.delete');
  });

  it('the renderer may call agentProfiles.* through the bridge', () => {
    expect(isUiMethod('agentProfiles.list')).toBe(true);
  });
});

describe('fix round 1', () => {
  it('a duplicate whose update failed is in the list after Cancel, and the next Duplicate picks the next free name', async () => {
    const list = [builtin('claude'), builtin('grok')] as any[];
    const { w, calls } = await open(core({
      'agentProfiles.list': () => [...list],
      'agentProfiles.duplicate': (p) => { const made = { ...mine, id: 20, name: p.name, argsBefore: [], argsAfter: [], defaultModel: '' }; list.push(made); return made; },
      'agentProfiles.update': () => { throw new Error('the database is locked'); },
    }));
    await w.get('[data-test=agent-cli-claude] [data-test=tile-duplicate]').trigger('click');
    await settle();
    await w.get('[data-test=editor-save]').trigger('click');
    await settle();
    expect(w.get('[data-test=editor-error]').text()).toContain('the database is locked');
    // Now an edit of the agent just made: the title says so, and a retry would update it, not copy again.
    expect(w.get('[data-test=agent-editor]').attributes('data-mode')).toBe('edit');
    expect(w.find('[aria-label="Edit Claude Code - copy"]').exists()).toBe(true);
    const lists = calls.filter((c) => c.method === 'agentProfiles.list').length;
    await w.get('[data-test=editor-cancel]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'agentProfiles.list').length).toBe(lists + 1);
    expect(w.find('[data-test=agent-custom-Claude\\ Code\\ -\\ copy]').exists()).toBe(true);
    await w.get('[data-test=agent-cli-claude] [data-test=tile-duplicate]').trigger('click');
    await settle();
    expect((w.get('[data-test=editor-name]').element as HTMLInputElement).value).toBe('Claude Code - copy 2');
  });

  it('Cancel after nothing was made does not reload', async () => {
    const { w, calls } = await open(core());
    const lists = calls.filter((c) => c.method === 'agentProfiles.list').length;
    await w.get('[data-test=agent-cli-claude] [data-test=tile-duplicate]').trigger('click');
    await settle();
    await w.get('[data-test=editor-cancel]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'agentProfiles.list').length).toBe(lists);
  });

  it('typing a Custom… model that starts like a listed one stays in Custom…', async () => {
    const { w, calls } = await open(core({ 'agentProfiles.setDefaultModel': () => ({}) }));
    await w.get('[data-test=agent-cli-claude] [data-test=tile-model]').trigger('click');
    await settle();
    await w.get('[data-test=model-dialog-pick]').setValue('__custom__');
    for (const typed of ['s', 'sonnet', 'sonnet-4', 'sonnet-4-5']) {
      await w.get('[data-test=model-dialog-pick-text]').setValue(typed);
      await settle();
      expect((w.get('[data-test=model-dialog-pick]').element as HTMLSelectElement).value).toBe('__custom__');
    }
    await w.get('[data-test=model-dialog-save]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'agentProfiles.setDefaultModel')!.params).toEqual({ baseCli: 'claude', model: 'sonnet-4-5' });
  });

  it('the Model dialog cannot save Custom… with no name', async () => {
    const { w } = await open(core());
    await w.get('[data-test=agent-cli-grok] [data-test=tile-model]').trigger('click');
    await settle();
    expect(w.get('[data-test=model-dialog-save]').attributes('disabled')).toBeUndefined();
    await w.get('[data-test=model-dialog-pick]').setValue('__custom__');
    await settle();
    expect(w.get('[data-test=model-dialog-save]').attributes('disabled')).toBeDefined();
    await w.get('[data-test=model-dialog-pick-text]').setValue('  ');
    await settle();
    expect(w.get('[data-test=model-dialog-save]').attributes('disabled')).toBeDefined();
    await w.get('[data-test=model-dialog-pick-text]').setValue('grok-x');
    await settle();
    expect(w.get('[data-test=model-dialog-save]').attributes('disabled')).toBeUndefined();
  });

  it('the preview is debounced, and a pending preview never disables Save', async () => {
    let hold: (() => void) | null = null;
    const { w, calls } = await open(core({
      'agents.commandLine': (p) => (p.model === 'held' ? new Promise((r) => { hold = () => r(described(p)); }) : described(p)),
    }));
    await tile(w, mine.name).get('[data-test=tile-edit]').trigger('click');
    await settle();
    const describes = () => calls.filter((c) => c.method === 'agents.commandLine').length;
    const start = describes();
    await w.get('[data-test=editor-model]').setValue('__custom__');
    for (const typed of ['g', 'gr', 'gro', 'grok-x']) await w.get('[data-test=editor-model-text]').setValue(typed);
    await flushPromises();
    expect(describes()).toBe(start); // nothing yet: waiting for the typing to stop
    vi.advanceTimersByTime(399);
    await flushPromises();
    expect(describes()).toBe(start);
    vi.advanceTimersByTime(1);
    await flushPromises();
    expect(describes()).toBe(start + 1);
    expect(calls.filter((c) => c.method === 'agents.commandLine').at(-1)!.params.model).toBe('grok-x');
    // A preview still in flight: Save stays enabled (it follows the refusals check only).
    await w.get('[data-test=editor-model-text]').setValue('held');
    await settle();
    expect(hold).not.toBeNull();
    expect(w.get('[data-test=editor-save]').attributes('disabled')).toBeUndefined();
    hold!();
    await settle();
  });

  it('removing a chip while another is being edited commits that edit to its own chip', async () => {
    const { w, calls } = await open(core({ 'agentProfiles.update': (p) => ({ ...mine, ...p }) }));
    await tile(w, mine.name).get('[data-test=tile-edit]').trigger('click');
    await settle();
    for (const a of ['--a', '--b']) {
      await w.get('[data-test=add-after]').setValue(a);
      await w.get('[data-test=add-after]').trigger('keydown', { key: 'Enter' });
      await settle();
    }
    await chips(w, 'chip-after')[0].get('[data-test=chip-text]').trigger('click');
    await w.get('[data-test=chip-edit]').setValue('--foo2'); // not committed yet
    await chips(w, 'chip-after')[1].get('[data-test=chip-remove]').trigger('click'); // remove --a
    await settle();
    expect(chips(w, 'chip-after').map((c) => c.text())).toEqual(['--foo2×', '--b×']);
    await w.get('[data-test=editor-save]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'agentProfiles.update')!.params.argsAfter).toEqual(['--foo2', '--b']);
  });

  it('a delete that fails for another reason shows the error and offers no move', async () => {
    const { w } = await open(core({ 'agentProfiles.delete': () => { throw new Error('no such custom agent'); } }));
    await tile(w, mine.name).get('[data-test=tile-delete]').trigger('click');
    await settle();
    await w.get('[data-test=delete-confirm]').trigger('click');
    await settle();
    expect(w.get('[data-test=delete-error]').text()).toBe('no such custom agent');
    expect(w.find('[data-test=delete-move]').exists()).toBe(false);
    expect(w.find('[data-test=delete-in-use]').exists()).toBe(false);
  });

  it('a custom tile shows its base CLI\'s problems', async () => {
    const { w } = await open(core({
      'agents.detect': () => [{ ...found('claude', '2.2.0'), problems: ['its folder is not on your PATH'] }, found('grok', '1.0.77')],
    }));
    expect(tile(w, mine.name).get('[data-test=cli-problems]').text()).toContain('not on your PATH');
    expect(tile(w, grokMine.name).find('[data-test=cli-problems]').exists()).toBe(false);
  });
});

describe('member settings: model picker and live command line', () => {
  const member = { id: 3, teamId: 1, team: 'T', name: 'Ada', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' };
  const lines = (model = 'opus', label = 'Agent default (opus)') => ({
    cli: 'claude', model: { model, source: 'agent', label },
    headless: { argv: ['claude', '-p', '<prompt>', '--session-id', '<session id>', '--model', model], env: ['YAWBLE_RUN_TOKEN'] },
    interactive: { argv: ['claude', '--model', model], env: [] },
  });
  const memberCore = (extra: Record<string, (p: any) => unknown> = {}) => core({
    'agentProfiles.list': () => [builtin('claude', 'opus'), builtin('grok'), mine],
    'agents.commandLine': () => lines(),
    'members.get': () => member,
    'policy.getMember': () => ({ backend: 'x', global: {}, override: {}, loosen: {}, widening: [], violations: [], effective: { notes: [] }, implicit: { readwrite: [], readonly: [], denied: [] }, loosenSummary: [] }),
    'credentials.status': () => ({ claude: { mode: 'subscription' }, grok: { mode: 'login' } }),
    'members.update': (p) => ({ ...member, ...p }),
    ...extra,
  });

  it('the first model entry is "Agent default (<model>)" and the preview follows the pick', async () => {
    const { api, calls } = fakeApi(memberCore({
      'agents.commandLine': (p) => lines(p.model ?? 'opus', p.model ? p.model : 'Agent default (opus)'),
    }));
    const w = mount(MemberSettingsDialog, { props: { memberId: 3, api } });
    await settle();
    const options = w.get('[data-test=member-model-pick]').findAll('option');
    expect(options[0].text()).toBe('Agent default (opus)');
    expect(w.get('[data-test=member-cmdline-headless]').text()).toContain('--model opus');
    await w.get('[data-test=member-model-pick]').setValue('haiku');
    await settle();
    expect(calls.filter((c) => c.method === 'agents.commandLine').at(-1)!.params).toEqual({ cli: 'claude', model: 'haiku' });
    await w.get('[data-test=save-general]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'members.update')!.params.model).toBe('haiku');
    w.unmount();
  });

  it('custom agents are offered in the agent picker; picking the default saves an empty model', async () => {
    const { api, calls } = fakeApi(memberCore());
    const w = mount(MemberSettingsDialog, { props: { memberId: 3, api } });
    await settle();
    const values = w.get('[data-test=member-cli]').findAll('option').map((o) => o.element.value);
    expect(values).toContain('Claude - Opus long runs');
    await w.get('[data-test=save-general]').trigger('click');
    await settle();
    expect(calls.find((c) => c.method === 'members.update')!.params.model).toBe('');
    w.unmount();
  });
});
