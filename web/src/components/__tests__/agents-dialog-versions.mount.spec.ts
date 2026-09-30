// @vitest-environment happy-dom
//
// ADMIN → AGENTS, THE VERSION LINE. Each built-in row shows its CLI's version, when it last changed
// and who brought it (a start or a person), from `cliVersions` on `GET /api/agents` - the Host's CLI
// version record, the one the operator CLI's `doctor` reads. A version the record lacks reads "version not
// known", never a guess. After the row's update button the line changes in place, from the update's
// own answer, and says so when the version did not change.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const { listCatalog, getAgentAuth, getAgentTools, getTenantSettings, updateAgentCli, notify } = vi.hoisted(() => ({
  notify: vi.fn(),
  listCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
  getAgentTools: vi.fn(),
  getTenantSettings: vi.fn(),
  updateAgentCli: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getAgentAuth,
  getAgentTools,
  getTenantSettings,
  updateAgentCli,
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

import AgentsDialog from '../AgentsDialog.vue';
import type { Agent, AgentUpdateResult, CliVersion } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { stamp } from '../../lib/agentVersions';

const preset = (name: string, fileName: string, builtIn = true): Agent => ({
  name,
  mode: 'Headless',
  builtIn,
  launch: { fileName, arguments: [] },
  updates: { update: [fileName, 'update'] },
  tags: [],
  tagsFromOperator: false,
  buildTags: builtIn ? [] : null,
});

const entry = (agent: string, over: Partial<CliVersion> & Pick<CliVersion, 'cli'>) => ({
  agent,
  version: null,
  updatedAt: null,
  since: '2026-09-25T15:22:00Z',
  updatedBy: null,
  person: null,
  ...over,
});

const claude = entry('claude-headless', {
  cli: 'claude',
  version: '2.1.285 (Claude Code)',
  updatedAt: '2026-09-29T20:17:30Z',
  updatedBy: 'person',
  person: 'ilse@example.test',
});

const grok = entry('grok-headless', {
  cli: 'grok',
  version: 'grok 1.0.44',
  updatedAt: '2026-09-29T16:16:19Z',
  updatedBy: 'start',
});

const copilot = entry('copilot-headless', { cli: 'copilot', version: null });

const codex = entry('codex-headless', { cli: 'codex', version: 'codex-cli 0.157.0' });

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [
      preset('claude-headless', 'claude'),
      preset('grok-headless', 'grok'),
      preset('copilot-headless', 'copilot'),
      preset('codex-headless', 'codex'),
      preset('my-claude', 'claude', false),
    ],
    cliVersions: [claude, grok, copilot, codex, entry('my-claude', { cli: 'claude', version: '2.1.285 (Claude Code)' })],
  });
  getAgentAuth.mockReset();
  getAgentAuth.mockResolvedValue([]);
  getAgentTools.mockReset();
  getAgentTools.mockResolvedValue({ at: null, running: false, presets: [] });
  getTenantSettings.mockReset();
  getTenantSettings.mockResolvedValue({ settings: [], roots: [] });
  updateAgentCli.mockReset();
  notify.mockReset();
});

afterEach(resetBody);

function row(name: string): HTMLElement {
  const found = [...document.body.querySelectorAll<HTMLElement>('.q-item')]
    .find((item) => item.querySelector('.mono')?.textContent?.trim().split(/\s+/)[0] === name);
  if (!found) throw new Error(`no row for ${name}`);
  return found;
}

const versionLine = (name: string) => row(name).querySelector<HTMLElement>('.agent-version-line');

