// @vitest-environment happy-dom
//
// ADMIN > SETTINGS, MOUNTED CLOSED AND THEN OPENED. The Admission wording,
// the key never on screen, the live ledger beside it, a source line on every field, validation
// before Save, Save disabled while nothing moved, a 400 put under the field it names, and the
// Concierge's Agent saved by the same button.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({
  getTenantSettings: vi.fn(),
  saveTenantSettings: vi.fn(),
  getAgentAuth: vi.fn(),
  getWip: vi.fn(),
  concierge: vi.fn(),
  listCatalog: vi.fn(),
  setConcierge: vi.fn(),
  getKanbanBoard: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: api.notify }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTenantSettings: api.getTenantSettings,
  saveTenantSettings: api.saveTenantSettings,
  getAgentAuth: api.getAgentAuth,
  getWip: api.getWip,
  concierge: api.concierge,
  listCatalog: api.listCatalog,
  setConcierge: api.setConcierge,
}));

vi.mock('../../api/kanban', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getKanbanBoard: api.getKanbanBoard,
}));

import TenantSettingsDialog from '../TenantSettingsDialog.vue';
import { TenantSettingRejected } from '../../api/client';
import { useConsoleStore } from '../../stores/console';
import { useTerminalDisplayStore } from '../../stores/terminalDisplay';
import type { TenantSetting } from '../../api/types';
import { normaliseTenantSettings } from '../../lib/tenantSettings';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const setting = (name: string, value: unknown, extra: Partial<TenantSetting> = {}): TenantSetting => ({
  name,
  value,
  default: value,
  source: 'appsettings',
  updatedAt: null,
  updatedBy: null,
  description: '',
  ...extra,
});

