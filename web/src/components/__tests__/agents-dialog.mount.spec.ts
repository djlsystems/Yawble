// @vitest-environment happy-dom
//
// AGENTS. Launch definitions only: no System Prompts tab and no reset to built-in defaults.
// Presets the build ships are listed read-only, and only a person's own presets can be edited or
// removed. A save carries every preset back, built-ins untouched, and never a prompt.
//
// TAGS OF A BUILT-IN. The one thing about a built-in a person may change: its Details show the
// whole launch read-only with Tags editable, its tile offers "Reset to the build's tags" when the tags
// are the operator's, and both write the tenant setting `agents.tags` - never a changed catalog.
//
// CLONE makes a new custom Agent from any preset, carrying every field - `liveView` included, which
// the form has no box for. The filter narrows what is shown by text and by mode.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const { listCatalog, saveCatalog, getAgentAuth, getAgentTools, getTenantSettings, saveTenantSettings } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getAgentTools: vi.fn(),
  saveCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
  getTenantSettings: vi.fn(),
  saveTenantSettings: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  // The gate holds no update: the dialog reads it on every opening.
  listAgentUpdates: async () => [],
  listCatalog,
  saveCatalog,
  getAgentAuth,
  getAgentTools,
  getTenantSettings,
  saveTenantSettings,
}));

import AgentsDialog from '../AgentsDialog.vue';
import type { Agent } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import { addButtonIn, addChips, chipsIn } from '../../test/chipList';

const tagsList = () => document.body.querySelector('[data-agent-tags]')!;

const liveView = { path: '~/.claude/projects/{sessionId}.jsonl', format: 'claude-jsonl' };

const builtIn = {
  name: 'claude-headless',
  mode: 'Headless',
  builtIn: true,
  launch: { fileName: 'claude', arguments: ['-p'] },
  tags: ['developer'],
  tagsFromOperator: false,
  buildTags: ['developer'],
  isolation: { arguments: ['--strict-mcp-config'], gaps: ['A project hook still loads.'] },
  updates: { env: { DISABLE_AUTOUPDATER: '1' }, update: ['claude', 'update'] },
  // A field the web's type does not declare and the form has no box for: a save must still carry it.
  liveView,
} as Agent;

const concierge: Agent = {
  name: 'claude',
  mode: 'Interactive',
  builtIn: true,
  launch: { fileName: 'claude', arguments: [] },
  tags: [],
  tagsFromOperator: false,
  buildTags: [],
};

/** A built-in whose tags are the operator's, from `agents.tags`. */
const retagged: Agent = {
  name: 'grok-headless',
  mode: 'Headless',
  builtIn: true,
  launch: { fileName: 'grok', arguments: [] },
  tags: ['developer', 'researcher'],
  tagsFromOperator: true,
  buildTags: ['researcher'],
};

const custom: Agent = {
  name: 'my-script',
  mode: 'Headless',
  builtIn: false,
  launch: { fileName: 'my-script', arguments: [], languageModel: false },
};

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [custom, builtIn] });
  saveCatalog.mockReset();
  saveCatalog.mockResolvedValue(undefined);
  getAgentAuth.mockReset();
  getAgentAuth.mockResolvedValue([]);
  getAgentTools.mockReset();
  getAgentTools.mockResolvedValue({ at: null, running: false, presets: [] });
  getTenantSettings.mockReset();
  getTenantSettings.mockResolvedValue({
    settings: [{
      name: 'agents.tags',
      // `gone` names no built-in: it is ignored where it is read, and a save here must keep it.
      value: { 'grok-headless': ['developer', 'researcher'], gone: ['tester'] },
      default: {},
      source: 'row',
      updatedAt: null,
      updatedBy: null,
      description: '',
    }],
    roots: [],
  });
  saveTenantSettings.mockReset();
  saveTenantSettings.mockResolvedValue(undefined);
});

afterEach(resetBody);

function row(name: string): HTMLElement {
  const found = [...document.body.querySelectorAll<HTMLElement>('.agent-tile')]
    .find((item) => item.querySelector('.agent-name')?.textContent?.trim() === name);
  if (!found) throw new Error(`no row for ${name}`);
  return found;
}

function rowButton(name: string, label: string): HTMLButtonElement | undefined {
  return row(name).querySelector<HTMLButtonElement>(`button[aria-label="${label} ${name}"]`) ?? undefined;
}