describe('AgentsDialog, the version line', () => {
  it('shows a built-in row its version, when it was last updated and who updated it', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    const byPerson = versionLine('claude-headless')!;
    expect(byPerson.textContent).toContain('2.1.285 (Claude Code)');
    expect(byPerson.textContent).toContain(`updated ${stamp('2026-09-29T20:17:30Z')} by ilse@example.test`);

    const atStart = versionLine('grok-headless')!;
    expect(atStart.textContent).toContain('grok 1.0.44');
    expect(atStart.textContent).toContain(`updated ${stamp('2026-09-29T16:16:19Z')} at a container start`);

    // The record never saw codex change: it says how far back it looked, and invents no update time.
    const steady = versionLine('codex-headless')!;
    expect(steady.textContent).toContain('codex-cli 0.157.0');
    expect(steady.textContent).toContain(`no update recorded since ${stamp('2026-09-25T15:22:00Z')}`);
    expect(steady.textContent).not.toContain('updated ');

    // A custom preset's row carries no version line.
    expect(versionLine('my-claude')).toBeNull();

    wrapper.unmount();
  });

  it('says "version not known" for a CLI whose version the record does not have, and nothing else', async () => {
    listCatalog.mockResolvedValue({
      agents: [preset('copilot-headless', 'copilot'), preset('claude-headless', 'claude')],
      // copilot's version could not be read; claude has no entry at all (a Host with no record).
      cliVersions: [copilot],
    });

    const wrapper = await mountDialog(AgentsDialog);

    for (const name of ['copilot-headless', 'claude-headless']) {
      const line = versionLine(name)!;
      // The icon's ligature, then the words and nothing else: no version, no time.
      expect(line.textContent?.replace('sync', '').trim()).toBe('version not known');
      expect(line.querySelector('.os-text-muted')).not.toBeNull();
    }

    wrapper.unmount();
  });

  it('updates the row in place after the update button, from the update\'s own answer', async () => {
    const result: AgentUpdateResult = {
      agent: 'claude-headless',
      command: 'claude',
      updated: true,
      exitCode: 0,
      versionBefore: '2.1.285 (Claude Code)',
      versionAfter: '2.1.290 (Claude Code)',
      at: '2026-09-30T09:12:00Z',
      detail: '`claude update` updated claude from 2.1.285 to 2.1.290.',
      cliVersion: {
        cli: 'claude',
        version: '2.1.290 (Claude Code)',
        updatedAt: '2026-09-30T09:12:00Z',
        since: '2026-09-25T15:22:00Z',
        updatedBy: 'person',
        person: 'quinn@example.test',
      },
    };
    updateAgentCli.mockResolvedValue(result);

    const wrapper = await mountDialog(AgentsDialog);
    const loads = listCatalog.mock.calls.length;

    row('claude-headless').querySelector<HTMLElement>('[aria-label="Update the CLI claude-headless runs"]')!.click();
    await flushPromises();

    expect(updateAgentCli).toHaveBeenCalledWith('claude-headless');
    expect(notify).toHaveBeenCalledWith({ type: 'positive', message: result.detail });
    // The catalog is read again, and still says the old version: the line comes from the update's
    // own answer, not from waiting on a reload.
    expect(listCatalog.mock.calls.length).toBe(loads + 1);

    const line = versionLine('claude-headless')!;
    expect(line.textContent).toContain('2.1.290 (Claude Code)');
    expect(line.textContent).toContain(`updated ${stamp('2026-09-30T09:12:00Z')} by quinn@example.test`);
    expect(row('claude-headless').querySelector('.agent-version-outcome')?.textContent).toContain(
      `Updated at ${stamp('2026-09-30T09:12:00Z')}: 2.1.285 (Claude Code) → 2.1.290 (Claude Code).`,
    );

    // Another CLI's row is untouched.
    expect(versionLine('grok-headless')!.textContent).toContain('grok 1.0.44');
    expect(row('grok-headless').querySelector('.agent-version-outcome')).toBeNull();

    wrapper.unmount();
  });

  it('says so in the row when the update left the version unchanged', async () => {
    updateAgentCli.mockResolvedValue({
      agent: 'grok-headless',
      command: 'grok',
      updated: true,
      exitCode: 0,
      versionBefore: 'grok 1.0.44',
      versionAfter: 'grok 1.0.44',
      at: '2026-09-30T09:20:00Z',
      detail: '`grok update` ran; grok is already the newest (grok 1.0.44).',
      cliVersion: {
        cli: 'grok',
        version: 'grok 1.0.44',
        updatedAt: '2026-09-29T16:16:19Z',
        since: '2026-09-25T15:22:00Z',
        updatedBy: 'start',
        person: null,
      },
    } satisfies AgentUpdateResult);

    const wrapper = await mountDialog(AgentsDialog);

    row('grok-headless').querySelector<HTMLElement>('[aria-label="Update the CLI grok-headless runs"]')!.click();
    await flushPromises();

    expect(row('grok-headless').querySelector('.agent-version-outcome')?.textContent).toContain(
      `Checked for an update at ${stamp('2026-09-30T09:20:00Z')}: already the newest, the version did not change.`,
    );
    // The last-updated time is still the start that brought it.
    expect(versionLine('grok-headless')!.textContent).toContain(
      `updated ${stamp('2026-09-29T16:16:19Z')} at a container start`,
    );

    wrapper.unmount();
  });
  it('says a failed update that still moved the version failed AND moved it', async () => {
    // npm installed the new version, then its postinstall exited 1.
    updateAgentCli.mockResolvedValue({
      agent: 'claude-headless',
      command: 'claude',
      updated: false,
      exitCode: 1,
      versionBefore: '2.1.286 (Claude Code)',
      versionAfter: '2.1.287 (Claude Code)',
      at: '2026-09-30T05:30:00Z',
      detail: '`npm install -g @anthropic-ai/claude-code@latest` exited 1: npm ERR! postinstall failed',
      cliVersion: {
        cli: 'claude',
        version: '2.1.287 (Claude Code)',
        updatedAt: '2026-09-30T05:30:00Z',
        since: '2026-09-25T15:22:00Z',
        updatedBy: 'person',
        person: 'quinn@example.test',
      },
    } satisfies AgentUpdateResult);

    const wrapper = await mountDialog(AgentsDialog);

    row('claude-headless').querySelector<HTMLElement>('[aria-label="Update the CLI claude-headless runs"]')!.click();
    await flushPromises();

    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'warning' }));
    expect(versionLine('claude-headless')!.textContent).toContain('2.1.287 (Claude Code)');
    const outcome = row('claude-headless').querySelector('.agent-version-outcome')?.textContent ?? '';
    expect(outcome).toContain(
      `The update command failed at ${stamp('2026-09-30T05:30:00Z')} (exit 1); `
        + 'the version moved from 2.1.286 (Claude Code) to 2.1.287 (Claude Code).',
    );
    expect(outcome).not.toContain('not changed');

    wrapper.unmount();
  });

  it('says a failed update that did not move the version failed and left it where it was', async () => {
    updateAgentCli.mockResolvedValue({
      agent: 'grok-headless',
      command: 'grok',
      updated: false,
      exitCode: 2,
      versionBefore: 'grok 1.0.44',
      versionAfter: 'grok 1.0.44',
      at: '2026-09-30T05:40:00Z',
      detail: '`grok update` exited 2: network unreachable',
      cliVersion: {
        cli: 'grok',
        version: 'grok 1.0.44',
        updatedAt: '2026-09-29T16:16:19Z',
        since: '2026-09-25T15:22:00Z',
        updatedBy: 'start',
        person: null,
      },
    } satisfies AgentUpdateResult);

    const wrapper = await mountDialog(AgentsDialog);

    row('grok-headless').querySelector<HTMLElement>('[aria-label="Update the CLI grok-headless runs"]')!.click();
    await flushPromises();

    expect(row('grok-headless').querySelector('.agent-version-outcome')?.textContent).toContain(
      `The update command failed at ${stamp('2026-09-30T05:40:00Z')} (exit 2); the version stayed at grok 1.0.44.`,
    );
    expect(versionLine('grok-headless')!.textContent).toContain(
      `updated ${stamp('2026-09-29T16:16:19Z')} at a container start`,
    );

    wrapper.unmount();
  });

  it('says nothing was run for a preset that declares no update command', async () => {
    // The screen's catalog still listed an update command when it loaded; the Host's no longer
    // does, so the route answers that it ran nothing - no exit code, no versions read.
    updateAgentCli.mockResolvedValue({
      agent: 'codex-headless',
      command: 'codex',
      updated: false,
      exitCode: null,
      versionBefore: null,
      versionAfter: null,
      at: '2026-09-30T05:50:00Z',
      detail: "'codex-headless' declares no update command, so the platform cannot update it.",
      cliVersion: null,
    } satisfies AgentUpdateResult);

    const wrapper = await mountDialog(AgentsDialog);

    row('codex-headless').querySelector<HTMLElement>('[aria-label="Update the CLI codex-headless runs"]')!.click();
    await flushPromises();

    const outcome = row('codex-headless').querySelector('.agent-version-outcome')?.textContent ?? '';
    expect(outcome).toContain(`Nothing was run at ${stamp('2026-09-30T05:50:00Z')}: this preset declares no update command.`);
    expect(outcome).not.toContain('did not complete');
    expect(outcome).not.toContain('version');
    // The version line is what the record said before: nothing ran, so nothing changed it.
    expect(versionLine('codex-headless')!.textContent).toContain('codex-cli 0.157.0');

    wrapper.unmount();
  });

  it('offers no update button on a row whose preset declares no update command', async () => {
    listCatalog.mockResolvedValue({
      agents: [{ ...preset('codex-headless', 'codex'), updates: null }],
      cliVersions: [codex],
    });

    const wrapper = await mountDialog(AgentsDialog);

    expect(row('codex-headless').querySelector('[aria-label="Update the CLI codex-headless runs"]')).toBeNull();

    wrapper.unmount();
  });
});