function settings() {
  return {
    settings: [
      setting('wip.maxRunning', 4, {
        source: 'row',
        default: 8,
        updatedAt: '2026-09-23T10:00:00Z',
        updatedBy: 'ops@example.com',
        description: 'The ledger reads this on every claim.',
      }),
      setting('workflow.spendLimit', 100000000),
      setting('concierge.idleTimeout', '08:00:00'),
      setting('quiet.window', '00:30:00'),
      setting('resume.maxAutomatic', 3),
      setting('causation.depthLimit', 25),
      setting('kanban.wipLimits', { blocked: 3 }),
      setting('theme.default', 'auto'),
      setting('system.packages', ['htop']),
      // THE ROOTS RIDE THE LIST READ-ONLY, as `b000c-api.md` fixes it.
      { ...setting('fileBrowser.roots.Data', '/data'), readOnly: true, note: 'Always included.' },
      {
        ...setting('fileBrowser.roots.Projects', '/mnt/projects'),
        readOnly: true,
        note: 'Add it to FileBrowser:Roots; the container must also mount the path.',
      },
    ],
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  // The wire body, through the same normaliser `getTenantSettings` runs it through.
  api.getTenantSettings.mockResolvedValue(normaliseTenantSettings(settings()));
  api.saveTenantSettings.mockResolvedValue(undefined);
  api.getAgentAuth.mockResolvedValue([]);
  api.getWip.mockResolvedValue({
    max: 4,
    running: [{ team: 'alpha', member: 'Dev1', since: '2026-09-23T10:00:00Z' }],
    waiting: [{ team: 'beta', member: 'Manager', since: '2026-09-23T10:01:00Z' }],
  });
  api.listCatalog.mockResolvedValue({
    agents: [
      { name: 'claude-interactive', mode: 'Interactive' },
      { name: 'claude-headless', mode: 'Headless' },
    ],
  });
  api.concierge.mockResolvedValue({ agent: 'claude-interactive' });
  api.setConcierge.mockResolvedValue(undefined);
  api.getKanbanBoard.mockResolvedValue({
    lanes: [
      { id: 'todo', title: 'To Do' },
      { id: 'in-progress', title: 'In Progress', wipLimit: 4 },
      { id: 'blocked', title: 'Needs You', wipLimit: 3 },
      { id: 'done', title: 'Done' },
    ],
    cards: [],
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function openDialog() {
  const wrapper = await mountDialog(TenantSettingsDialog, {});
  vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue();
  await flushPromises();
  return wrapper;
}

function tabLabels(): string[] {
  return [...document.body.querySelectorAll('.q-tab .q-tab__label')].map((label) => label.textContent?.trim() ?? '');
}

function saveButton(): HTMLButtonElement {
  return [...document.body.querySelectorAll('button')].find(
    (b) => b.querySelector('.block')?.textContent?.trim() === 'Save',
  ) as HTMLButtonElement;
}

async function showTab(wrapper: VueWrapper, name: string) {
  const tab = wrapper.findAllComponents({ name: 'QTab' }).find((t) => t.props('name') === name)!;
  await tab.trigger('click');
  await flushPromises();
}

const input = (wrapper: VueWrapper, label: string) =>
  wrapper.findAllComponents({ name: 'QInput' }).find((i) => i.props('label') === label);

const select = (wrapper: VueWrapper, label: string) =>
  wrapper.findAllComponents({ name: 'QSelect' }).find((s) => s.props('label') === label);

async function type(wrapper: VueWrapper, label: string, value: string) {
  input(wrapper, label)!.vm.$emit('update:modelValue', value);
  await flushPromises();
}

describe('the tabs', () => {
  it('lists the tabs in order, with System after Kanban', async () => {
    await openDialog();

    expect(tabLabels()).toEqual(['Admission', 'Spend', 'Concierge', 'Sweeps', 'Kanban', 'System', 'Catalog health', 'Roots']);
  });
});

describe('Admission', () => {
  it('uses the decided label and hint, and never shows the key', async () => {
    const wrapper = await openDialog();

    expect(input(wrapper, 'Agents running at once')!.props('modelValue')).toBe('4');
    expect(bodyText()).toContain(
      'Across all teams, Managers included. Work over this number waits its turn; nothing is refused. 0 means no limit.',
    );
    expect(bodyText()).not.toContain('wip.maxRunning');
    // The field's own hint is the one description on screen, not the server's beside it.
    expect(bodyText()).not.toContain('The ledger reads this on every claim.');
  });

  it('shows who is running and who is waiting, from the ledger', async () => {
    await openDialog();

    const running = document.body.querySelector('[data-list="running"]')?.textContent ?? '';
    const waiting = document.body.querySelector('[data-list="waiting"]')?.textContent ?? '';
    expect(running).toContain('alpha / Dev1');
    expect(waiting).toContain('beta / Manager');
    expect(api.getWip).toHaveBeenCalled();
  });

  it('shows where the value came from and who last changed it', async () => {
    await openDialog();

    const source = document.body.querySelector('[data-setting="wip.maxRunning"] .tenant-setting-source')?.textContent ?? '';
    expect(source).toContain('Set by ops@example.com');
  });
});

describe('Save', () => {
  it('is disabled while nothing has changed', async () => {
    await openDialog();

    expect(saveButton().disabled).toBe(true);
  });

  it('is disabled and says why while a field is invalid', async () => {
    const wrapper = await openDialog();

    await type(wrapper, 'Agents running at once', '-1');

    expect(saveButton().disabled).toBe(true);
    expect(input(wrapper, 'Agents running at once')!.props('errorMessage')).toBe('A whole number, 0 or more.');
    expect(api.saveTenantSettings).not.toHaveBeenCalled();
  });

  it('sends only what moved, as a partial map', async () => {
    const wrapper = await openDialog();

    await type(wrapper, 'Agents running at once', '2');
    expect(saveButton().disabled).toBe(false);
    saveButton().click();
    await flushPromises();

    expect(api.saveTenantSettings).toHaveBeenCalledWith({ 'wip.maxRunning': 2 });
    expect(api.setConcierge).not.toHaveBeenCalled();
  });

  it('puts a 400 under the field the server names, on that field’s tab', async () => {
    api.saveTenantSettings.mockRejectedValue(new TenantSettingRejected('Too long: at most 7 days.', 'quiet.window'));
    const wrapper = await openDialog();

    await showTab(wrapper, 'sweeps');
    await type(wrapper, 'Quiet team window', '20d');
    await showTab(wrapper, 'admission');
    saveButton().click();
    await flushPromises();

    expect(wrapper.findComponent({ name: 'QTabPanels' }).props('modelValue')).toBe('sweeps');
    expect(api.saveTenantSettings).toHaveBeenCalledWith({ 'quiet.window': '20.00:00:00' });
    expect(input(wrapper, 'Quiet team window')!.props('errorMessage')).toBe('Too long: at most 7 days.');
    expect(saveButton().disabled).toBe(true);
  });

  /** The server's message names the setting by its key; the screen never does. */
  it('shows a 400 under the field with the field’s label, never the setting key', async () => {
    api.saveTenantSettings.mockRejectedValue(
      new TenantSettingRejected('wip.maxRunning must be between 0 and 1000.', 'wip.maxRunning'),
    );
    const wrapper = await openDialog();

    await type(wrapper, 'Agents running at once', '2');
    saveButton().click();
    await flushPromises();

    expect(input(wrapper, 'Agents running at once')!.props('errorMessage')).toBe(
      'Agents running at once must be between 0 and 1000.',
    );
    expect(document.body.textContent).toContain('Agents running at once must be between 0 and 1000.');
    expect(document.body.textContent).not.toContain('wip.maxRunning');
  });

  it('drops every setting key from a lane-limit 400, including one it only mentions', async () => {
    api.saveTenantSettings.mockRejectedValue(
      new TenantSettingRejected(
        "kanban.wipLimits: the 'in-progress' lane's limit is wip.maxRunning; set that instead.",
        'kanban.wipLimits',
      ),
    );
    const wrapper = await openDialog();

    await type(wrapper, 'Agents running at once', '2');
    saveButton().click();
    await flushPromises();

    expect(wrapper.findComponent({ name: 'QTabPanels' }).props('modelValue')).toBe('kanban');
    const text = document.body.textContent ?? '';
    expect(text).toContain(
      "Per-lane limits: the 'in-progress' lane's limit is Agents running at once; set that instead.",
    );
    expect(text).not.toContain('kanban.wipLimits');
    expect(text).not.toContain('wip.maxRunning');
  });
});

describe('durations', () => {
  it('sends a typed 45m as the hh:mm:ss the server takes, and treats 30m as unchanged', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'sweeps');

    await type(wrapper, 'Quiet team window', '30m');
    expect(saveButton().disabled).toBe(true);

    await type(wrapper, 'Quiet team window', '45m');
    saveButton().click();
    await flushPromises();

    expect(api.saveTenantSettings).toHaveBeenCalledWith({ 'quiet.window': '00:45:00' });
  });

  it('refuses a count over the server bound before Save', async () => {
    const wrapper = await openDialog();

    await type(wrapper, 'Agents running at once', '1001');

    expect(input(wrapper, 'Agents running at once')!.props('errorMessage')).toBe('At most 1000.');
    expect(saveButton().disabled).toBe(true);
  });
});

describe('Kanban', () => {
  it('shows In Progress as the Admission limit and saves an advisory lane limit', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'kanban');

    expect(bodyText()).toContain('the Agents running at once, set on Admission');
    const needsYou = wrapper.findAllComponents({ name: 'QInput' }).find((i) => i.attributes('aria-label') === 'Needs You limit')
      ?? wrapper.findAllComponents({ name: 'QInput' }).find((i) => (i.vm.$attrs['aria-label'] as string) === 'Needs You limit');
    expect(needsYou!.props('modelValue')).toBe('3');

    needsYou!.vm.$emit('update:modelValue', '5');
    await flushPromises();
    saveButton().click();
    await flushPromises();

    expect(api.saveTenantSettings).toHaveBeenCalledWith({ 'kanban.wipLimits': { blocked: 5 } });
  });
});