describe('AgentsDialog, mounted', () => {
  it('has no System Prompts tab and no reset to built-in defaults', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(bodyText()).not.toContain('System Prompts');
    expect(bodyText()).not.toContain('Reset to built-in defaults');
    expect(document.body.querySelector('.q-tabs')).toBeNull();

    wrapper.unmount();
  });

  it('lists built-ins first and read-only, with no Edit or Remove', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    const names = [...document.body.querySelectorAll('.agent-tile .agent-name')].map((label) => label.textContent ?? '');
    expect(names[0]).toContain('claude-headless');
    expect(names[1]).toContain('my-script');

    expect(row('claude-headless').textContent).toContain('Built-in');
    expect(row('claude-headless').textContent).toContain('Read-only');
    expect(rowButton('claude-headless', 'Edit')).toBeUndefined();
    expect(rowButton('claude-headless', 'Remove')).toBeUndefined();

    wrapper.unmount();
  });

  it('offers Details, Clone and Remove on a custom preset', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(row('my-script').textContent).toContain('Custom');
    expect(rowButton('my-script', 'Details')).toBeDefined();
    expect(rowButton('my-script', 'Clone')).toBeDefined();
    expect(rowButton('my-script', 'Remove')).toBeDefined();

    wrapper.unmount();
  });

  it('removes a custom preset and sends every other one back, the built-in untouched', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('my-script', 'Remove')!.click();
    await settle();
    button('Remove').click();
    await flushPromises();

    expect(saveCatalog).toHaveBeenCalledTimes(1);
    expect(saveCatalog.mock.calls[0]).toEqual([[builtIn]]);

    wrapper.unmount();
  });

  it('shows the server sentence on the list when a removal is refused', async () => {
    saveCatalog.mockRejectedValue(new Error("'my-script' is what Scout in Alpha runs."));
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('my-script', 'Remove')!.click();
    await settle();
    button('Remove').click();
    await flushPromises();

    expect(bodyText()).toContain('Scout in Alpha');

    wrapper.unmount();
  });

  it('opens a built-in whole and read-only apart from its tags, with Details and Clone and no Remove', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(rowButton('claude-headless', 'Details')).toBeDefined();
    expect(rowButton('claude-headless', 'Clone')).toBeDefined();
    expect(rowButton('claude-headless', "Reset to the build's tags")).toBeUndefined();
    expect(rowButton('claude-headless', 'Remove')).toBeUndefined();
    expect(row('claude-headless').textContent).toContain('developer');

    rowButton('claude-headless', 'Details')!.click();
    await settle();

    expect(field('Executable').value).toBe('claude');
    expect(field('Executable').readOnly).toBe(true);
    expect(field('Arguments').value).toBe('-p');
    expect(field('Arguments').readOnly).toBe(true);
    // Tags are the one editable thing: removable chips with an Add button.
    expect(chipsIn(tagsList())).toEqual(['developer']);
    expect(addButtonIn(tagsList())).not.toBeNull();
    expect(tagsList().querySelector('.q-chip__icon--remove')).not.toBeNull();
    expect(button('Save tags')).toBeDefined();
    expect(bodyText()).toContain('clone it and edit the copy');

    wrapper.unmount();
  });

  it('shows what a member launch switches off on the Isolation tab', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Details')!.click();
    await settle();
    [...document.body.querySelectorAll<HTMLElement>('.q-tab')].find((t) => t.textContent?.includes('Isolation'))!.click();
    await settle();

    const isolation = document.body.querySelector('[data-agent-isolation]')!;
    expect(isolation.textContent).toContain('--strict-mcp-config');
    expect(isolation.textContent).toContain('DISABLE_AUTOUPDATER=1');
    expect(isolation.textContent).toContain('claude update');

    wrapper.unmount();
  });

  it('writes a built-in\'s new tags to agents.tags from its Details, keeping every other entry, and never saves the catalog', async () => {
    listCatalog.mockResolvedValue({ agents: [custom, builtIn, retagged] });
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Details')!.click();
    await settle();
    await addChips(tagsList(), 'tester');
    button('Save tags').click();
    await flushPromises();

    expect(saveTenantSettings).toHaveBeenCalledTimes(1);
    expect(saveTenantSettings.mock.calls[0]).toEqual([{
      'agents.tags': {
        'grok-headless': ['developer', 'researcher'],
        gone: ['tester'],
        'claude-headless': ['developer', 'tester'],
      },
    }]);
    expect(saveCatalog).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('shows the operator\'s tags as theirs and resets a built-in by removing its entry', async () => {
    listCatalog.mockResolvedValue({ agents: [custom, builtIn, retagged] });
    const wrapper = await mountDialog(AgentsDialog);

    expect(row('grok-headless').textContent).toContain('developer, researcher');
    expect(row('grok-headless').textContent).toContain("build's: researcher");

    rowButton('grok-headless', "Reset to the build's tags")!.click();
    await flushPromises();

    expect(saveTenantSettings).toHaveBeenCalledTimes(1);
    expect(saveTenantSettings.mock.calls[0]).toEqual([{ 'agents.tags': { gone: ['tester'] } }]);
    expect(saveCatalog).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('keeps the Details open with the server\'s words when agents.tags is refused', async () => {
    saveTenantSettings.mockRejectedValue(new Error("agents.tags: 'x' for 'claude-headless' is not a tag."));
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Details')!.click();
    await settle();
    await addChips(tagsList(), 'x');
    button('Save tags').click();
    await flushPromises();

    expect(bodyText()).toContain('is not a tag.');
    expect(button('Save tags')).toBeDefined();

    wrapper.unmount();
  });

  it('clones a built-in into a new custom Agent carrying every field, and saves the change made to it', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Clone')!.click();
    await settle();

    expect(field('Name').value).toBe('claude-headless-copy');
    expect(field('Arguments').readOnly).toBe(false);

    await type('Arguments', '-p\n--model\nopus');
    button('Save').click();
    await flushPromises();

    expect(saveCatalog).toHaveBeenCalledTimes(1);
    const [sent] = saveCatalog.mock.calls[0] as [Agent[]];
    // Every existing preset goes back as it was; the clone is added.
    expect(sent.slice(0, 2)).toEqual([custom, builtIn]);
    const clone = sent[2] as Agent & { liveView?: unknown };
    expect(clone.name).toBe('claude-headless-copy');
    expect(clone.builtIn).toBe(false);
    expect(clone.launch?.arguments).toEqual(['-p', '--model', 'opus']);
    expect(clone.isolation).toEqual(builtIn.isolation);
    expect(clone.updates).toEqual(builtIn.updates);
    expect(clone.liveView).toEqual(liveView);
    expect(clone.tags).toEqual(['developer']);
    expect('tagsFromOperator' in clone).toBe(false);
    expect('buildTags' in clone).toBe(false);

    wrapper.unmount();
  });

  it('names a second clone -copy-2', async () => {
    listCatalog.mockResolvedValue({ agents: [custom, builtIn, { ...custom, name: 'claude-headless-copy' }] });
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Clone')!.click();
    await settle();

    expect(field('Name').value).toBe('claude-headless-copy-2');

    wrapper.unmount();
  });

  it('keeps a field the form has no box for when a custom preset is edited', async () => {
    const watched = { ...custom, liveView } as Agent;
    listCatalog.mockResolvedValue({ agents: [watched, builtIn] });
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('my-script', 'Details')!.click();
    await settle();
    await type('Arguments', '--verbose');
    button('Save').click();
    await flushPromises();

    const [sent] = saveCatalog.mock.calls[0] as [(Agent & { liveView?: unknown })[]];
    expect(sent[0]!.launch?.arguments).toEqual(['--verbose']);
    expect(sent[0]!.liveView).toEqual(liveView);

    wrapper.unmount();
  });

  it('filters by text across name, command, arguments and tags, and by mode, and calls interactive concierge', async () => {
    listCatalog.mockResolvedValue({ agents: [custom, builtIn, concierge] });
    const wrapper = await mountDialog(AgentsDialog);
    const shown = () => [...document.body.querySelectorAll('.agent-tile .agent-name')].map((n) => n.textContent?.trim());

    expect(row('claude').textContent).toContain('concierge · claude');
    expect(row('claude').textContent).not.toContain('interactive');

    const filter = document.body.querySelector<HTMLInputElement>('input[data-agent-filter], [data-agent-filter] input')!;
    filter.value = 'CLAUDE';
    filter.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    expect(shown()).toEqual(['claude-headless', 'claude']);

    filter.value = 'developer';
    filter.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    expect(shown()).toEqual(['claude-headless']);

    filter.value = '';
    filter.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    document.body.querySelector<HTMLElement>('[data-agent-show="headless"]')!.click();
    await settle();
    expect(shown()).toEqual(['claude']);

    document.body.querySelector<HTMLElement>('[data-agent-show="concierge"]')!.click();
    await settle();
    expect(shown()).toEqual([]);
    expect(bodyText()).toContain('No Agent matches the filter.');

    wrapper.unmount();
  });
});
