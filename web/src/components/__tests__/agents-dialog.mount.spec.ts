// @vitest-environment happy-dom
//
// AGENTS. Launch definitions only: no System Prompts tab and no reset to built-in defaults.
// Presets the build ships are listed read-only, and only a person's own presets can be edited or
// removed. A save carries every preset back, built-ins untouched, and never a prompt.
//
// TAGS OF A BUILT-IN. The one thing about a built-in a person may change: its row offers
// "Edit tags" and "Reset to the build's tags" and nothing else, and both write the tenant setting
// `agents.tags` - never a changed catalog.
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
import { button, settle, type } from '../../test/formProbe';

const builtIn: Agent = {
  name: 'claude-headless',
  mode: 'Headless',
  builtIn: true,
  launch: { fileName: 'claude', arguments: ['-p'] },
  tags: ['developer'],
  tagsFromOperator: false,
  buildTags: ['developer'],
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
  const found = [...document.body.querySelectorAll<HTMLElement>('.q-item')]
    .find((item) => item.querySelector('.mono')?.textContent?.includes(name));
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

    const names = [...document.body.querySelectorAll('.q-item .mono')].map((label) => label.textContent ?? '');
    expect(names[0]).toContain('claude-headless');
    expect(names[1]).toContain('my-script');

    expect(row('claude-headless').textContent).toContain('Built-in');
    expect(row('claude-headless').textContent).toContain('Read-only');
    expect(rowButton('claude-headless', 'Edit')).toBeUndefined();
    expect(rowButton('claude-headless', 'Remove')).toBeUndefined();

    wrapper.unmount();
  });

  it('offers Edit and Remove on a custom preset', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(row('my-script').textContent).toContain('Custom');
    expect(rowButton('my-script', 'Edit')).toBeDefined();
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

  it('offers tag editing on a built-in and nothing else, and no Reset while the tags are the build\'s', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(rowButton('claude-headless', 'Edit tags')).toBeDefined();
    expect(rowButton('claude-headless', "Reset to the build's tags")).toBeUndefined();
    expect(rowButton('claude-headless', 'Edit')).toBeUndefined();
    expect(rowButton('claude-headless', 'Remove')).toBeUndefined();
    expect(row('claude-headless').textContent).toContain('developer');

    wrapper.unmount();
  });

  it('writes a built-in\'s new tags to agents.tags, keeping every other entry, and never saves the catalog', async () => {
    listCatalog.mockResolvedValue({ agents: [custom, builtIn, retagged] });
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Edit tags')!.click();
    await settle();
    await type('Tags', 'developer\ntester');
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

  it('keeps the tag editor open with the server\'s words when agents.tags is refused', async () => {
    saveTenantSettings.mockRejectedValue(new Error("agents.tags: 'x' for 'claude-headless' is not a tag."));
    const wrapper = await mountDialog(AgentsDialog);

    rowButton('claude-headless', 'Edit tags')!.click();
    await settle();
    await type('Tags', 'x');
    button('Save tags').click();
    await flushPromises();

    expect(bodyText()).toContain("is not a tag.");
    expect(button('Save tags')).toBeDefined();

    wrapper.unmount();
  });

  it('leaves a custom preset editing its tags on the preset, with no tag-only action', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(rowButton('my-script', 'Edit')).toBeDefined();
    expect(rowButton('my-script', 'Edit tags')).toBeUndefined();
    expect(rowButton('my-script', "Reset to the build's tags")).toBeUndefined();

    wrapper.unmount();
  });
});