describe('Concierge', () => {
  /** What the Concierge is told is the built-in Concierge prompt, so there is no Prompt here. */
  it('holds the Agent moved from the Concierge dialog, interactive Agents only, and no Prompt', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'concierge');

    expect(select(wrapper, 'Agent')!.props('options')).toEqual(['claude-interactive']);
    expect(select(wrapper, 'Prompt')).toBeUndefined();
    expect(saveButton().disabled).toBe(true);
  });

  it('saves a changed Agent through the Concierge route with the same Save', async () => {
    api.listCatalog.mockResolvedValue({
      agents: [
        { name: 'claude-interactive', mode: 'Interactive' },
        { name: 'codex-interactive', mode: 'Interactive' },
      ],
    });
    const wrapper = await openDialog();
    await showTab(wrapper, 'concierge');

    select(wrapper, 'Agent')!.vm.$emit('update:modelValue', 'codex-interactive');
    await flushPromises();
    saveButton().click();
    await flushPromises();

    expect(api.setConcierge).toHaveBeenCalledWith({ agent: 'codex-interactive' });
    expect(api.saveTenantSettings).not.toHaveBeenCalled();
  });

  /** The Concierge tab also holds this browser's terminal display. */
  it('holds this browser\'s terminal display, saved as it changes and never by Save', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'concierge');
    const display = useTerminalDisplayStore();

    expect(bodyText()).toContain('Saved in this browser only, as you change it.');

    select(wrapper, 'Font family')!.vm.$emit('update:modelValue', 'Consolas, monospace');
    const slider = wrapper.findComponent({ name: 'QSlider' });
    expect(slider.props('min')).toBe(10);
    expect(slider.props('max')).toBe(22);
    slider.vm.$emit('update:modelValue', 18);
    await flushPromises();

    expect(display.fontFamily).toBe('Consolas, monospace');
    expect(display.fontSize).toBe(18);
    expect(saveButton().disabled).toBe(true);
    expect(api.setConcierge).not.toHaveBeenCalled();
    expect(api.saveTenantSettings).not.toHaveBeenCalled();
  });

  it('opens straight on the Concierge tab when asked, as the panel\'s gear does', async () => {
    const wrapper = await mountDialog(TenantSettingsDialog, { initialTab: 'concierge' });
    vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue();
    await flushPromises();

    expect(select(wrapper, 'Font family')).toBeDefined();
    expect(select(wrapper, 'Agent')).toBeDefined();
  });

  /** Opened from the panel it must sit above the terminal, and so must the font menu it teleports. */
  it('lifts itself and the font menu above the Concierge panel', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'concierge');

    expect(document.body.querySelector('.q-dialog.concierge-settings, .concierge-settings .q-dialog__inner, .concierge-settings')).not.toBeNull();
    expect(select(wrapper, 'Font family')!.props('popupContentClass')).toBe('concierge-settings');
  });

  /** Open Agents and Open Skills showed their dialogs BEHIND this one: 6000 under its 7100. */
  it('drops the lift while a dialog it opened covers it, keeping what was typed', async () => {
    const wrapper = await openDialog();
    const lifted = () => document.body.querySelector('.concierge-settings') !== null;
    expect(lifted()).toBe(true);

    await type(wrapper, 'Agents running at once', '7');
    await wrapper.setProps({ covered: true });
    await flushPromises();
    expect(lifted()).toBe(false);
    expect(document.body.querySelector('[data-tenant-settings]')).not.toBeNull();

    await wrapper.setProps({ covered: false });
    await flushPromises();
    expect(lifted()).toBe(true);
    expect(input(wrapper, 'Agents running at once')!.props('modelValue')).toBe('7');
  });
});

describe('System packages', () => {
  it('says what adding one costs, and never shows the key', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'system');

    expect(bodyText()).toContain('Adding one costs a restart, not an image rebuild.');
    expect(bodyText()).not.toContain('system.packages');
    expect(input(wrapper, 'System packages')!.props('modelValue')).toBe('htop');
  });

  it('sends the typed names as a list', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'system');

    await type(wrapper, 'System packages', 'htop, ffmpeg  imagemagick');
    await saveButton().click();
    await flushPromises();

    expect(api.saveTenantSettings).toHaveBeenCalledWith({ 'system.packages': ['htop', 'ffmpeg', 'imagemagick'] });
  });

  it('refuses a name that is not a package name before Save', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'system');

    await type(wrapper, 'System packages', 'htop; rm -rf /');

    expect(saveButton().disabled).toBe(true);
    expect(bodyText()).toContain("'htop;' is not a package name");
  });
});

describe('Spend and Roots', () => {
  it('notes the billing weights, read-only', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'spend');

    expect(bodyText()).toContain('cache reads at 1/10, cache writes at 5/4');
  });

  it('lists the roots read-only with the mount note', async () => {
    const wrapper = await openDialog();
    await showTab(wrapper, 'roots');

    expect(bodyText()).toContain('/mnt/projects');
    expect(bodyText()).toContain('Projects');
    expect(bodyText()).toContain('the container must also mount the path');
    expect(bodyText()).toContain('mounting that path into the container');
    // A root is never an editable field.
    expect(wrapper.findAllComponents({ name: 'QInput' }).some((i) => i.props('modelValue') === '/mnt/projects')).toBe(false);
  });
});

/**
 * EVERY TAB. Every instance-wide setting is visible in the dialog with its source and last change,
 * on every tab rather than Admission alone; the key off screen on every tab; and Save going back
 * to disabled when an edit is undone.
 */
describe('every tab', () => {
  const settingTabs = ['admission', 'spend', 'concierge', 'sweeps', 'kanban'];

  it('shows every setting with a source line, naming who changed a saved one', async () => {
    const withRow = settings();
    const quiet = withRow.settings.find((entry) => entry.name === 'quiet.window')!;
    Object.assign(quiet, { source: 'row', updatedAt: '2026-09-22T09:00:00Z', updatedBy: 'lead@example.com' });
    api.getTenantSettings.mockResolvedValue(normaliseTenantSettings(withRow));

    const wrapper = await openDialog();
    const seen = new Map<string, string>();

    for (const name of settingTabs) {
      await showTab(wrapper, name);
      for (const field of document.body.querySelectorAll('[data-setting]')) {
        const source = field.querySelector('.tenant-setting-source')?.textContent?.trim() ?? '';
        seen.set(field.getAttribute('data-setting')!, source);
      }
    }

    // The lane limits have no [data-setting] box; their source line sits on the Kanban tab.
    const kanbanSource = [...document.body.querySelectorAll('.tenant-setting-source')]
      .filter((line) => line.closest('[data-setting]') === null)
      .map((line) => line.textContent?.trim() ?? '');

    expect([...seen.keys()].sort()).toEqual(
      ['causation.depthLimit', 'concierge.idleTimeout', 'quiet.window', 'resume.maxAutomatic', 'theme.default', 'wip.maxRunning', 'workflow.spendLimit'],
    );
    for (const [name, source] of seen) {
      expect(source, name).toMatch(/^(Set by \S+@\S+|From the host configuration)/);
    }
    expect(seen.get('wip.maxRunning')).toContain('Set by ops@example.com');
    expect(seen.get('quiet.window')).toContain('Set by lead@example.com');
    expect(seen.get('resume.maxAutomatic')).toBe('From the host configuration (default 3)');
    expect(kanbanSource).toEqual(['From the host configuration']);
  });

  it('never shows the key wip.maxRunning on any tab', async () => {
    const wrapper = await openDialog();

    for (const name of [...settingTabs, 'health', 'roots']) {
      await showTab(wrapper, name);
      expect(bodyText(), name).not.toContain('wip.maxRunning');
    }
  });

  it('disables Save again when an edit is put back', async () => {
    const wrapper = await openDialog();

    await type(wrapper, 'Agents running at once', '2');
    expect(saveButton().disabled).toBe(false);

    await type(wrapper, 'Agents running at once', '4');
    expect(saveButton().disabled).toBe(true);
  });
});
